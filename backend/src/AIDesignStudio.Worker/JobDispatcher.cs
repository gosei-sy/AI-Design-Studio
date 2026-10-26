using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Application.Providers;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Worker;

public sealed class JobDispatcherOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Jobs claimed per poll.
    ///
    /// Default 1. Jobs in a batch are executed sequentially, so a batch of N
    /// leases work that will not start for up to (N-1) handler runtimes. With a
    /// provider call allowed 180s, a batch of 5 could see the last job's lease
    /// lapse before it was ever started — it would be reclaimed by another worker
    /// while this one still intended to run it. Raise this only alongside
    /// concurrent execution.
    /// </summary>
    public int BatchSize { get; set; } = 1;

    /// <summary>Idle wait between polls when the last poll found nothing.</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Lease duration. Must exceed the longest expected handler runtime; the
    /// dispatcher renews it while a handler runs, so this bounds how long a
    /// *crashed* worker's job stays unavailable, not how long a handler may take.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 300;

    /// <summary>
    /// How often the lease is renewed while a handler runs. Must be comfortably
    /// below <see cref="LeaseDurationSeconds"/>.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Polls the durable job table and dispatches claimed jobs to handlers.
///
/// Polling rather than push: the queue lives in PostgreSQL, work is slow
/// (provider calls take seconds to minutes) and low-volume, so a two-second poll
/// adds negligible latency and removes the need for a broker. If volume ever
/// justifies it, <c>LISTEN/NOTIFY</c> can wake the loop without changing the
/// durability model. See ADR-0005.
///
/// Failures are classified before retrying: a <see cref="ProviderException"/>
/// carries the provider's own retryability, so a credential error is not retried
/// three times against a billable endpoint.
/// </summary>
public sealed class JobDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<JobDispatcherOptions> options,
    IHostEnvironment environment,
    ILogger<JobDispatcher> logger) : BackgroundService
{
    private readonly JobDispatcherOptions _options = options.Value;

    /// <summary>
    /// Identifies this worker instance in the lease column. Includes the machine
    /// name and process id so an orphaned lease can be traced to a real process.
    ///
    /// Truncated to the <c>lease_owner</c> column width rather than sliced with a
    /// fixed range: <c>[..64]</c> throws when the composed string is *shorter*
    /// than 64 characters, which is the normal case on a host with a short name
    /// and would have crashed the dispatcher at construction. Found by
    /// JobDispatcherTests.
    /// </summary>
    private readonly string _leaseOwner = BuildLeaseOwner();

    private const int LeaseOwnerMaxLength = 128;

    private static string BuildLeaseOwner()
    {
        var identity = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";

        return identity.Length <= LeaseOwnerMaxLength
            ? identity
            : identity[..LeaseOwnerMaxLength];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Job dispatcher starting as {LeaseOwner} in {Environment}",
            _leaseOwner, environment.EnvironmentName);

        var leaseDuration = TimeSpan.FromSeconds(_options.LeaseDurationSeconds);
        var pollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;

            try
            {
                claimed = await PollOnceAsync(leaseDuration, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop itself must survive anything, including the database
                // being unreachable. Back off and keep going.
                logger.LogError(ex, "Job dispatcher poll failed; backing off");
                claimed = 0;
            }

            if (claimed == 0)
            {
                try
                {
                    await Task.Delay(pollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Job dispatcher {LeaseOwner} stopped", _leaseOwner);
    }

    private async Task<int> PollOnceAsync(TimeSpan leaseDuration, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        var jobs = await queue.ClaimAsync(_leaseOwner, _options.BatchSize, leaseDuration, stoppingToken);

        foreach (var job in jobs)
        {
            await ExecuteJobAsync(job, stoppingToken);
        }

        return jobs.Count;
    }

    /// <summary>
    /// Runs one job in its own scope so a handler's DbContext and any per-request
    /// state cannot leak between jobs.
    /// </summary>
    private async Task ExecuteJobAsync(JobRecord job, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        var handler = scope.ServiceProvider
            .GetServices<IJobHandler>()
            .FirstOrDefault(h => h.Kind == job.Kind);

        if (handler is null)
        {
            // An unknown kind cannot succeed on retry: the deployed binary has no
            // handler for it. Fail terminally so it surfaces instead of looping.
            logger.LogError("No handler registered for job kind {Kind} (job {JobId})", job.Kind, job.Id);

            await queue.FailAsync(
                job.Id, _leaseOwner, $"No handler registered for kind '{job.Kind}'.",
                retryable: false, stoppingToken);

            return;
        }

        logger.LogInformation(
            "Executing job {JobId} ({Kind}) attempt {Attempt}/{MaxAttempts}",
            job.Id, job.Kind, job.Attempt, job.MaxAttempts);

        // Renews the lease while the handler runs, so a legitimately slow provider
        // call does not look like a crashed worker. The renewal loop cancels the
        // handler the moment lease ownership becomes uncertain — whether the
        // heartbeat was refused or could not be performed at all — so the handler
        // stops instead of continuing billable work it may no longer own.
        using var handlerScope = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var lease = new LeaseState();
        var renewal = RenewLeaseAsync(job.Id, handlerScope, lease, stoppingToken);

        try
        {
            await handler.HandleAsync(job, handlerScope.Token);

            if (lease.IsOwnershipUncertain)
            {
                // The handler returned, but renewal already determined we may not
                // own the job. Writing a completion could overwrite another
                // worker's attempt, and the predicate would refuse it anyway.
                logger.LogWarning(
                    "Job {JobId} handler finished but lease ownership is uncertain ({Reason}); " +
                    "not recording a result", job.Id, lease.Reason);

                return;
            }

            await queue.CompleteAsync(job.Id, _leaseOwner, stoppingToken);

            logger.LogInformation("Job {JobId} ({Kind}) succeeded", job.Id, job.Kind);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Coordinated shutdown, not failure and not lease loss. Leave the
            // lease to expire so another worker picks the job up rather than
            // consuming a retry attempt.
            logger.LogInformation("Job {JobId} interrupted by shutdown; lease will expire", job.Id);
            throw;
        }
        catch (OperationCanceledException) when (handlerScope.IsCancellationRequested)
        {
            // Cancelled by the renewal loop. Ownership is uncertain, so nothing is
            // recorded: see LeaseState for why that is the fail-closed choice.
            logger.LogWarning(
                "Job {JobId} abandoned mid-execution: {Reason}", job.Id, lease.Reason);
        }
        catch (ProviderException ex)
        {
            logger.LogWarning(
                ex,
                "Job {JobId} failed with provider error {Code} (retryable: {IsRetryable})",
                job.Id, ex.Code, ex.IsRetryable);

            await RecordFailureAsync(
                queue, job, lease, $"{ex.Code}: {ex.Message}", ex.IsRetryable, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) threw an unhandled exception", job.Id, job.Kind);
            await RecordFailureAsync(queue, job, lease, ex.Message, retryable: true, stoppingToken);
        }
        finally
        {
            await handlerScope.CancelAsync();
            await ObserveRenewalAsync(renewal, job.Id);
        }
    }

    /// <summary>
    /// Tracks whether this worker can still prove it owns the job's lease.
    ///
    /// Set by the renewal loop, read by the execution path. Once ownership is
    /// uncertain the dispatcher records nothing: a completion or failure written
    /// from here could overwrite the attempt of the worker that reclaimed the job,
    /// and for billable work it would conceal that the provider ran twice. The
    /// queue's predicate would refuse the write in any case, but not attempting it
    /// keeps the intent explicit rather than relying on the fence to catch us.
    /// </summary>
    private sealed class LeaseState
    {
        private volatile string? _reason;

        public bool IsOwnershipUncertain => _reason is not null;

        public string Reason => _reason ?? "lease held";

        public void MarkUncertain(string reason) => _reason ??= reason;
    }

    /// <summary>
    /// Awaits the renewal task so its outcome is observed rather than discarded.
    ///
    /// Cancellation is expected once the handler returns. Anything else is a bug
    /// in the renewal loop itself: it is logged rather than rethrown, because this
    /// runs in a finally block and throwing here would mask the real failure.
    /// </summary>
    private async Task ObserveRenewalAsync(Task renewal, Guid jobId)
    {
        try
        {
            await renewal;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop is cancelled once the handler returns.
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Lease renewal for job {JobId} faulted unexpectedly", jobId);
        }
    }

    /// <summary>
    /// Records a failure, unless lease ownership is uncertain.
    ///
    /// If the lease lapsed mid-execution, another worker now owns the job and the
    /// queue's predicate rejects the write. That is the fence working, so it is
    /// logged rather than rethrown — rethrowing would bubble a second error out of
    /// a failure path.
    /// </summary>
    private async Task RecordFailureAsync(
        IJobQueue queue,
        JobRecord job,
        LeaseState lease,
        string error,
        bool retryable,
        CancellationToken cancellationToken)
    {
        if (lease.IsOwnershipUncertain)
        {
            logger.LogWarning(
                "Not recording the failure of job {JobId}: lease ownership is uncertain ({Reason})",
                job.Id, lease.Reason);

            return;
        }

        try
        {
            await queue.FailAsync(job.Id, _leaseOwner, error, retryable, cancellationToken);
        }
        catch (DomainException ex) when (
            ex.Code is "job.lease_not_held" or "job.lease_expired" or "job.not_running")
        {
            logger.LogWarning(
                "Could not record the failure of job {JobId}: {Reason}. Another worker owns it now.",
                job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Renews the lease while the handler runs, and fails closed.
    ///
    /// Three outcomes end the loop, and two of them cancel the handler:
    ///
    ///   * The heartbeat is refused (the lease is held by someone else, or has
    ///     expired) — ownership is lost, so cancel.
    ///   * The heartbeat cannot be performed at all, e.g. PostgreSQL is
    ///     unreachable — ownership is *unprovable*, so cancel. Letting the handler
    ///     continue here was M00-R3-001B: the renewal task faulted, nobody
    ///     observed it, and the handler kept doing billable work until it
    ///     returned.
    ///   * The handler finished, or the host is shutting down — cancel is already
    ///     in progress, so just return.
    ///
    /// Inability to prove ownership is treated exactly like losing it. A worker
    /// that cannot reach the database cannot know whether its lease expired, and
    /// the safe assumption is that it did.
    /// </summary>
    private async Task RenewLeaseAsync(
        Guid jobId,
        CancellationTokenSource handlerScope,
        LeaseState lease,
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds);
        var leaseDuration = TimeSpan.FromSeconds(_options.LeaseDurationSeconds);

        while (!handlerScope.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, handlerScope.Token);
            }
            catch (OperationCanceledException)
            {
                // The handler returned or the host is stopping. Not lease loss.
                return;
            }

            bool stillHeld;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

                stillHeld = await queue.TryHeartbeatAsync(
                    jobId, _leaseOwner, leaseDuration, handlerScope.Token);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested || handlerScope.IsCancellationRequested)
            {
                // Coordinated shutdown, or the handler finished while the
                // heartbeat was in flight. Neither is lease loss.
                return;
            }
            catch (Exception ex)
            {
                lease.MarkUncertain(
                    $"the lease could not be renewed ({ex.GetType().Name}: {ex.Message})");

                logger.LogError(
                    ex,
                    "Could not renew the lease on job {JobId}; cancelling the handler because " +
                    "ownership can no longer be proven", jobId);

                await handlerScope.CancelAsync();
                return;
            }

            if (!stillHeld)
            {
                lease.MarkUncertain("the lease is no longer held");

                logger.LogError(
                    "Lost the lease on job {JobId} mid-execution; cancelling the handler so it does " +
                    "not continue work another worker now owns", jobId);

                await handlerScope.CancelAsync();
                return;
            }
        }
    }
}
