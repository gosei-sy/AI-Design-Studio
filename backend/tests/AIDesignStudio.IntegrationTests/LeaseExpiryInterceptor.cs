using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Expires a job's stored lease immediately before a chosen SQL statement
/// executes, on a separate connection.
///
/// This is the concurrency seam for proving that lease validity is evaluated at
/// the database write rather than at an earlier application timestamp. The gap
/// between the queue's read and its UPDATE is microseconds wide, so there is no
/// deterministic way to land inside it from the outside; sleeping would be slow
/// and flaky.
///
/// Why it mutates the row rather than advancing a clock: tests that use it drive
/// the queue with a real <see cref="AIDesignStudio.SharedKernel.SystemClock"/>,
/// so the production predicate resolves its instant with
/// <c>clock_timestamp()</c> — the exact expression that ships. Pushing
/// <c>lease_expires_at</c> into the past is what a competing worker or the
/// passage of time would do, and it is visible to that predicate without the
/// test substituting anything.
///
/// Advancing a simulated clock would not work here: the queue binds the
/// simulated instant as a parameter before the call, so changing it afterwards
/// has no effect on the statement already on the wire. That asymmetry is the
/// reason the simulated clock is used only for scheduling determinism and never
/// to prove write-time semantics.
/// </summary>
internal sealed class LeaseExpiryInterceptor(
    string connectionString,
    Guid jobId,
    string whenSqlContains,
    string? mutationSql = null,
    TimeSpan? delayBefore = null) : DbCommandInterceptor
{
    /// <summary>
    /// Optional real delay before the statement reaches the server, during which
    /// a short stored lease expires by the passage of time rather than by being
    /// rewritten.
    ///
    /// This is the only way to distinguish "the predicate resolves its instant at
    /// execution" from "the predicate compares against a timestamp captured
    /// earlier": rewriting the expiry backwards is refused by both, whereas time
    /// simply passing is refused only by the former. Deterministic despite being
    /// a real delay — the delay is longer than the lease by a wide margin, so the
    /// outcome does not depend on timing luck.
    /// </summary>
    private readonly TimeSpan _delayBefore = delayBefore ?? TimeSpan.Zero;

    /// <summary>
    /// What to do to the row in the gap. Defaults to expiring the lease; a test
    /// isolating the owner predicate instead reassigns <c>lease_owner</c> while
    /// leaving the lease live, so only that predicate can refuse the write.
    /// </summary>
    private readonly string _mutationSql = mutationSql ??
        """
        UPDATE app.jobs
        SET lease_expires_at = clock_timestamp() - interval '1 second'
        WHERE id = @id
        """;

    private int _fired;

    /// <summary>True once the lease has been expired, so tests can assert the seam engaged.</summary>
    public bool Fired => Volatile.Read(ref _fired) != 0;

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (ShouldFire(command))
        {
            ExpireLease();
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFire(command))
        {
            await ExpireLeaseAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Fires once only. A second firing would move the row during the assertion
    /// phase and make the outcome depend on statement ordering.
    /// </summary>
    private bool ShouldFire(DbCommand command) =>
        command.CommandText.Contains(whenSqlContains, StringComparison.Ordinal)
        && Interlocked.Exchange(ref _fired, 1) == 0;

    private void ExpireLease() => ExpireLeaseAsync(CancellationToken.None).GetAwaiter().GetResult();

    private async Task ExpireLeaseAsync(CancellationToken cancellationToken)
    {
        if (_delayBefore > TimeSpan.Zero)
        {
            // Let the stored lease lapse naturally.
            await Task.Delay(_delayBefore, cancellationToken);
        }

        if (_mutationSql.Length == 0)
        {
            // Delay only: the lease expires because time passed, not because the
            // row was rewritten.
            return;
        }

        // A separate connection, so this commits independently of the statement
        // the queue is about to run.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(_mutationSql, connection);

        command.Parameters.AddWithValue("id", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
