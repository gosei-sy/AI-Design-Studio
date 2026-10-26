using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.SharedKernel;
using AIDesignStudio.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the dispatcher's lease-renewal behaviour with a scripted queue.
///
/// No database: the behaviour under test is how the dispatcher reacts to a
/// heartbeat result, and a fake queue makes each case deterministic and
/// immediate. The database-backed counterparts live in
/// <c>AIDesignStudio.IntegrationTests.JobDispatcherTests</c>.
///
/// The rule being enforced is **fail closed**. M00-R3-001B found that the
/// renewal loop cancelled the handler only when the heartbeat returned false; if
/// the heartbeat *threw* — PostgreSQL unreachable — the renewal task faulted
/// unobserved and the handler carried on doing potentially billable work for a
/// job it could no longer prove it owned.
/// </summary>
public sealed class JobDispatcherLeaseTests
{
    private const string Kind = "test.lease";

    /// <summary>How a scripted heartbeat should respond.</summary>
    private enum HeartbeatBehaviour
    {
        Held,
        Lost,
        Throws,
    }

    /// <summary>
    /// A queue whose heartbeat is scripted, and which records every write the
    /// dispatcher attempts. Recording is the point: the fail-closed rule says no
    /// result may be written once ownership is uncertain.
    /// </summary>
    private sealed class ScriptedQueue(HeartbeatBehaviour behaviour) : IJobQueue
    {
        public int CompleteCalls { get; private set; }

        public int FailCalls { get; private set; }

        public int HeartbeatCalls { get; private set; }

