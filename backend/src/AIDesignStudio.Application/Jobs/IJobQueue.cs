using AIDesignStudio.Domain.Jobs;

namespace AIDesignStudio.Application.Jobs;

/// <summary>
/// Durable job queue port, backed by the application's own PostgreSQL database.
///
/// Enqueue happens in the same transaction as the state change that requested the
/// work, which removes the "committed the row but lost the job" failure mode that
/// an external broker would introduce. See ADR-0005.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Enqueues work, or returns the existing job when
    /// <paramref name="idempotencyKey"/> has already been used for this
    /// organization and kind. Callers can therefore retry a submission without
    /// risking a duplicate billable provider call.
    ///
    /// Idempotency is scoped to <paramref name="organizationId"/>: keys are
    /// client-supplied, so a global scope would let one tenant's key collide with
    /// another's and return a job belonging to a different organization.
    ///
    /// Does not call SaveChanges — the caller owns the transaction, which is what
    /// makes enqueueing atomic with the state change that requested the work.
    ///
    /// Concurrency: this performs a lookup, so two simultaneous callers can both
    /// miss and both add. The unique index
    /// <c>(organization_id, kind, idempotency_key)</c> is the backstop and the
    /// loser's commit fails with a unique violation. Callers that must survive a
    /// true race should catch it and call <see cref="FindByIdempotencyKeyAsync"/>;
    /// <c>EnqueueAsync</c> cannot do this itself because it does not own the
    /// commit.
    /// </summary>
    Task<JobRecord> EnqueueAsync(
        Guid organizationId,
        string kind,
        string payloadJson,
        string idempotencyKey,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an already-enqueued job by its idempotency key within an
    /// organization. Used to recover the winner's job after losing an enqueue race.
    /// </summary>
    Task<JobRecord?> FindByIdempotencyKeyAsync(
        Guid organizationId,
        string kind,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="maxJobs"/> eligible jobs, taking a lease on each.
    /// Eligible means pending with <c>RunAfter</c> in the past, or running with an
    /// expired lease (the crashed-worker recovery path).
    /// </summary>
    Task<IReadOnlyList<JobRecord>> ClaimAsync(
        string leaseOwner,
        int maxJobs,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a job by id with no organization filter. For worker-internal use
    /// only — an API-facing query must scope by organization.
    /// </summary>
    Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a job succeeded. Requires <paramref name="leaseOwner"/> to still hold
    /// the lease; a worker whose lease expired mid-execution must not overwrite
    /// the state of the attempt that reclaimed it.
    /// </summary>
    Task CompleteAsync(Guid jobId, string leaseOwner, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt. Lease-fenced, as <see cref="CompleteAsync"/>.</summary>
    Task FailAsync(
        Guid jobId,
        string leaseOwner,
        string error,
        bool retryable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease on a job still being worked. Returns false when the lease
    /// has already been lost, so a caller can abandon the work instead of
    /// continuing to run something another worker now owns.
    /// </summary>
    Task<bool> TryHeartbeatAsync(
        Guid jobId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles one job kind. Implementations must be idempotent: a lease can expire
/// mid-flight and the same payload can legitimately be delivered more than once.
/// </summary>
public interface IJobHandler
{
    /// <summary>The <see cref="JobRecord.Kind"/> this handler serves.</summary>
    string Kind { get; }

    Task HandleAsync(JobRecord job, CancellationToken cancellationToken);
}

/// <summary>
/// Well-known job kinds. String constants rather than an enum because the value
/// is persisted and must survive code changes that reorder members.
/// </summary>
public static class JobKinds
{
    public const string GenerateScreen = "screen.generate";
    public const string GenerateVariants = "screen.generate_variants";
    public const string ImportScreenshot = "screen.import_screenshot";
    public const string ParseDesignArtifact = "artifact.parse";
    public const string ApplyDesignSystem = "design_system.apply";
}
