using System.Globalization;
using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Jobs;

public enum JobState
{
    /// <summary>Queued and eligible to be claimed once <see cref="JobRecord.RunAfter"/> has passed.</summary>
    Pending = 1,

    /// <summary>Claimed by a worker and covered by a lease. Reclaimable once the lease expires.</summary>
    Running = 2,

    Succeeded = 3,

    /// <summary>Terminal failure: retries exhausted or a non-retryable error.</summary>
    Dead = 4,

    Cancelled = 5,
}

/// <summary>
/// A durable unit of background work. Persisted in PostgreSQL rather than an
/// in-memory queue so that a worker crash cannot lose enqueued work — the
/// requirement that makes AI generation (slow, costly, externally rate-limited)
/// safe to run asynchronously.
///
/// Durability model:
///   * Claiming is a lease, not a dequeue. A crashed worker's job becomes
///     claimable again after <see cref="LeaseExpiresAt"/>, with no broker involved.
///   * Retries use exponential backoff via <see cref="RunAfter"/>.
///   * <see cref="IdempotencyKey"/> is unique per organization and job kind, so
///     a double-submit from the API collapses onto one job rather than
///     double-charging an external provider. Scoped by organization because the
///     key is client-supplied.
///
/// See docs/02-architecture/decisions/ADR-0005-durable-jobs-in-postgresql.md.
/// </summary>
public sealed class JobRecord : AggregateRoot
{
    public const int DefaultMaxAttempts = 3;

    private JobRecord() { } // EF Core