        public Task<JobRecord> EnqueueAsync(
            Guid organizationId, string kind, string payloadJson, string idempotencyKey,
            TimeSpan? delay = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JobRecord?> FindByIdempotencyKeyAsync(
            Guid organizationId, string kind, string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JobRecord>> ClaimAsync(
            string leaseOwner, int maxJobs, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobRecord>>([]);

        public Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JobRecord?>(null);

        public Task CompleteAsync(Guid jobId, string leaseOwner, CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            return Task.CompletedTask;
        }

        public Task FailAsync(
            Guid jobId, string leaseOwner, string error, bool retryable,
            CancellationToken cancellationToken = default)
        {
            FailCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> TryHeartbeatAsync(
            Guid jobId, string leaseOwner, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            HeartbeatCalls++;

            return behaviour switch
            {
                HeartbeatBehaviour.Held => Task.FromResult(true),
                HeartbeatBehaviour.Lost => Task.FromResult(false),
                HeartbeatBehaviour.Throws =>
                    throw new InvalidOperationException("the database is unreachable"),
                _ => throw new ArgumentOutOfRangeException(nameof(behaviour)),
            };
        }
    }

    /// <summary>
    /// Blocks until cancelled, recording how it ended. The dispatcher's contract
    /// is that it will be cancelled, so a handler that runs to completion is the
    /// failure signal.
    /// </summary>
    private sealed class BlockingHandler : IJobHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => JobDispatcherLeaseTests.Kind;

        public Task Started => _started.Task;

        public bool WasCancelled { get; private set; }

        public bool RanToCompletion { get; private set; }

        public async Task HandleAsync(JobRecord job, CancellationToken cancellationToken)
        {
            _started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                RanToCompletion = true;
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }

    /// <summary>A queue that hands out one job once, then nothing.</summary>
    private sealed class OneShotQueue(IJobQueue inner, JobRecord job) : IJobQueue
    {
        private bool _handedOut;

        public Task<JobRecord> EnqueueAsync(
            Guid organizationId, string kind, string payloadJson, string idempotencyKey,
            TimeSpan? delay = null, CancellationToken cancellationToken = default) =>
            inner.EnqueueAsync(organizationId, kind, payloadJson, idempotencyKey, delay, cancellationToken);

        public Task<JobRecord?> FindByIdempotencyKeyAsync(
            Guid organizationId, string kind, string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            inner.FindByIdempotencyKeyAsync(organizationId, kind, idempotencyKey, cancellationToken);

        public Task<IReadOnlyList<JobRecord>> ClaimAsync(
            string leaseOwner, int maxJobs, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            if (_handedOut)
            {
                return Task.FromResult<IReadOnlyList<JobRecord>>([]);
            }

            _handedOut = true;
            return Task.FromResult<IReadOnlyList<JobRecord>>([job]);
        }

        public Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(jobId, cancellationToken);

        public Task CompleteAsync(Guid jobId, string leaseOwner, CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(jobId, leaseOwner, cancellationToken);

        public Task FailAsync(
            Guid jobId, string leaseOwner, string error, bool retryable,
            CancellationToken cancellationToken = default) =>
            inner.FailAsync(jobId, leaseOwner, error, retryable, cancellationToken);

        public Task<bool> TryHeartbeatAsync(
            Guid jobId, string leaseOwner, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            inner.TryHeartbeatAsync(jobId, leaseOwner, leaseDuration, cancellationToken);
    }

    private static JobRecord ClaimedJob(IClock clock)
    {
        var job = JobRecord.Enqueue(Guid.CreateVersion7(), Kind, "{}", "lease-test", clock);
        job.TryClaim("lease-owner", TimeSpan.FromMinutes(5), clock).ShouldBeTrue();
        return job;
    }

    private static (JobDispatcher Dispatcher, ServiceProvider Provider) BuildDispatcher(
        IJobQueue queue, BlockingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(queue);
        services.AddSingleton<IJobHandler>(handler);
        services.AddLogging();

        var provider = services.BuildServiceProvider();

        var options = Options.Create(new JobDispatcherOptions
        {
            BatchSize = 1,
            PollIntervalSeconds = 1,

            // One-second renewal so each case resolves immediately rather than on
            // a production-realistic interval.
            HeartbeatIntervalSeconds = 1,
            LeaseDurationSeconds = 300,
        });

        var dispatcher = new JobDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new StubHostEnvironment(),
            NullLogger<JobDispatcher>.Instance);

        return (dispatcher, provider);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new Xunit.Sdk.XunitException($"Timed out: {because}");
    }

    // -----------------------------------------------------------------------
    // R3-001B — fail closed on both loss and inability to prove ownership
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_heartbeat_reporting_loss_cancels_the_running_handler()
    {
        var clock = new TestClock();
        var handler = new BlockingHandler();
        var scripted = new ScriptedQueue(HeartbeatBehaviour.Lost);
        var queue = new OneShotQueue(scripted, ClaimedJob(clock));

        var (dispatcher, provider) = BuildDispatcher(queue, handler);

        await using (provider)
        {
            await dispatcher.StartAsync(CancellationToken.None);

            try
            {
                await handler.Started;
                await WaitUntilAsync(() => handler.WasCancelled, "the handler should be cancelled");

                handler.RanToCompletion.ShouldBeFalse();
            }
            finally
            {
                await dispatcher.StopAsync(CancellationToken.None);
            }
        }

        scripted.CompleteCalls.ShouldBe(0, "ownership was uncertain, so nothing may be recorded");
        scripted.FailCalls.ShouldBe(0, "ownership was uncertain, so nothing may be recorded");
    }

    /// <summary>
    /// The R3-001B case: the heartbeat cannot be performed at all. A worker that
    /// cannot reach the database cannot know whether its lease expired, so the
    /// safe assumption is that it did.
    /// </summary>
    [Fact]
    public async Task A_heartbeat_that_throws_cancels_the_running_handler()
    {
        var clock = new TestClock();
        var handler = new BlockingHandler();
        var scripted = new ScriptedQueue(HeartbeatBehaviour.Throws);
        var queue = new OneShotQueue(scripted, ClaimedJob(clock));

        var (dispatcher, provider) = BuildDispatcher(queue, handler);

        await using (provider)
        {
            await dispatcher.StartAsync(CancellationToken.None);

            try
            {
                await handler.Started;
                await WaitUntilAsync(
                    () => handler.WasCancelled,
                    "a heartbeat exception must cancel the handler, not just fault the renewal task");

                handler.RanToCompletion.ShouldBeFalse();
                scripted.HeartbeatCalls.ShouldBeGreaterThan(0);
            }
            finally
            {
                await dispatcher.StopAsync(CancellationToken.None);
            }
        }

        scripted.CompleteCalls.ShouldBe(0, "ownership was unprovable, so nothing may be recorded");
        scripted.FailCalls.ShouldBe(
            0, "a failure written after losing provable ownership could overwrite another attempt");
    }

    /// <summary>
    /// Host shutdown must stay clean: the handler is cancelled because the service
    /// is stopping, not because the lease was lost, and no result is written.
    /// </summary>
    [Fact]
    public async Task Shutdown_cancellation_is_not_classified_as_lease_loss()
    {
        var clock = new TestClock();
        var handler = new BlockingHandler();

        // The heartbeat always succeeds, so any cancellation must come from
        // shutdown rather than from renewal.
        var scripted = new ScriptedQueue(HeartbeatBehaviour.Held);
        var queue = new OneShotQueue(scripted, ClaimedJob(clock));

        var (dispatcher, provider) = BuildDispatcher(queue, handler);

        await using (provider)
        {
            await dispatcher.StartAsync(CancellationToken.None);
            await handler.Started;

            // Stopping must complete without throwing.
            await dispatcher.StopAsync(CancellationToken.None);
        }

        handler.WasCancelled.ShouldBeTrue("shutdown cancels the handler");
        handler.RanToCompletion.ShouldBeFalse();

        scripted.CompleteCalls.ShouldBe(0);
        scripted.FailCalls.ShouldBe(
            0, "shutdown must not consume a retry attempt; the lease is left to expire");
    }
}

internal sealed class StubHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "AIDesignStudio.UnitTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
