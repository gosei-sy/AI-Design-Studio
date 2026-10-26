using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace AIDesignStudio.Infrastructure.Jobs;

/// <summary>
/// Durable job queue over the <c>app.jobs</c> table.
///
/// The claim query is the only subtle part, and two things are load-bearing.
///
/// <c>FOR UPDATE SKIP LOCKED</c> lets several workers poll the same table
/// concurrently: each takes a disjoint set of rows and no worker blocks behind
/// another's transaction. Without SKIP LOCKED, N workers would serialise on the
/// first eligible row.
///
/// And the claim is a <i>single statement</i> that both selects eligible rows and
/// writes every field of the lease, with all timestamps taken from PostgreSQL.
/// It used to select in SQL and then mutate through the domain, which derived
/// <c>lease_expires_at</c> from the worker's clock: a worker running behind the
/// database minted a lease that was already expired when it was persisted, and
/// the first heartbeat or completion was refused. The queue's own fencing turned
/// a clock-skew problem into an outage (M00-R4-001, ADR-0005 amendment A8).
/// </summary>
public sealed class PostgresJobQueue(
    AppDbContext db,
    IClock clock,
    ILogger<PostgresJobQueue> logger) : IJobQueue
{
    public async Task<JobRecord> EnqueueAsync(
        Guid organizationId,
        string kind,
        string payloadJson,
        string idempotencyKey,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindByIdempotencyKeyAsync(
            organizationId, kind, idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Job {Kind}/{IdempotencyKey} already enqueued for organization {OrganizationId} as {JobId}; returning existing job",
                kind, idempotencyKey, organizationId, existing.Id);
            return existing;
        }

        var job = JobRecord.Enqueue(organizationId, kind, payloadJson, idempotencyKey, clock, delay: delay);
        db.Jobs.Add(job);

        // Intentionally no SaveChanges here: the caller commits, so enqueueing is
        // atomic with whatever state change requested the work.
        return job;
    }

    public Task<JobRecord?> FindByIdempotencyKeyAsync(
        Guid organizationId,
        string kind,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        db.Jobs.FirstOrDefaultAsync(
            j => j.OrganizationId == organizationId
                 && j.Kind == kind
                 && j.IdempotencyKey == idempotencyKey,
            cancellationToken);

    public async Task<IReadOnlyList<JobRecord>> ClaimAsync(
        string leaseOwner,
        int maxJobs,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (maxJobs <= 0)
        {
            return [];
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimedIds = await ExecuteClaimAsync(leaseOwner, maxJobs, leaseDuration, cancellationToken);
        var deadLettered = await DeadLetterExhaustedAsync(maxJobs, cancellationToken);

        await tx.CommitAsync(cancellationToken);

        // Materialises rows PostgreSQL has already written. Reading after the
        // authoritative mutation is not the read-then-mutate pattern M00-R4-001
        // rejected: every persisted field was set by the UPDATE above, and
        // nothing here is written back.
        var claimed = claimedIds.Count == 0
            ? []
            : await db.Jobs.AsNoTracking()
                .Where(j => claimedIds.Contains(j.Id))
                .ToListAsync(cancellationToken);

        if (claimed.Count > 0 || deadLettered > 0)
        {
            logger.LogInformation(
                "Worker {LeaseOwner} claimed {Count} job(s), dead-lettered {DeadCount}",
                leaseOwner, claimed.Count, deadLettered);
        }

        return claimed;
    }

    /// <summary>
    /// Selects eligible rows and claims them in one statement, so that
    /// eligibility and every persisted field of the claim resolve against the
    /// same PostgreSQL instant.
    ///
    /// <para>The <c>write_time</c> CTE is declared <c>MATERIALIZED</c> for a
    /// reason: <c>clock_timestamp()</c> is volatile, so writing it inline at each
    /// of the four places it is needed would evaluate it four times and the
    /// comparison instant would differ from the stored expiry by however long the
    /// statement takes. Materialising it pins one instant for the
    /// statement.</para>
    ///
    /// <para><c>AND j.attempt &lt; j.max_attempts</c> leaves rows whose budget is
    /// spent for <see cref="DeadLetterExhaustedAsync"/>. The candidate CTE still
    /// locks them, which is harmless and keeps the batch limit meaning what it
    /// says: at most <paramref name="maxJobs"/> rows are considered.</para>
    /// </summary>
    private async Task<List<Guid>> ExecuteClaimAsync(
        string leaseOwner, int maxJobs, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH write_time AS MATERIALIZED (
                SELECT COALESCE(@simulated_now::timestamptz, clock_timestamp()) AS now
            ),
            candidate AS (
                SELECT j.id
                FROM app.jobs j
                WHERE (j.state = 'Pending'
                       AND j.run_after <= (SELECT now FROM write_time))
                   OR (j.state = 'Running'
                       AND j.lease_expires_at <= (SELECT now FROM write_time))
                ORDER BY j.run_after
                LIMIT @max_jobs
                FOR UPDATE SKIP LOCKED
            )
            UPDATE app.jobs j
            SET state = 'Running',
                attempt = j.attempt + 1,
                lease_owner = @lease_owner,
                lease_expires_at = (SELECT now FROM write_time)
                                   + make_interval(secs => @lease_seconds),
                updated_at = (SELECT now FROM write_time)
            WHERE j.id IN (SELECT id FROM candidate)
              AND j.attempt < j.max_attempts
            RETURNING j.id
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(SimulatedNowParameter());
        command.Parameters.AddWithValue("max_jobs", maxJobs);
        command.Parameters.AddWithValue("lease_owner", leaseOwner);
        command.Parameters.AddWithValue("lease_seconds", leaseDuration.TotalSeconds);

        var ids = new List<Guid>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>
    /// Terminates rows whose lease lapsed with no attempts left — a crash loop:
    /// the worker died without reporting a result, every time. This is the only
    /// place it can happen, because a dead process never calls
    /// <see cref="FailAsync"/>. Without it the claim sweep would hand the same
    /// poison job out forever.
    ///
    /// <para>In SQL, and against the same database clock as the claim, so a
    /// skewed worker cannot decide a lease has lapsed when the database says
    /// otherwise. The message comes from
    /// <see cref="JobRecord.ExhaustedLeaseErrorTemplate"/>, rewritten into
    /// <c>format()</c> placeholders, so the wording has one source.</para>
    /// </summary>
    private async Task<int> DeadLetterExhaustedAsync(int maxJobs, CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH write_time AS MATERIALIZED (
                SELECT COALESCE(@simulated_now::timestamptz, clock_timestamp()) AS now
            ),
            exhausted AS (
                SELECT j.id
                FROM app.jobs j
                WHERE j.state = 'Running'
                  AND j.lease_expires_at <= (SELECT now FROM write_time)
                  AND j.attempt >= j.max_attempts
                ORDER BY j.run_after
                LIMIT @max_jobs
                FOR UPDATE SKIP LOCKED
            )
            UPDATE app.jobs j
            SET state = 'Dead',
                completed_at = (SELECT now FROM write_time),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = format(@error_template, j.attempt, j.max_attempts),
                updated_at = (SELECT now FROM write_time)
            WHERE j.id IN (SELECT id FROM exhausted)
            RETURNING j.id, j.kind, j.attempt, j.max_attempts
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(SimulatedNowParameter());
        command.Parameters.AddWithValue("max_jobs", maxJobs);
        command.Parameters.AddWithValue("error_template", ExhaustedLeaseErrorSqlTemplate);

        var count = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            count++;

            logger.LogError(
                "Job {JobId} ({Kind}) dead-lettered: lease expired on attempt {Attempt}/{MaxAttempts} " +
                "with no attempts remaining",
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
        }

        return count;
    }

    /// <summary>
    /// <see cref="JobRecord.ExhaustedLeaseErrorTemplate"/> with .NET placeholders
    /// rewritten as PostgreSQL positional ones, so the sentence is not restated
    /// here and cannot drift from the domain's.
    /// </summary>
    private static readonly string ExhaustedLeaseErrorSqlTemplate =
        JobRecord.ExhaustedLeaseErrorTemplate
            .Replace("{0}", "%1$s", StringComparison.Ordinal)
            .Replace("{1}", "%2$s", StringComparison.Ordinal);

    /// <summary>
    /// A command on the context's connection and ambient transaction.
    ///
    /// Raw ADO rather than <c>FromSql</c> because these statements are
    /// data-modifying CTEs, which PostgreSQL requires at the top level, and EF
    /// wraps a <c>FromSql</c> query in a subselect to project its columns.
    /// </summary>
    private NpgsqlCommand CreateCommand(string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction(),
        };

        return command;
    }

    /// <summary>
    /// Binds the simulated instant, or NULL so <c>COALESCE</c> falls through to
    /// <c>clock_timestamp()</c>. Explicitly typed because a null parameter
    /// carries no type Npgsql can infer.
    /// </summary>
    private NpgsqlParameter SimulatedNowParameter() =>
        new("simulated_now", NpgsqlDbType.TimestampTz)
        {
            Value = SimulatedWriteTime ?? (object)DBNull.Value,
        };

    /// <summary>
    /// Reads a job by id with no organization filter. For worker-internal and
    /// diagnostic use only — an API-facing query must scope by organization.
    ///
    /// Untracked, because the worker transitions below write through conditional
    /// SQL that the change tracker does not see. A tracked read would return
    /// whatever this context loaded earlier and silently hide the committed row.
    /// </summary>
    public Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    // -----------------------------------------------------------------------
    // Worker-initiated transitions
    //
    // Each computes its outcome from the domain and writes it with a *single
    // conditional UPDATE* whose WHERE clause re-checks the lease.
    //
    // Two defects shaped this code, both found by review:
    //
    //   M00-RR-001 — validating in memory and then saving by primary key left
    //   the original interleaving intact: A validates, the lease expires, B
    //   reclaims, A saves by id and overwrites B's attempt.
    //
    //   M00-R3-001 — the first fix added the predicate but compared against an
    //   application timestamp captured *before* an await. A lease could expire
    //   between that capture and the UPDATE, and the stale timestamp still
    //   satisfied `lease_expires_at > @now`. Fail-open.
    //
    // Lease validity is therefore evaluated by PostgreSQL at the instant it
    // executes the statement, via WriteTimeNow() below. Zero rows affected means
    // the lease was lost, and the caller is told so.
    //
    // Entities are loaded untracked, so EF cannot flush a tracked copy and
    // reintroduce the unconditional write-by-primary-key.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The instant a time-dependent predicate is evaluated against, expressed so
    /// that PostgreSQL resolves it while executing the statement.
    ///
    /// <c>clock_timestamp()</c> rather than <c>now()</c> deliberately:
    /// <c>now()</c> is the transaction start time, which would reintroduce the
    /// very gap this exists to close when a statement runs late in a transaction.
    ///
    /// A simulated clock substitutes its instant so tests are deterministic while
    /// exercising this same predicate. Production always takes the server clock,
    /// because <see cref="SystemClock"/> is not an <see cref="ISimulatedClock"/>.
    /// See ADR-0005 amendment A6.
    /// </summary>
    private DateTimeOffset? SimulatedWriteTime =>
        clock is ISimulatedClock ? clock.UtcNow : null;

    public async Task CompleteAsync(
        Guid jobId, string leaseOwner, CancellationToken cancellationToken = default)
    {
        var job = await LoadForTransitionAsync(jobId, cancellationToken);

        // Lets the domain reject an illegal transition before any SQL runs, so
        // callers get a precise error rather than a bare "lease lost". This is a
        // fast path only — the predicate below is the authority.
        job.Succeed(leaseOwner, clock);

        var simulatedNow = SimulatedWriteTime;

        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE app.jobs
            SET state = 'Succeeded',
                completed_at = COALESCE({simulatedNow}::timestamptz, clock_timestamp()),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = NULL,
                updated_at = COALESCE({simulatedNow}::timestamptz, clock_timestamp())
            WHERE id = {jobId}
              AND state = 'Running'
              AND lease_owner = {leaseOwner}
              AND lease_expires_at > COALESCE({simulatedNow}::timestamptz, clock_timestamp())
            """, cancellationToken);

        EnsureLeaseWasStillHeld(rows, jobId, leaseOwner, "complete");
    }

    public async Task FailAsync(
        Guid jobId,
        string leaseOwner,
        string error,
        bool retryable,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadForTransitionAsync(jobId, cancellationToken);

        // The domain owns backoff and the dead/pending decision; this computes
        // the outcome, and the UPDATE below persists it atomically.
        job.Fail(leaseOwner, Truncate(error, 4000), clock, retryable);

        var state = job.State.ToString();
        var simulatedNow = SimulatedWriteTime;

        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE app.jobs
            SET state = {state},
                run_after = {job.RunAfter},
                last_error = {job.LastError},
                completed_at = {job.CompletedAt},
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = COALESCE({simulatedNow}::timestamptz, clock_timestamp())
            WHERE id = {jobId}
              AND state = 'Running'
              AND lease_owner = {leaseOwner}
              AND lease_expires_at > COALESCE({simulatedNow}::timestamptz, clock_timestamp())
            """, cancellationToken);

        EnsureLeaseWasStillHeld(rows, jobId, leaseOwner, "fail");

        logger.LogWarning(
            "Job {JobId} ({Kind}) failed on attempt {Attempt}/{MaxAttempts}; new state {State}",
            job.Id, job.Kind, job.Attempt, job.MaxAttempts, job.State);
    }

    public async Task<bool> TryHeartbeatAsync(
        Guid jobId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var simulatedNow = SimulatedWriteTime;

        // No read first: the conditional UPDATE is the check. A read would only
        // add a window between deciding and writing.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE app.jobs
            SET lease_expires_at =
                    COALESCE({simulatedNow}::timestamptz, clock_timestamp()) + {leaseDuration},
                updated_at = COALESCE({simulatedNow}::timestamptz, clock_timestamp())
            WHERE id = {jobId}
              AND state = 'Running'
              AND lease_owner = {leaseOwner}
              AND lease_expires_at > COALESCE({simulatedNow}::timestamptz, clock_timestamp())
            """, cancellationToken);

        if (rows == 0)
        {
            logger.LogWarning(
                "Worker {LeaseOwner} no longer holds the lease on job {JobId}; renewal refused",
                leaseOwner, jobId);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Translates a zero-row conditional update into the domain's lease error.
    /// Reached when the lease was valid at read time and gone by write time —
    /// the interleaving this design exists to make impossible to miss.
    /// </summary>
    private void EnsureLeaseWasStillHeld(int rows, Guid jobId, string leaseOwner, string operation)
    {
        if (rows > 0)
        {
            return;
        }

        logger.LogWarning(
            "Worker {LeaseOwner} could not {Operation} job {JobId}: the lease was lost between " +
            "validation and persistence", leaseOwner, operation, jobId);

        throw new DomainException(
            "job.lease_not_held",
            $"Lease on job {jobId} was no longer held by '{leaseOwner}' when the {operation} was " +
            "persisted. Another worker has reclaimed it.");
    }

    /// <summary>
    /// Loads a job for the purpose of computing a transition.
    ///
    /// <c>AsNoTracking</c> matters for two reasons. It bypasses the identity map,
    /// so this reads the row as it is *now* rather than returning a snapshot this
    /// context loaded earlier — a worker that claimed the job minutes ago would
    /// otherwise validate against its own stale copy. And an untracked entity
    /// cannot be flushed by a later <c>SaveChanges</c> on this context, which
    /// would reintroduce the unconditional write-by-primary-key that M00-RR-001
    /// identified.
    /// </summary>
    private async Task<JobRecord> LoadForTransitionAsync(Guid jobId, CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
        ?? throw new DomainException("job.not_found", $"Job {jobId} does not exist.");

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