    public string Kind { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public Guid OrganizationId { get; private set; }

    public JobState State { get; private set; }
    public int Attempt { get; private set; }
    public int MaxAttempts { get; private set; }

    /// <summary>Not eligible to be claimed before this instant. Drives both scheduling and retry backoff.</summary>
    public DateTimeOffset RunAfter { get; private set; }

    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public string? LastError { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static JobRecord Enqueue(
        Guid organizationId,
        string kind,
        string payloadJson,
        string idempotencyKey,
        IClock clock,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? delay = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new DomainException("job.kind_required", "A job must have a kind.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException(
                "job.idempotency_key_required",
                "A job must have an idempotency key so duplicate submissions collapse onto one job.");
        }

        if (maxAttempts < 1)
        {
            throw new DomainException("job.invalid_max_attempts", "maxAttempts must be at least 1.");
        }

        return new JobRecord
        {
            OrganizationId = organizationId,
            Kind = kind,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
            State = JobState.Pending,
            Attempt = 0,
            MaxAttempts = maxAttempts,
            RunAfter = clock.UtcNow + (delay ?? TimeSpan.Zero),
            CreatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// Takes a lease on a pending job. Returns false rather than throwing when the
    /// job is not claimable, because losing a claim race is normal operation when
    /// several workers poll the same table.
    ///
    /// <para><b>Not used by the queue.</b> <c>PostgresJobQueue.ClaimAsync</c>
    /// performs the claim in SQL so that eligibility and the resulting
    /// <see cref="LeaseExpiresAt"/> are both anchored to PostgreSQL's clock. A
    /// worker clock running behind the database would otherwise mint a lease that
    /// was already expired when persisted (M00-R4-001). See ADR-0005 amendment
    /// A8.</para>
    ///
    /// <para>This remains the in-memory model of the transition, and is how the
    /// unit tests put a job into <see cref="JobState.Running"/>. It is held to the
    /// SQL implementation by
    /// <c>DurableJobQueueTests.The_sql_claim_agrees_with_the_domain_claim_model</c>,
    /// which fails if the two diverge on state, attempt or owner.</para>
    /// </summary>
    public bool TryClaim(string leaseOwner, TimeSpan leaseDuration, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
        {
            throw new DomainException("job.lease_owner_required", "A lease owner is required to claim a job.");
        }

        var now = clock.UtcNow;

        if (!IsClaimableAt(now))
        {
            return false;
        }

        State = JobState.Running;
        Attempt++;
        LeaseOwner = leaseOwner;
        LeaseExpiresAt = now + leaseDuration;
        Touch(clock);
        return true;
    }

    /// <summary>True when the lease has lapsed and the job is up for reclamation.</summary>
    public bool HasExpiredLease(IClock clock) =>
        State == JobState.Running && LeaseExpiresAt <= clock.UtcNow;

    /// <summary>
    /// True when the attempt budget is spent, so a further claim would exceed
    /// <see cref="MaxAttempts"/>.
    /// </summary>
    public bool IsAttemptBudgetExhausted => Attempt >= MaxAttempts;

    private bool IsClaimableAt(DateTimeOffset now)
    {
        var leaseExpired = State == JobState.Running && LeaseExpiresAt <= now;

        if (State != JobState.Pending && !leaseExpired)
        {
            return false;
        }

        // A worker that dies holding a lease never calls Fail, so lease expiry is
        // the only signal that the attempt was spent. Without this check the
        // claim path would hand a poison job out forever: it would be reclaimed
        // as attempts 4, 5, 6 ... on a job configured for three.
        // Covered by JobRecordTests.Repeated_lease_expiry_exhausts_the_attempt_budget.
        if (IsAttemptBudgetExhausted)
        {
            return false;
        }

        return RunAfter <= now;
    }

    /// <summary>
    /// The message stored on a job dead-lettered for a lease that expired with no
    /// attempts left.
    ///
    /// A template rather than a literal because the dead-letter sweep runs in SQL
    /// (see <c>PostgresJobQueue.ClaimAsync</c>) while
    /// <see cref="FailExhaustedLease"/> models the same transition in memory.
    /// Sharing the template is what stops the two wordings drifting apart: the
    /// infrastructure rewrites <c>{0}</c>/<c>{1}</c> into PostgreSQL
    /// <c>format()</c> placeholders rather than restating the sentence.
    /// </summary>
    public const string ExhaustedLeaseErrorTemplate =
        "Lease expired on attempt {0} of {1} without the worker reporting a result, and no " +
        "attempts remain. The worker most likely terminated mid-execution.";

    /// <summary>Formats <see cref="ExhaustedLeaseErrorTemplate"/> for a given attempt budget.</summary>
    public static string ExhaustedLeaseError(int attempt, int maxAttempts) =>
        string.Format(CultureInfo.InvariantCulture, ExhaustedLeaseErrorTemplate, attempt, maxAttempts);

    /// <summary>
    /// Terminates a job whose lease expired with no attempts left.
    ///
    /// <para><b>Not used by the queue.</b> The claim sweep dead-letters such rows
    /// in SQL so the terminal timestamps come from database time, consistent with
    /// the claim itself (M00-R4-001, ADR-0005 amendment A8). This is the in-memory
    /// model of that transition and is what the unit tests exercise; both produce
    /// <see cref="ExhaustedLeaseErrorTemplate"/>.</para>
    /// </summary>
    public void FailExhaustedLease(IClock clock)
    {
        if (State != JobState.Running)
        {
            throw new DomainException(
                "job.not_running", $"Cannot expire the lease of a job in state {State}.");
        }

        if (!IsAttemptBudgetExhausted)
        {
            throw new DomainException(
                "job.attempts_remain",
                $"Job has used {Attempt} of {MaxAttempts} attempts and may still be reclaimed.");
        }

        State = JobState.Dead;
        CompletedAt = clock.UtcNow;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        LastError = ExhaustedLeaseError(Attempt, MaxAttempts);
        Touch(clock);
    }

    /// <summary>
    /// Asserts that <paramref name="leaseOwner"/> currently holds this job's lease.
    ///
    /// Every state transition a worker performs is fenced by this check. A worker
    /// whose lease expired mid-execution — because the job took longer than the
    /// lease, or the process stalled — may have had its job reclaimed by another
    /// worker. If the stale worker could still complete or fail the job, it would
    /// overwrite the new owner's attempt and, for billable work, mask the fact
    /// that the operation ran twice.
    ///
    /// Covered by JobRecordTests.A_stale_lease_owner_cannot_complete_or_fail_the_job.
    /// </summary>
    private void EnsureLeaseHeldBy(string leaseOwner, string operation, IClock clock)
    {
        if (State != JobState.Running)
        {
            throw new DomainException("job.not_running", $"Cannot {operation} a job in state {State}.");
        }

        if (!string.Equals(LeaseOwner, leaseOwner, StringComparison.Ordinal))
        {
            throw new DomainException(
                "job.lease_not_held",
                $"Lease on job {Id} is held by '{LeaseOwner ?? "(none)"}', not '{leaseOwner}'. " +
                "The lease was most likely reclaimed after it expired.");
        }

        // An expired lease confers no rights even to its original owner. Without
        // this, a worker that stalled past its lease could still write — and if
        // nobody had reclaimed the job yet, the owner string would still match.
        if (LeaseExpiresAt <= clock.UtcNow)
        {
            throw new DomainException(
                "job.lease_expired",
                $"Lease on job {Id} held by '{leaseOwner}' expired at {LeaseExpiresAt:O}. " +
                "Another worker may already be executing this attempt.");
        }
    }

    /// <summary>Extends the lease on a long-running job so it is not reclaimed mid-flight.</summary>
    public void Heartbeat(string leaseOwner, TimeSpan leaseDuration, IClock clock)
    {
        EnsureLeaseHeldBy(leaseOwner, "heartbeat", clock);

        LeaseExpiresAt = clock.UtcNow + leaseDuration;
        Touch(clock);
    }

    public void Succeed(string leaseOwner, IClock clock)
    {
        EnsureLeaseHeldBy(leaseOwner, "complete", clock);

        State = JobState.Succeeded;
        CompletedAt = clock.UtcNow;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        LastError = null;
        Touch(clock);
    }

    /// <summary>
    /// Records a failed attempt. Reschedules with backoff while attempts remain,
    /// otherwise moves to <see cref="JobState.Dead"/>. Pass
    /// <paramref name="retryable"/> = false for errors that cannot succeed on
    /// retry (bad payload, revoked credentials) to avoid burning quota.
    /// </summary>
    public void Fail(string leaseOwner, string error, IClock clock, bool retryable = true)
    {
        EnsureLeaseHeldBy(leaseOwner, "fail", clock);

        LastError = error;
        LeaseOwner = null;
        LeaseExpiresAt = null;

        if (!retryable || Attempt >= MaxAttempts)
        {
            State = JobState.Dead;
            CompletedAt = clock.UtcNow;
        }
        else
        {
            State = JobState.Pending;
            RunAfter = clock.UtcNow + BackoffFor(Attempt);
        }

        Touch(clock);
    }

    public void Cancel(IClock clock)
    {
        if (State is JobState.Succeeded or JobState.Dead or JobState.Cancelled)
        {
            throw new DomainException("job.already_terminal", $"Cannot cancel a job in terminal state {State}.");
        }

        State = JobState.Cancelled;
        CompletedAt = clock.UtcNow;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        Touch(clock);
    }

    /// <summary>Exponential backoff: 2s, 4s, 8s, ... capped at 5 minutes.</summary>
    public static TimeSpan BackoffFor(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Max(attempt, 1)), 300);
        return TimeSpan.FromSeconds(seconds);
    }
}
