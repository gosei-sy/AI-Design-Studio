using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Infrastructure.Jobs;
using AIDesignStudio.SharedKernel;
using AIDesignStudio.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Covers the dispatcher against a real database and a real queue.
///
/// The behaviour that matters here is what happens to a *running handler* when
/// the lease is lost. M00-RR-001 found that the renewal loop merely returned, so
/// the handler carried on doing potentially billable work for a job another
/// worker owned, and the loss only surfaced when completion was refused — after
/// the provider had been called twice.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobDispatcherTests(DatabaseFixture fixture)
{
    private const string Kind = "test.handler";

    private async Task RequireEmptyJobTableAsync()
    {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM app.jobs", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A handler that reports when it started, blocks until cancelled, and
    /// records whether it was cancelled or allowed to run to completion.
    /// </summary>
    private sealed class BlockingHandler : IJobHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => JobDispatcherTests.Kind;

        public Task Started => _started.Task;

        public bool WasCancelled { get; private set; }

        public bool RanToCompletion { get; private set; }

        public async Task HandleAsync(JobRecord job, CancellationToken cancellationToken)
        {
            _started.TrySetResult();

            try
            {
                // Stands in for a long provider call. Only cancellation ends it.
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

    private ServiceProvider BuildWorkerServices(BlockingHandler handler, IClock clock)
    {
        var services = new ServiceCollection();

        services.AddSingleton(clock);
        services.AddSingleton<IJobHandler>(handler);
        services.AddDbContext<Infrastructure.Persistence.AppDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());

        // System-scoped, exactly as the real worker is
        // (AddInfrastructure(OrganizationScoping.System), ADR-0009). Without
        // this the context defaults to the closed scope, the organization query
        // filter on jobs matches nothing, and the dispatcher waits forever for a
        // queue it cannot see — which is how these two tests failed when the
        // filter was introduced. The failure mode is worth remembering: a
        // forgotten scope makes a worker silently idle, not loud.
        services.AddSingleton<Application.Organizations.IOrganizationScope>(
            Infrastructure.Organizations.SystemOrganizationScope.Instance);
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The RR-001 correction, end to end: the renewal loop must cancel the
    /// handler when it discovers the lease is gone.
    ///
    /// Deterministic rather than timing-dependent — the handler blocks until
    /// cancelled, and the test reclaims the job from a second worker as soon as
    /// the handler has started.
    /// </summary>
    [Fact]
    public async Task Losing_the_lease_mid_execution_cancels_the_running_handler()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var handler = new BlockingHandler();
        Guid jobId;

        await using (var db = fixture.CreateContext())
        {
            var job = JobRecord.Enqueue(
                organizationId, Kind, "{}", $"dispatch-{Guid.CreateVersion7():N}", clock);

            db.Jobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        await using var provider = BuildWorkerServices(handler, clock);

        var options = Options.Create(new JobDispatcherOptions
        {
            BatchSize = 1,
            PollIntervalSeconds = 1,
            LeaseDurationSeconds = 60,

            // Renews once a second, so the loss is discovered promptly without
            // the test having to wait on a realistic interval.
            HeartbeatIntervalSeconds = 1,
        });

        var dispatcher = new JobDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new TestHostEnvironment(),
            NullLogger<JobDispatcher>.Instance);

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(stopping.Token);

        try
        {
            // Wait for the handler to actually be executing.
            await handler.Started.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            // Another worker reclaims the job. Advancing the clock past the lease
            // is what makes it claimable; the dispatcher's next renewal then finds
            // the lease held by someone else.
            clock.Advance(TimeSpan.FromSeconds(90));

            await using var otherDb = fixture.CreateContext();
            var otherWorker = new PostgresJobQueue(otherDb, clock, NullLogger<PostgresJobQueue>.Instance);

            var reclaimed = await otherWorker.ClaimAsync(
                "intruder", 10, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

            reclaimed.ShouldHaveSingleItem().Id.ShouldBe(jobId);

            // The renewal loop must now cancel the handler.
            await WaitUntilAsync(
                () => handler.WasCancelled,
                TimeSpan.FromSeconds(20),
                "the handler should have been cancelled once the lease was lost");

            handler.WasCancelled.ShouldBeTrue();
            handler.RanToCompletion.ShouldBeFalse(
                "a handler must not finish work for a job another worker owns");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        // The intruder's attempt must be intact: the dispatcher did not write.
        await using var reader = fixture.CreateContext();
        var queue = new PostgresJobQueue(reader, clock, NullLogger<PostgresJobQueue>.Instance);
        var final = await queue.GetAsync(jobId, TestContext.Current.CancellationToken);

        final!.LeaseOwner.ShouldBe("intruder");
        final.State.ShouldBe(JobState.Running);
        final.CompletedAt.ShouldBeNull();
    }

    /// <summary>
    /// An unknown job kind cannot succeed on retry, so it must die at once rather
    /// than loop. Also confirms the dispatcher's own write path is lease-fenced
    /// and works for the legitimate owner.
    /// </summary>
    [Fact]
    public async Task A_job_with_no_registered_handler_fails_terminally()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var handler = new BlockingHandler();
        Guid jobId;

        await using (var db = fixture.CreateContext())
        {
            var job = JobRecord.Enqueue(
                organizationId, "kind.with.no.handler", "{}",
                $"unhandled-{Guid.CreateVersion7():N}", clock);

            db.Jobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        await using var provider = BuildWorkerServices(handler, clock);

        var dispatcher = new JobDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new JobDispatcherOptions { BatchSize = 1, PollIntervalSeconds = 1 }),
            new TestHostEnvironment(),
            NullLogger<JobDispatcher>.Instance);

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(stopping.Token);

        try
        {
            await using var reader = fixture.CreateContext();
            var queue = new PostgresJobQueue(reader, clock, NullLogger<PostgresJobQueue>.Instance);

            await WaitUntilAsync(
                async () =>
                {
                    var job = await queue.GetAsync(jobId, TestContext.Current.CancellationToken);
                    return job?.State == JobState.Dead;
                },
                TimeSpan.FromSeconds(20),
                "an unhandled job kind should fail terminally");

            var final = await queue.GetAsync(jobId, TestContext.Current.CancellationToken);
            final!.State.ShouldBe(JobState.Dead);
            final.Attempt.ShouldBe(1, "a non-retryable failure must not burn further attempts");
            final.LastError!.ShouldContain("No handler registered");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout, because);

    /// <summary>
    /// Polls a condition to a deadline. Used instead of a fixed delay so the test
    /// completes as soon as the dispatcher reacts, and fails with a readable
    /// message rather than a mysterious assertion if it never does.
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {timeout.TotalSeconds:0}s: {because}");
    }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "AIDesignStudio.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
