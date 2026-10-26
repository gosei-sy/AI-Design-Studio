using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Infrastructure.Jobs;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Exercises the durable job queue against a real PostgreSQL instance.
///
/// The behaviours proven here cannot be proven with an in-memory provider,
/// because they depend on PostgreSQL row locking: concurrent workers taking
/// disjoint work via <c>FOR UPDATE SKIP LOCKED</c>, and leases surviving a
/// simulated worker crash. This is the evidence for SPIKE-06.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DurableJobQueueTests(DatabaseFixture fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Skips when no database is available, and otherwise empties the jobs table.
    ///
    /// The claim query is intentionally global — a worker claims whatever is due,
    /// not whatever a particular test enqueued — so tests that assert on claimed
    /// counts need an empty table to be meaningful. Tests in this collection run
    /// sequentially, so clearing here is safe.
    /// </summary>
    private async Task RequireEmptyJobTableAsync()
    {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM app.jobs", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds a queue over its own DbContext, so that each queue instance in a
    /// test behaves like a separate worker process with its own change tracker.
    /// </summary>
    private (PostgresJobQueue Queue, Infrastructure.Persistence.AppDbContext Db) NewWorker(IClock clock)
    {
        var db = fixture.CreateContext();
        return (new PostgresJobQueue(db, clock, NullLogger<PostgresJobQueue>.Instance), db);
    }

    private static string UniqueKey(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}";

    [Fact]
    public async Task An_enqueued_job_is_persisted_and_can_then_be_claimed_and_completed()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("lifecycle");
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, """{"prompt":"login"}""", key,
                cancellationToken: TestContext.Current.CancellationToken);

            // EnqueueAsync deliberately does not save: the caller owns the
            // transaction so enqueueing is atomic with the triggering change.
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (claimer, claimerDb) = NewWorker(clock);

        await using (claimerDb)
        {
            var claimed = await claimer.ClaimAsync(
                "worker-1", 10, Lease, TestContext.Current.CancellationToken);

            var job = claimed.ShouldHaveSingleItem();
            job.Id.ShouldBe(jobId);
            job.State.ShouldBe(JobState.Running);
            job.Attempt.ShouldBe(1);
            job.LeaseOwner.ShouldBe("worker-1");

            await claimer.CompleteAsync(jobId, "worker-1", TestContext.Current.CancellationToken);
        }

        var (reader, readerDb) = NewWorker(clock);

        await using (readerDb)
        {
            var job = await reader.GetAsync(jobId, TestContext.Current.CancellationToken);

            job.ShouldNotBeNull();
            job!.State.ShouldBe(JobState.Succeeded);
            job.CompletedAt.ShouldNotBeNull();
            job.LeaseOwner.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Enqueueing_the_same_idempotency_key_twice_returns_the_original_job()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("idempotent");
        Guid firstId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var first = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstId = first.Id;
        }

        var (second, secondDb) = NewWorker(clock);

        await using (secondDb)
        {
            var job = await second.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);

            job.Id.ShouldBe(firstId, "a duplicate submission must not create a second billable job");

            await secondDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Two workers polling the same table must take disjoint work. Without
    /// <c>SKIP LOCKED</c> the second worker would block on the first's
    /// transaction instead of moving on to other rows.
    /// </summary>
    [Fact]
    public async Task Two_workers_claiming_concurrently_take_disjoint_jobs()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var organizationId = Guid.CreateVersion7();
        var keys = Enumerable.Range(0, 6).Select(i => UniqueKey($"concurrent-{i}")).ToList();

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            foreach (var key in keys)
            {
                await queue.EnqueueAsync(
                    organizationId, JobKinds.GenerateScreen, "{}", key,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (workerA, dbA) = NewWorker(clock);
        var (workerB, dbB) = NewWorker(clock);

        await using (dbA)
        await using (dbB)
        {
            var claimA = workerA.ClaimAsync("worker-a", 3, Lease, TestContext.Current.CancellationToken);
            var claimB = workerB.ClaimAsync("worker-b", 3, Lease, TestContext.Current.CancellationToken);

            var results = await Task.WhenAll(claimA, claimB);

            var idsA = results[0].Select(j => j.Id).ToHashSet();
            var idsB = results[1].Select(j => j.Id).ToHashSet();

            idsA.Overlaps(idsB).ShouldBeFalse("no job may be claimed by two workers at once");
            (idsA.Count + idsB.Count).ShouldBe(6);
        }
    }

    /// <summary>
    /// The crash-recovery path. A worker claims a job and dies; once the lease
    /// expires, another worker must take it over with no broker and no operator
    /// intervention.
    /// </summary>
    [Fact]
    public async Task A_job_orphaned_by_a_crashed_worker_is_reclaimed_after_its_lease_expires()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("orphaned");
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        // A worker claims the job, then "crashes": it never completes or fails it.
        var shortLease = TimeSpan.FromSeconds(30);
        var (crashing, crashingDb) = NewWorker(clock);

        await using (crashingDb)
        {
            var claimed = await crashing.ClaimAsync(
                "worker-doomed", 10, shortLease, TestContext.Current.CancellationToken);

            claimed.ShouldHaveSingleItem().Id.ShouldBe(jobId);
        }

        // Nothing is claimable while the lease is still valid.
        var (tooEarly, tooEarlyDb) = NewWorker(clock);

        await using (tooEarlyDb)
        {
            (await tooEarly.ClaimAsync("worker-2", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty("a live lease must be respected");
        }

        clock.Advance(shortLease + TimeSpan.FromSeconds(5));

        var (recovering, recoveringDb) = NewWorker(clock);

        await using (recoveringDb)
        {
            var reclaimed = await recovering.ClaimAsync(
                "worker-2", 10, Lease, TestContext.Current.CancellationToken);

            var job = reclaimed.ShouldHaveSingleItem();
            job.Id.ShouldBe(jobId);
            job.LeaseOwner.ShouldBe("worker-2");
            job.Attempt.ShouldBe(2, "reclaiming counts as a new attempt");
        }
    }

    [Fact]
    public async Task A_retryable_failure_is_rescheduled_and_not_immediately_reclaimable()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("retry");
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            await worker.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken);
            await worker.FailAsync(
                jobId, "worker-1", "UNAVAILABLE: provider is down", retryable: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var (checker, checkerDb) = NewWorker(clock);

        await using (checkerDb)
        {
            var job = await checker.GetAsync(jobId, TestContext.Current.CancellationToken);
            job!.State.ShouldBe(JobState.Pending);
            job.LastError!.ShouldContain("UNAVAILABLE");

            // Backoff after attempt 1 is 2s, so it is not yet due.
            (await checker.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty("backoff must be respected");
        }

        clock.Advance(TimeSpan.FromSeconds(5));

        var (retrier, retrierDb) = NewWorker(clock);

        await using (retrierDb)
        {
            (await retrier.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem().Attempt.ShouldBe(2);
        }
    }

    [Fact]
    public async Task A_non_retryable_failure_moves_the_job_straight_to_dead()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("dead");
        var clock = new SystemClock();
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            await worker.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken);

            await worker.FailAsync(
                jobId, "worker-1", "MISSING_CANVAS_CREDENTIALS", retryable: false,
                cancellationToken: TestContext.Current.CancellationToken);

            var job = await worker.GetAsync(jobId, TestContext.Current.CancellationToken);
            job!.State.ShouldBe(JobState.Dead);
            job.Attempt.ShouldBe(1, "a non-retryable error must not burn further attempts");

            (await worker.ClaimAsync("worker-2", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty("a dead job must never be claimed again");
        }
    }

    [Fact]
    public async Task A_delayed_job_is_not_claimable_before_its_scheduled_time()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("delayed");
        var clock = new MutableClock(DateTimeOffset.UtcNow);

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                delay: TimeSpan.FromMinutes(10),
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (early, earlyDb) = NewWorker(clock);

        await using (earlyDb)
        {
            (await early.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
        }

        clock.Advance(TimeSpan.FromMinutes(11));

        var (late, lateDb) = NewWorker(clock);

        await using (lateDb)
        {
            (await late.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem();
        }
    }

    // -----------------------------------------------------------------------
    // Lease fencing against a real database (M00-CX-001)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The stale-worker scenario end to end: worker A's lease lapses, worker B
    /// reclaims, and worker A then tries to report a result. The write must be
    /// rejected — otherwise A would overwrite B's attempt, and for billable work
    /// the fact that the provider was called twice would be invisible.
    /// </summary>
    [Fact]
    public async Task A_stale_worker_cannot_complete_a_job_another_worker_reclaimed()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var shortLease = TimeSpan.FromSeconds(30);
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", UniqueKey("stale"),
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (workerA, dbA) = NewWorker(clock);

        await using (dbA)
        {
            (await workerA.ClaimAsync("worker-a", 10, shortLease, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem();
        }

        clock.Advance(shortLease + TimeSpan.FromSeconds(5));

        var (workerB, dbB) = NewWorker(clock);

        await using (dbB)
        {
            (await workerB.ClaimAsync("worker-b", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem().LeaseOwner.ShouldBe("worker-b");
        }

        // Worker A wakes up and tries to finish work it no longer owns.
        var (stale, staleDb) = NewWorker(clock);

        await using (staleDb)
        {
            (await Should.ThrowAsync<DomainException>(() =>
                    stale.CompleteAsync(jobId, "worker-a", TestContext.Current.CancellationToken)))
                .Code.ShouldBe("job.lease_not_held");

            (await Should.ThrowAsync<DomainException>(() =>
                    stale.FailAsync(jobId, "worker-a", "stale failure", retryable: true,
                        TestContext.Current.CancellationToken)))
                .Code.ShouldBe("job.lease_not_held");
        }

        var (reader, readerDb) = NewWorker(clock);

        await using (readerDb)
        {
            var job = await reader.GetAsync(jobId, TestContext.Current.CancellationToken);
            job!.State.ShouldBe(JobState.Running, "worker B's attempt must survive");
            job.LeaseOwner.ShouldBe("worker-b");
        }
    }

    [Fact]
    public async Task A_lost_lease_makes_the_heartbeat_report_failure_rather_than_throw()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var shortLease = TimeSpan.FromSeconds(30);
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", UniqueKey("heartbeat"),
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (workerA, dbA) = NewWorker(clock);

        await using (dbA)
        {
            await workerA.ClaimAsync("worker-a", 10, shortLease, TestContext.Current.CancellationToken);

            // Still the owner: renewal succeeds.
            (await workerA.TryHeartbeatAsync(
                jobId, "worker-a", shortLease, TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        clock.Advance(shortLease + TimeSpan.FromSeconds(5));

        var (workerB, dbB) = NewWorker(clock);

        await using (dbB)
        {
            await workerB.ClaimAsync("worker-b", 10, Lease, TestContext.Current.CancellationToken);
        }

        var (stale, staleDb) = NewWorker(clock);

        await using (staleDb)
        {
            (await stale.TryHeartbeatAsync(
                    jobId, "worker-a", Lease, TestContext.Current.CancellationToken))
                .ShouldBeFalse("a worker must learn it lost the lease, not crash");
        }
    }

    /// <summary>
    /// The read/reclaim/write interleaving from M00-RR-001, made deterministic.
    ///
    /// Worker A loads the job while its lease is **still valid** and holds that
    /// snapshot. Only then does the lease expire and worker B reclaim. A's write
    /// must be refused. An earlier implementation validated the snapshot in
    /// memory and then saved by primary key, which silently overwrote B's attempt.
    ///
    /// No sleeping or racing: the clock is advanced by hand between A's read and
    /// A's write, which is exactly the window the defect lived in.
    /// </summary>
    [Fact]
    public async Task A_worker_that_loses_its_lease_between_reading_and_writing_cannot_complete()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var shortLease = TimeSpan.FromSeconds(30);
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", UniqueKey("interleave"),
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (workerA, dbA) = NewWorker(clock);

        await using (dbA)
        {
            (await workerA.ClaimAsync("worker-a", 10, shortLease, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem();

            // Step 1: A reads the row while its lease is valid. dbA now holds a
            // snapshot in which lease_owner is 'worker-a' and the lease is live.
            var snapshot = await workerA.GetAsync(jobId, TestContext.Current.CancellationToken);
            snapshot!.LeaseOwner.ShouldBe("worker-a");
            snapshot.LeaseExpiresAt!.Value.ShouldBeGreaterThan(clock.UtcNow);

            // Step 2: time passes and B reclaims — all while A still holds its
            // snapshot and believes it owns the job.
            clock.Advance(shortLease + TimeSpan.FromSeconds(5));

            var (workerB, dbB) = NewWorker(clock);

            await using (dbB)
            {
                (await workerB.ClaimAsync("worker-b", 10, Lease, TestContext.Current.CancellationToken))
                    .ShouldHaveSingleItem().Attempt.ShouldBe(2);
            }

            // Step 3: A writes, and is refused. Either layer may catch it first —
            // the fresh untracked read sees worker-b as the owner, and the
            // conditional UPDATE's predicate would match no row regardless. Both
            // are correct refusals, so the assertion accepts either code and the
            // state check below is what proves nothing was overwritten.
            (await Should.ThrowAsync<DomainException>(() =>
                    workerA.CompleteAsync(jobId, "worker-a", TestContext.Current.CancellationToken)))
                .Code.ShouldBeOneOf("job.lease_not_held", "job.lease_expired");

            (await Should.ThrowAsync<DomainException>(() =>
                    workerA.FailAsync(jobId, "worker-a", "stale", retryable: true,
                        TestContext.Current.CancellationToken)))
                .Code.ShouldBeOneOf("job.lease_not_held", "job.lease_expired");

            (await workerA.TryHeartbeatAsync(
                    jobId, "worker-a", Lease, TestContext.Current.CancellationToken))
                .ShouldBeFalse();
        }

        var (reader, readerDb) = NewWorker(clock);

        await using (readerDb)
        {
            var job = await reader.GetAsync(jobId, TestContext.Current.CancellationToken);
            job!.State.ShouldBe(JobState.Running, "worker B's attempt must be intact");
            job.LeaseOwner.ShouldBe("worker-b");
            job.Attempt.ShouldBe(2);
            job.CompletedAt.ShouldBeNull();
        }
    }

    // -----------------------------------------------------------------------
    // Write-time lease evaluation (M00-R3-001A)
    //
    // These drive the production queue with a real SystemClock, so the predicate
    // under test is the shipping expression — `lease_expires_at >
    // COALESCE(NULL::timestamptz, clock_timestamp())` — resolved by PostgreSQL
    // while it executes the statement.
    //
    // An earlier version of this section ran a copy of the SQL owned by the test,
    // which would still have passed if the production predicate were deleted.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A lease valid when the queue reads the row and expired by the time
    /// PostgreSQL executes the UPDATE must not complete the job.
    ///
    /// The interceptor expires the stored lease on a separate connection, landing
    /// deterministically inside a window otherwise microseconds wide. Capturing an
    /// application timestamp before the read — the M00-R3-001A defect — would have
    /// let this through, because that timestamp still satisfied the comparison.
    /// </summary>
    [Fact]
    public async Task A_lease_expiring_between_the_read_and_the_write_cannot_complete()
    {
        var held = await ArrangeHeldLeaseAsync();

        var interceptor = new LeaseExpiryInterceptor(
            fixture.ConnectionString, held.JobId, whenSqlContains: "UPDATE app.jobs");

        await using var db = fixture.CreateContext(interceptor);
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        var error = await Should.ThrowAsync<DomainException>(() =>
            queue.CompleteAsync(held.JobId, held.LeaseOwner, TestContext.Current.CancellationToken));

        interceptor.Fired.ShouldBeTrue("the seam must have engaged, or the test proves nothing");
        error.Code.ShouldBe("job.lease_not_held");

        await AssertStillRunningAsync(held.JobId, held.LeaseOwner);
    }

    [Fact]
    public async Task A_lease_expiring_between_the_read_and_the_write_cannot_fail_the_job()
    {
        var held = await ArrangeHeldLeaseAsync();

        var interceptor = new LeaseExpiryInterceptor(
            fixture.ConnectionString, held.JobId, whenSqlContains: "UPDATE app.jobs");

        await using var db = fixture.CreateContext(interceptor);
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        var error = await Should.ThrowAsync<DomainException>(() =>
            queue.FailAsync(held.JobId, held.LeaseOwner, "late failure", retryable: true,
                TestContext.Current.CancellationToken));

        interceptor.Fired.ShouldBeTrue();
        error.Code.ShouldBe("job.lease_not_held");

        await AssertStillRunningAsync(held.JobId, held.LeaseOwner);
    }

    [Fact]
    public async Task A_lease_expiring_before_the_write_cannot_be_renewed()
    {
        var held = await ArrangeHeldLeaseAsync();

        var interceptor = new LeaseExpiryInterceptor(
            fixture.ConnectionString, held.JobId, whenSqlContains: "UPDATE app.jobs");

        await using var db = fixture.CreateContext(interceptor);
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        (await queue.TryHeartbeatAsync(
                held.JobId, held.LeaseOwner, Lease, TestContext.Current.CancellationToken))
            .ShouldBeFalse("an expired lease must not be renewable");

        interceptor.Fired.ShouldBeTrue();
    }

    /// <summary>
    /// Isolates *write-time resolution* of the expiry comparison.
    ///
    /// The job is claimed with a one-second lease. The interceptor then holds the
    /// UPDATE for two seconds before it reaches the server, so the lease lapses
    /// by the passage of time — nothing rewrites the row.
    ///
    /// This is the only shape that distinguishes the fix from the defect:
    ///
    ///   * Comparing against a timestamp captured before the read (M00-R3-001A)
    ///     still satisfies `lease_expires_at > @now`, so the write is allowed.
    ///   * Resolving the instant with `clock_timestamp()` at execution sees the
    ///     expired lease and refuses.
    ///
    /// Rewriting the stored expiry backwards, as the neighbouring tests do, is
    /// refused by *both* — which is why those tests cannot catch this regression.
    /// </summary>
    [Fact]
    public async Task A_lease_lapsing_by_elapsed_time_during_the_write_is_refused()
    {
        await RequireEmptyJobTableAsync();

        var realClock = new SystemClock();
        var oneSecondLease = TimeSpan.FromSeconds(1);
        Guid jobId;

        await using (var db = fixture.CreateContext())
        {
            var queue = new PostgresJobQueue(db, realClock, NullLogger<PostgresJobQueue>.Instance);

            var job = await queue.EnqueueAsync(
                Guid.CreateVersion7(), JobKinds.GenerateScreen, "{}", UniqueKey("elapsed"),
                cancellationToken: TestContext.Current.CancellationToken);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;

            (await queue.ClaimAsync(
                "worker-brief", 10, oneSecondLease, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem().Id.ShouldBe(jobId);
        }

        // Delay only — no mutation. The lease expires because time passes.
        var interceptor = new LeaseExpiryInterceptor(
            fixture.ConnectionString,
            jobId,
            whenSqlContains: "UPDATE app.jobs",
            mutationSql: string.Empty,
            delayBefore: TimeSpan.FromSeconds(2));

        await using var slowDb = fixture.CreateContext(interceptor);
        var slowQueue = new PostgresJobQueue(slowDb, realClock, NullLogger<PostgresJobQueue>.Instance);

        var error = await Should.ThrowAsync<DomainException>(() =>
            slowQueue.CompleteAsync(jobId, "worker-brief", TestContext.Current.CancellationToken));

        interceptor.Fired.ShouldBeTrue("the seam must have engaged, or the test proves nothing");
        error.Code.ShouldBe("job.lease_not_held");

        await using var reader = fixture.CreateContext();
        var readQueue = new PostgresJobQueue(reader, realClock, NullLogger<PostgresJobQueue>.Instance);
        var final = await readQueue.GetAsync(jobId, TestContext.Current.CancellationToken);

        final!.State.ShouldBe(JobState.Running, "the stale write must not have landed");
        final.CompletedAt.ShouldBeNull();
    }

    /// <summary>
    /// Isolates the lease-owner predicate in the SQL.
    ///
    /// The row is reassigned to another worker in the gap, with the lease left
    /// unexpired and the state left Running. The domain's pre-check saw us as the
    /// owner, so only <c>AND lease_owner = {leaseOwner}</c> can refuse this write.
    /// Deleting that line from the queue makes this test fail.
    /// </summary>
    [Fact]
    public async Task A_lease_reassigned_between_the_read_and_the_write_cannot_complete()
    {
        var held = await ArrangeHeldLeaseAsync();

        var interceptor = new LeaseExpiryInterceptor(
            fixture.ConnectionString,
            held.JobId,
            whenSqlContains: "UPDATE app.jobs",
            mutationSql: """
                UPDATE app.jobs
                SET lease_owner = 'a-different-worker',
                    lease_expires_at = clock_timestamp() + interval '10 minutes'
                WHERE id = @id
                """);

        await using var db = fixture.CreateContext(interceptor);
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        var error = await Should.ThrowAsync<DomainException>(() =>
            queue.CompleteAsync(held.JobId, held.LeaseOwner, TestContext.Current.CancellationToken));

        interceptor.Fired.ShouldBeTrue("the seam must have engaged, or the test proves nothing");
        error.Code.ShouldBe("job.lease_not_held");

        await AssertStillRunningAsync(held.JobId, "a-different-worker");
    }

    /// <summary>
    /// The domain pre-check also refuses a caller who was never the owner, before
    /// any SQL runs. Complements the test above rather than replacing it: that one
    /// proves the database predicate, this one proves the fast path.
    /// </summary>
    [Fact]
    public async Task A_caller_who_never_held_the_lease_is_refused_before_any_write()
    {
        var held = await ArrangeHeldLeaseAsync();

        await using var db = fixture.CreateContext();
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        var error = await Should.ThrowAsync<DomainException>(() =>
            queue.CompleteAsync(held.JobId, "a-different-worker", TestContext.Current.CancellationToken));

        error.Code.ShouldBe("job.lease_not_held");

        await AssertStillRunningAsync(held.JobId, held.LeaseOwner);
    }

    /// <summary>
    /// Isolates the write-time expiry predicate: the owner matches and the state
    /// is Running, and only expiry disqualifies the write. Deleting
    /// <c>AND lease_expires_at > COALESCE(...)</c> from the queue makes this fail.
    ///
    /// The stored expiry is pushed into the past *after* the row was claimed, so
    /// the only thing that can refuse the write is the database-side comparison.
    /// </summary>
    [Fact]
    public async Task An_expired_stored_lease_is_refused_by_the_database_predicate()
    {
        var held = await ArrangeHeldLeaseAsync();

        await using (var setup = fixture.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE app.jobs SET lease_expires_at = clock_timestamp() - interval '1 second' "
                + "WHERE id = {0}",
                [held.JobId],
                TestContext.Current.CancellationToken);
        }

        await using var db = fixture.CreateContext();
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        var error = await Should.ThrowAsync<DomainException>(() =>
            queue.CompleteAsync(held.JobId, held.LeaseOwner, TestContext.Current.CancellationToken));

        error.Code.ShouldBeOneOf("job.lease_not_held", "job.lease_expired");

        (await queue.TryHeartbeatAsync(
                held.JobId, held.LeaseOwner, Lease, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        await AssertStillRunningAsync(held.JobId, held.LeaseOwner);
    }

    /// <summary>
    /// The legitimate holder must still be able to act — proving the predicates
    /// are not simply refusing everything.
    /// </summary>
    [Fact]
    public async Task The_current_lease_holder_can_renew_then_complete()
    {
        var held = await ArrangeHeldLeaseAsync();

        await using var db = fixture.CreateContext();
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        (await queue.TryHeartbeatAsync(
                held.JobId, held.LeaseOwner, Lease, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await queue.CompleteAsync(held.JobId, held.LeaseOwner, TestContext.Current.CancellationToken);

        var job = await queue.GetAsync(held.JobId, TestContext.Current.CancellationToken);
        job!.State.ShouldBe(JobState.Succeeded);
        job.LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public async Task The_current_lease_holder_can_record_a_failure()
    {
        var held = await ArrangeHeldLeaseAsync();

        await using var db = fixture.CreateContext();
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        await queue.FailAsync(
            held.JobId, held.LeaseOwner, "UNAVAILABLE", retryable: true,
            TestContext.Current.CancellationToken);

        var job = await queue.GetAsync(held.JobId, TestContext.Current.CancellationToken);
        job!.State.ShouldBe(JobState.Pending);
        job.LastError.ShouldBe("UNAVAILABLE");
        job.LeaseOwner.ShouldBeNull();
    }

    /// <summary>A job whose lease is currently held, plus the clock that says so.</summary>
    private sealed record HeldLease(Guid JobId, string LeaseOwner, MutableClock Clock);

    /// <summary>
    /// Enqueues a job and claims it with a lease long enough that real time cannot
    /// expire it during the test.
    /// </summary>
    private async Task<HeldLease> ArrangeHeldLeaseAsync()
    {
        await RequireEmptyJobTableAsync();

        var clock = new MutableClock(DateTimeOffset.UtcNow);
        const string owner = "worker-holder";
        Guid id;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                Guid.CreateVersion7(), JobKinds.GenerateScreen, "{}", UniqueKey("writetime"),
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = job.Id;
        }

        // Claimed with a real clock so the stored lease is anchored to server time,
        // which is what the production predicate compares against.
        await using (var claimDb = fixture.CreateContext())
        {
            var claimer = new PostgresJobQueue(
                claimDb, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

            (await claimer.ClaimAsync(
                owner, 10, TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem().Id.ShouldBe(id);
        }

        return new HeldLease(id, owner, clock);
    }

    /// <summary>
    /// Asserts the row was not written: still Running, still owned, not completed.
    /// This is what distinguishes a refused write from a silently applied one.
    /// </summary>
    private async Task AssertStillRunningAsync(Guid jobId, string leaseOwner)
    {
        await using var db = fixture.CreateContext();
        var queue = new PostgresJobQueue(db, new SystemClock(), NullLogger<PostgresJobQueue>.Instance);

        var job = await queue.GetAsync(jobId, TestContext.Current.CancellationToken);

        job.ShouldNotBeNull();
        job!.State.ShouldBe(JobState.Running, "a refused write must not change state");
        job.LeaseOwner.ShouldBe(leaseOwner);
        job.CompletedAt.ShouldBeNull();
    }

    /// <summary>
    /// An expired lease confers no rights even when nobody has reclaimed the job,
    /// so the original owner string still matches. Only the expiry distinguishes
    /// this from a valid write.
    /// </summary>
    [Fact]
    public async Task An_expired_lease_is_refused_even_though_the_owner_still_matches()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var shortLease = TimeSpan.FromSeconds(30);
        Guid jobId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", UniqueKey("expired-owner"),
                cancellationToken: TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            await worker.ClaimAsync("worker-a", 10, shortLease, TestContext.Current.CancellationToken);

            clock.Advance(shortLease + TimeSpan.FromSeconds(1));

            // Nobody reclaimed, so lease_owner is still 'worker-a'.
            var job = await worker.GetAsync(jobId, TestContext.Current.CancellationToken);
            job!.LeaseOwner.ShouldBe("worker-a");

            (await Should.ThrowAsync<DomainException>(() =>
                    worker.CompleteAsync(jobId, "worker-a", TestContext.Current.CancellationToken)))
                .Code.ShouldBeOneOf("job.lease_expired", "job.lease_not_held");

            (await worker.TryHeartbeatAsync(
                    jobId, "worker-a", Lease, TestContext.Current.CancellationToken))
                .ShouldBeFalse("an expired lease cannot be renewed");
        }
    }

    /// <summary>
    /// The successful path, to prove the conditional updates are not simply
    /// refusing everything.
    /// </summary>
    [Fact]
    public async Task A_current_lease_holder_can_complete_fail_and_renew()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        Guid failId;
        Guid succeedId;

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            failId = (await queue.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", UniqueKey("happy-fail"),
                cancellationToken: TestContext.Current.CancellationToken)).Id;

            succeedId = (await queue.EnqueueAsync(
                organizationId, JobKinds.ParseDesignArtifact, "{}", UniqueKey("happy-ok"),
                cancellationToken: TestContext.Current.CancellationToken)).Id;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            (await worker.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken))
                .Count.ShouldBe(2);

            (await worker.TryHeartbeatAsync(
                succeedId, "worker-1", Lease, TestContext.Current.CancellationToken)).ShouldBeTrue();

            await worker.CompleteAsync(succeedId, "worker-1", TestContext.Current.CancellationToken);
            await worker.FailAsync(
                failId, "worker-1", "UNAVAILABLE", retryable: true,
                TestContext.Current.CancellationToken);
        }

        var (reader, readerDb) = NewWorker(clock);

        await using (readerDb)
        {
            (await reader.GetAsync(succeedId, TestContext.Current.CancellationToken))!
                .State.ShouldBe(JobState.Succeeded);

            var failed = await reader.GetAsync(failId, TestContext.Current.CancellationToken);
            failed!.State.ShouldBe(JobState.Pending);
            failed.LastError.ShouldBe("UNAVAILABLE");
            failed.RunAfter.ShouldBeGreaterThan(clock.UtcNow);
        }
    }

    // -----------------------------------------------------------------------
    // Crash exhaustion against a real database (M00-CX-002)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A job that kills its worker every time must reach Dead. Previously the
    /// claim sweep ignored the attempt budget, so such a job was handed out as
    /// attempt 4, 5, 6 ... indefinitely.
    /// </summary>
    [Fact]
    public async Task A_job_that_repeatedly_crashes_its_worker_is_dead_lettered()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var shortLease = TimeSpan.FromSeconds(30);
        const int maxAttempts = 3;
        Guid jobId;

        await using (var db = fixture.CreateContext())
        {
            var job = JobRecord.Enqueue(
                organizationId, JobKinds.GenerateScreen, "{}", UniqueKey("poison"),
                clock, maxAttempts: maxAttempts);

            db.Jobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        }

        // Every attempt is claimed and then abandoned by a dying worker.
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (worker, workerDb) = NewWorker(clock);

            await using (workerDb)
            {
                var claimed = await worker.ClaimAsync(
                    $"worker-{attempt}", 10, shortLease, TestContext.Current.CancellationToken);

                claimed.ShouldHaveSingleItem().Attempt.ShouldBe(attempt);
            }

            clock.Advance(shortLease + TimeSpan.FromSeconds(5));
        }

        // The next sweep must terminate it instead of handing it out again.
        var (sweeper, sweeperDb) = NewWorker(clock);

        await using (sweeperDb)
        {
            (await sweeper.ClaimAsync("worker-4", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty("the attempt budget is spent");

            var job = await sweeper.GetAsync(jobId, TestContext.Current.CancellationToken);
            job!.State.ShouldBe(JobState.Dead);
            job.Attempt.ShouldBe(maxAttempts);
            job.LastError!.ShouldContain("Lease expired on attempt 3 of 3");
        }

        // And it stays dead on subsequent sweeps.
        var (again, againDb) = NewWorker(clock);

        await using (againDb)
        {
            clock.Advance(TimeSpan.FromHours(1));
            (await again.ClaimAsync("worker-5", 10, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    // Tenant-scoped idempotency (M00-CX-003)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Idempotency keys are client-supplied. With a global scope, one tenant's
    /// key collided with another's and the pre-insert lookup returned a job
    /// belonging to a different organization — a cross-tenant disclosure.
    /// </summary>
    [Fact]
    public async Task The_same_idempotency_key_in_two_organizations_creates_two_jobs()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();
        const string sharedKey = "screen:checkout:v1";
        Guid jobA;

        var (queueA, dbA) = NewWorker(clock);

        await using (dbA)
        {
            var job = await queueA.EnqueueAsync(
                orgA, JobKinds.GenerateScreen, "{}", sharedKey,
                cancellationToken: TestContext.Current.CancellationToken);
            await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobA = job.Id;
        }

        var (queueB, dbB) = NewWorker(clock);

        await using (dbB)
        {
            var job = await queueB.EnqueueAsync(
                orgB, JobKinds.GenerateScreen, "{}", sharedKey,
                cancellationToken: TestContext.Current.CancellationToken);

            job.Id.ShouldNotBe(jobA, "organization B must not receive organization A's job");
            job.OrganizationId.ShouldBe(orgB);

            await dbB.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = fixture.CreateContext();

        (await reader.Jobs.CountAsync(
            j => j.IdempotencyKey == sharedKey, TestContext.Current.CancellationToken))
            .ShouldBe(2);
    }

    [Fact]
    public async Task The_database_rejects_a_duplicate_key_within_one_organization()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var organizationId = Guid.CreateVersion7();
        const string key = "screen:checkout:v1";

        await using var db = fixture.CreateContext();
        db.Jobs.Add(JobRecord.Enqueue(organizationId, JobKinds.GenerateScreen, "{}", key, clock));
        db.Jobs.Add(JobRecord.Enqueue(organizationId, JobKinds.GenerateScreen, "{}", key, clock));

        var ex = await Should.ThrowAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<Npgsql.PostgresException>()
            .SqlState.ShouldBe(Npgsql.PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// The documented race contract: both callers miss the lookup, the loser's
    /// commit hits the unique index, and re-resolving by key returns the winner's
    /// job rather than creating a second billable one.
    /// </summary>
    [Fact]
    public async Task Losing_an_enqueue_race_resolves_to_the_winning_job()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var organizationId = Guid.CreateVersion7();
        var key = UniqueKey("race");
        Guid winnerId;

        var (winner, winnerDb) = NewWorker(clock);
        var (loser, loserDb) = NewWorker(clock);

        await using (winnerDb)
        await using (loserDb)
        {
            // Both observe no existing job, as in a true race.
            var winnerJob = await winner.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);

            var loserJob = await loser.EnqueueAsync(
                organizationId, JobKinds.GenerateScreen, "{}", key,
                cancellationToken: TestContext.Current.CancellationToken);

            loserJob.Id.ShouldNotBe(winnerJob.Id, "both added a distinct job before either committed");

            await winnerDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            winnerId = winnerJob.Id;

            await Should.ThrowAsync<DbUpdateException>(() =>
                loserDb.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        // The loser recovers the winner's job instead of charging twice.
        var (resolver, resolverDb) = NewWorker(clock);

        await using (resolverDb)
        {
            var resolved = await resolver.FindByIdempotencyKeyAsync(
                organizationId, JobKinds.GenerateScreen, key, TestContext.Current.CancellationToken);

            resolved.ShouldNotBeNull();
            resolved!.Id.ShouldBe(winnerId);
        }

        await using var reader = fixture.CreateContext();

        (await reader.Jobs.CountAsync(
            j => j.IdempotencyKey == key, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Claiming_respects_the_requested_batch_size()
    {
        await RequireEmptyJobTableAsync();

        var organizationId = Guid.CreateVersion7();
        var clock = new SystemClock();

        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            for (var i = 0; i < 5; i++)
            {
                await queue.EnqueueAsync(
                    organizationId, JobKinds.ParseDesignArtifact, "{}", UniqueKey($"batch-{i}"),
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            (await worker.ClaimAsync("worker-1", 2, Lease, TestContext.Current.CancellationToken))
                .Count.ShouldBe(2);

            (await worker.ClaimAsync("worker-1", 0, Lease, TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    // M00-R4-001 — the claim is anchored to database time
    //
    // Claim eligibility was already decided by PostgreSQL, but the lease it
    // produced was then calculated by the domain from the *worker's* clock. The
    // two could disagree, and the failure was asymmetric and nasty: a worker
    // running behind the database wrote an expiry that was already in the past,
    // so its own first heartbeat was refused and the job was handed straight back
    // to the pool — a clock-skew problem presenting as a work-loss outage.
    //
    // These tests use SkewedSystemClock, which is deliberately *not* an
    // ISimulatedClock: the simulated seam substitutes its instant for the server's
    // and so cannot show which clock a value came from. Skewing a real clock
    // against a real server is the only arrangement where the two are
    // distinguishable.
    // -----------------------------------------------------------------------

    private static readonly TimeSpan Skew = TimeSpan.FromHours(1);

    /// <summary>
    /// Reads <c>clock_timestamp()</c> from the server, for bracketing a claim
    /// between two known database instants.
    /// </summary>
    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);

        return ToOffset(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Npgsql materialises <c>timestamptz</c> as a UTC <see cref="DateTime"/>, so
    /// a direct cast to <see cref="DateTimeOffset"/> throws.
    /// </summary>
    private static DateTimeOffset ToOffset(object? value) => (DateTime)value!;

    private async Task<DateTimeOffset> StoredLeaseExpiryAsync(Guid jobId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT lease_expires_at FROM app.jobs WHERE id = @id", connection);

        command.Parameters.AddWithValue("id", jobId);

        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        value.ShouldNotBe(DBNull.Value, "a claimed job must carry a lease expiry");

        return ToOffset(value);
    }

    /// <summary>
    /// Enqueues one immediately-due job and returns its id.
    ///
    /// Always on a true clock, even when the test is about skew. The skew under
    /// test belongs to the <i>claiming</i> worker; enqueueing on a fast clock
    /// would instead set <c>run_after</c> in the database's future, and the job
    /// would be correctly ineligible for reasons that have nothing to do with the
    /// lease.
    /// </summary>
    private async Task<Guid> EnqueueOneAsync(string prefix)
    {
        var clock = new SystemClock();
        var (queue, db) = NewWorker(clock);

        await using (db)
        {
            var job = await queue.EnqueueAsync(
                Guid.CreateVersion7(), JobKinds.GenerateScreen, "{}", UniqueKey(prefix),
                cancellationToken: TestContext.Current.CancellationToken);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return job.Id;
        }
    }

    /// <summary>
    /// The regression proper. A worker an hour behind the database must still
    /// produce a lease the database considers live — and must be able to use it.
    ///
    /// The heartbeat assertion is the one that matters. Checking only that the
    /// stored expiry looks plausible would pass on an implementation that stored
    /// the right value and still refused to act on it; renewing proves the lease
    /// the worker minted is one the worker can actually hold.
    /// </summary>
    [Fact]
    public async Task A_worker_clock_an_hour_behind_the_database_still_mints_a_usable_lease()
    {
        await RequireEmptyJobTableAsync();

        var jobId = await EnqueueOneAsync("clock-behind");
        var behind = new SkewedSystemClock(-Skew);

        var (worker, workerDb) = NewWorker(behind);

        await using (workerDb)
        {
            var claimed = await worker.ClaimAsync(
                "worker-behind", 10, Lease, TestContext.Current.CancellationToken);

            claimed.Select(j => j.Id).ShouldContain(jobId);

            var expiry = await StoredLeaseExpiryAsync(jobId);
            var databaseNow = await DatabaseNowAsync();

            expiry.ShouldBeGreaterThan(
                databaseNow,
                "a lease derived from the worker's slow clock would already have expired");

            // Pinned to database time, not the worker's: an hour of skew is far
            // larger than the five-minute lease, so these cannot be confused.
            expiry.ShouldBeGreaterThan(behind.UtcNow + Lease);

            (await worker.TryHeartbeatAsync(
                jobId, "worker-behind", Lease, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the worker must be able to renew the lease it just took");
        }
    }

    /// <summary>
    /// The other direction. A fast worker clock must not buy extra lease time:
    /// an hour of skew would have made a five-minute lease last sixty-five
    /// minutes, delaying crash recovery by exactly that much.
    /// </summary>
    [Fact]
    public async Task A_worker_clock_an_hour_ahead_of_the_database_does_not_lengthen_its_lease()
    {
        await RequireEmptyJobTableAsync();

        var jobId = await EnqueueOneAsync("clock-ahead");
        var ahead = new SkewedSystemClock(Skew);

        var (worker, workerDb) = NewWorker(ahead);

        await using (workerDb)
        {
            var before = await DatabaseNowAsync();

            (await worker.ClaimAsync("worker-ahead", 10, Lease, TestContext.Current.CancellationToken))
                .Select(j => j.Id).ShouldContain(jobId);

            var expiry = await StoredLeaseExpiryAsync(jobId);

            expiry.ShouldBeLessThan(
                before + Lease + TimeSpan.FromMinutes(1),
                "the lease must be bounded by database time plus its duration");

            expiry.ShouldBeLessThan(ahead.UtcNow, "the worker's own clock must not extend the lease");
        }
    }

    /// <summary>
    /// States the rule exactly rather than approximately. Bracketing the claim
    /// between two server reads makes the assertion tolerance-free: the expiry
    /// must fall inside the window the claim demonstrably happened in.
    /// </summary>
    [Fact]
    public async Task The_persisted_lease_expiry_is_database_claim_time_plus_the_lease_duration()
    {
        await RequireEmptyJobTableAsync();

        var jobId = await EnqueueOneAsync("expiry-window");

        // Skewed on purpose: if the expiry were derived from this clock the
        // bracket below could not contain it.
        var clock = new SkewedSystemClock(-Skew);

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            var before = await DatabaseNowAsync();

            await worker.ClaimAsync("worker-1", 10, Lease, TestContext.Current.CancellationToken);

            var after = await DatabaseNowAsync();
            var expiry = await StoredLeaseExpiryAsync(jobId);

            expiry.ShouldBeInRange(before + Lease, after + Lease);
        }
    }

    /// <summary>
    /// Eight workers, one job. Exactly one claim may succeed.
    ///
    /// <see cref="Two_workers_claiming_concurrently_take_disjoint_jobs"/> proves
    /// work is partitioned; this proves the stronger property on the contended
    /// case, where every worker wants the same row. A claim that selected and
    /// then wrote in two steps could satisfy the first test and fail this one.
    /// </summary>
    [Fact]
    public async Task Eight_workers_racing_for_one_job_produce_exactly_one_claim()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var jobId = await EnqueueOneAsync("contended");

        var workers = Enumerable.Range(0, 8).Select(_ => NewWorker(clock)).ToList();

        try
        {
            var claims = await Task.WhenAll(workers.Select((w, i) =>
                w.Queue.ClaimAsync($"worker-{i}", 1, Lease, TestContext.Current.CancellationToken)));

            claims.SelectMany(c => c.Select(j => j.Id))
                .Where(id => id == jobId)
                .Count()
                .ShouldBe(1, "a single job must not be claimed by more than one worker");
        }
        finally
        {
            foreach (var worker in workers)
            {
                await worker.Db.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Holds the SQL claim and <see cref="JobRecord.TryClaim"/> together.
    ///
    /// Moving the claim into SQL left the domain with an in-memory model of the
    /// same transition, which the unit tests use as setup. Two descriptions of one
    /// rule drift apart unless something fails when they do — the lesson of the
    /// copied-SQL test M00-R3-001C removed. Expiry is excluded deliberately:
    /// disagreeing about it is the whole point of this round's fix.
    /// </summary>
    [Fact]
    public async Task The_sql_claim_agrees_with_the_domain_claim_model()
    {
        await RequireEmptyJobTableAsync();

        var clock = new SystemClock();
        var jobId = await EnqueueOneAsync("cross-check");

        var (worker, workerDb) = NewWorker(clock);

        await using (workerDb)
        {
            var claimed = (await worker.ClaimAsync(
                    "worker-1", 10, Lease, TestContext.Current.CancellationToken))
                .Single(j => j.Id == jobId);

            // The same transition applied in memory to a fresh, unclaimed job.
            var model = JobRecord.Enqueue(
                claimed.OrganizationId, claimed.Kind, claimed.PayloadJson, "model", clock);

            model.TryClaim("worker-1", Lease, clock).ShouldBeTrue();

            claimed.State.ShouldBe(model.State);
            claimed.Attempt.ShouldBe(model.Attempt);
            claimed.LeaseOwner.ShouldBe(model.LeaseOwner);
        }
    }
}

/// <summary>
/// A real clock offset by a fixed amount, standing in for a worker whose host
/// clock disagrees with the database server's.
///
/// Deliberately <b>not</b> an <see cref="ISimulatedClock"/>. The simulated seam
/// substitutes its instant for <c>clock_timestamp()</c>, which makes the two
/// clocks indistinguishable — exactly the property these tests need to break. So
/// the queue takes real server time here, and this clock is only ever consulted
/// where application time is still (legitimately) used.
/// </summary>
internal sealed class SkewedSystemClock(TimeSpan skew) : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + skew;
}

/// <summary>
/// A clock the test advances by hand, so lease expiry and retry backoff are
/// verified in milliseconds instead of by sleeping.
/// </summary>
internal sealed class MutableClock(DateTimeOffset start) : ISimulatedClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}
