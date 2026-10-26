using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.SharedKernel;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the durable job state machine: leasing, crash recovery, retry backoff
/// and terminal failure. These are the mechanics that make background AI work
/// safe to retry without double-charging a provider.
/// </summary>
public sealed class JobRecordTests
{
    private readonly TestClock _clock = new();
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private JobRecord NewJob(int maxAttempts = 3, TimeSpan? delay = null) => JobRecord.Enqueue(
        organizationId: Guid.CreateVersion7(),
        kind: "screen.generate",
        payloadJson: """{"prompt":"login"}""",
        idempotencyKey: "key-1",
        clock: _clock,
        maxAttempts: maxAttempts,
        delay: delay);

    [Fact]
    public void Enqueue_starts_pending_with_no_attempts()
    {
        var job = NewJob();

        job.State.ShouldBe(JobState.Pending);
        job.Attempt.ShouldBe(0);
        job.RunAfter.ShouldBe(_clock.UtcNow);
    }

    [Fact]
    public void Enqueue_requires_an_idempotency_key()
    {
        Should.Throw<DomainException>(() => JobRecord.Enqueue(
                Guid.CreateVersion7(), "k", "{}", "   ", _clock))
            .Code.ShouldBe("job.idempotency_key_required");
    }

    [Fact]
    public void Enqueue_requires_a_kind()
    {
        Should.Throw<DomainException>(() => JobRecord.Enqueue(
                Guid.CreateVersion7(), "  ", "{}", "key", _clock))
            .Code.ShouldBe("job.kind_required");
    }

    [Fact]
    public void TryClaim_takes_a_lease_and_increments_the_attempt()
    {
        var job = NewJob();

        job.TryClaim("worker-1", Lease, _clock).ShouldBeTrue();

        job.State.ShouldBe(JobState.Running);
        job.Attempt.ShouldBe(1);
        job.LeaseOwner.ShouldBe("worker-1");
        job.LeaseExpiresAt.ShouldBe(_clock.UtcNow + Lease);
    }

    [Fact]
    public void TryClaim_refuses_a_job_already_leased_by_another_worker()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock).ShouldBeTrue();

        job.TryClaim("worker-2", Lease, _clock).ShouldBeFalse();

        job.LeaseOwner.ShouldBe("worker-1");
        job.Attempt.ShouldBe(1, "a lost claim race must not consume an attempt");
    }

    [Fact]
    public void TryClaim_refuses_a_job_whose_scheduled_time_has_not_arrived()
    {
        var job = NewJob(delay: TimeSpan.FromMinutes(10));

        job.TryClaim("worker-1", Lease, _clock).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromMinutes(10));
        job.TryClaim("worker-1", Lease, _clock).ShouldBeTrue();
    }

    /// <summary>
    /// The crash-recovery path: a worker dies holding a lease, and once the lease
    /// expires another worker must be able to take over without any broker.
    /// </summary>
    [Fact]
    public void An_expired_lease_lets_another_worker_reclaim_the_job()
    {
        var job = NewJob();
        job.TryClaim("worker-crashed", Lease, _clock).ShouldBeTrue();

        _clock.Advance(Lease + TimeSpan.FromSeconds(1));

        job.TryClaim("worker-2", Lease, _clock).ShouldBeTrue();

        job.LeaseOwner.ShouldBe("worker-2");
        job.Attempt.ShouldBe(2, "reclaiming is a new attempt");
    }

    [Fact]
    public void Heartbeat_extends_the_lease_for_the_holder_only()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock);

        _clock.Advance(TimeSpan.FromMinutes(4));
        job.Heartbeat("worker-1", Lease, _clock);

        job.LeaseExpiresAt.ShouldBe(_clock.UtcNow + Lease);

        Should.Throw<DomainException>(() => job.Heartbeat("worker-2", Lease, _clock))
            .Code.ShouldBe("job.lease_not_held");
    }

    [Fact]
    public void Succeed_clears_the_lease_and_records_completion()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock);

        job.Succeed("worker-1", _clock);

        job.State.ShouldBe(JobState.Succeeded);
        job.CompletedAt.ShouldBe(_clock.UtcNow);
        job.LeaseOwner.ShouldBeNull();
        job.LeaseExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void Succeed_is_rejected_when_the_job_is_not_running()
    {
        Should.Throw<DomainException>(() => NewJob().Succeed("worker-1", _clock))
            .Code.ShouldBe("job.not_running");
    }

    [Fact]
    public void A_retryable_failure_reschedules_with_exponential_backoff()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock);

        job.Fail("worker-1", "transient provider error", _clock);

        job.State.ShouldBe(JobState.Pending);
        job.LastError.ShouldBe("transient provider error");
        job.RunAfter.ShouldBe(_clock.UtcNow + JobRecord.BackoffFor(1));
        job.LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public void A_retryable_failure_becomes_dead_once_attempts_are_exhausted()
    {
        var job = NewJob(maxAttempts: 2);

        job.TryClaim("worker-1", Lease, _clock);
        job.Fail("worker-1", "attempt 1 failed", _clock);
        job.State.ShouldBe(JobState.Pending);

        _clock.Advance(TimeSpan.FromMinutes(1));
        job.TryClaim("worker-1", Lease, _clock).ShouldBeTrue();
        job.Fail("worker-1", "attempt 2 failed", _clock);

        job.State.ShouldBe(JobState.Dead);
        job.Attempt.ShouldBe(2);
        job.CompletedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// A non-retryable failure (bad credentials, invalid payload) must die at once:
    /// retrying it wastes attempts and, for billable calls, money.
    /// </summary>
    [Fact]
    public void A_non_retryable_failure_dies_immediately_with_attempts_remaining()
    {
        var job = NewJob(maxAttempts: 5);
        job.TryClaim("worker-1", Lease, _clock);

        job.Fail("worker-1", "MISSING_CANVAS_CREDENTIALS", _clock, retryable: false);

        job.State.ShouldBe(JobState.Dead);
        job.Attempt.ShouldBe(1);
    }

    [Fact]
    public void Cancel_is_allowed_from_pending_and_running_but_not_from_terminal_states()
    {
        var pending = NewJob();
        pending.Cancel(_clock);
        pending.State.ShouldBe(JobState.Cancelled);

        var running = NewJob();
        running.TryClaim("worker-1", Lease, _clock);
        running.Cancel(_clock);
        running.State.ShouldBe(JobState.Cancelled);

        Should.Throw<DomainException>(() => running.Cancel(_clock))
            .Code.ShouldBe("job.already_terminal");
    }

    [Fact]
    public void A_cancelled_job_cannot_be_claimed()
    {
        var job = NewJob();
        job.Cancel(_clock);

        job.TryClaim("worker-1", Lease, _clock).ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Lease fencing (M00-CX-001)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A worker whose lease lapsed mid-execution may have had its job reclaimed.
    /// If it could still complete or fail the job, it would overwrite the new
    /// owner's attempt and hide the fact that billable work ran twice.
    /// </summary>
    [Fact]
    public void A_stale_lease_owner_cannot_complete_or_fail_the_job()
    {
        var job = NewJob();
        job.TryClaim("worker-crashed", Lease, _clock);

        _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        job.TryClaim("worker-2", Lease, _clock).ShouldBeTrue();

        Should.Throw<DomainException>(() => job.Succeed("worker-crashed", _clock))
            .Code.ShouldBe("job.lease_not_held");

        Should.Throw<DomainException>(() => job.Fail("worker-crashed", "stale", _clock))
            .Code.ShouldBe("job.lease_not_held");

        job.State.ShouldBe(JobState.Running, "a stale write must not change state");
        job.LeaseOwner.ShouldBe("worker-2");
    }

    [Fact]
    public void Only_the_current_lease_owner_can_complete_the_job()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock);

        Should.Throw<DomainException>(() => job.Succeed("worker-2", _clock))
            .Code.ShouldBe("job.lease_not_held");

        job.Succeed("worker-1", _clock);
        job.State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public void A_completed_job_cannot_be_completed_again_by_its_former_owner()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock);
        job.Succeed("worker-1", _clock);

        Should.Throw<DomainException>(() => job.Succeed("worker-1", _clock))
            .Code.ShouldBe("job.not_running");
    }

    // -----------------------------------------------------------------------
    // Attempt budget on repeated crash (M00-CX-002)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A worker that dies never calls Fail, so lease expiry is the only signal
    /// that the attempt was spent. Without counting it, a job that reliably kills
    /// its worker would be reclaimed as attempts 4, 5, 6 ... forever.
    /// </summary>
    [Fact]
    public void Repeated_lease_expiry_exhausts_the_attempt_budget()
    {
        var job = NewJob(maxAttempts: 3);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            job.TryClaim($"worker-{attempt}", Lease, _clock)
                .ShouldBeTrue($"attempt {attempt} should be claimable");

            job.Attempt.ShouldBe(attempt);

            // The worker dies: no Succeed, no Fail, just an expiring lease.
            _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        }

        job.TryClaim("worker-4", Lease, _clock)
            .ShouldBeFalse("the attempt budget is spent, so it must not be handed out again");

        job.Attempt.ShouldBe(3, "a refused claim must not consume a further attempt");
    }

    [Fact]
    public void An_exhausted_expired_lease_can_be_dead_lettered()
    {
        var job = NewJob(maxAttempts: 1);
        job.TryClaim("worker-doomed", Lease, _clock);

        _clock.Advance(Lease + TimeSpan.FromSeconds(1));

        job.HasExpiredLease(_clock).ShouldBeTrue();
        job.IsAttemptBudgetExhausted.ShouldBeTrue();

        job.FailExhaustedLease(_clock);

        job.State.ShouldBe(JobState.Dead);
        job.CompletedAt.ShouldNotBeNull();
        job.LeaseOwner.ShouldBeNull();
        job.LastError!.ShouldContain("Lease expired on attempt 1 of 1");
    }

    [Fact]
    public void A_job_with_attempts_remaining_cannot_be_dead_lettered_for_an_expired_lease()
    {
        var job = NewJob(maxAttempts: 3);
        job.TryClaim("worker-1", Lease, _clock);

        _clock.Advance(Lease + TimeSpan.FromSeconds(1));

        Should.Throw<DomainException>(() => job.FailExhaustedLease(_clock))
            .Code.ShouldBe("job.attempts_remain");
    }

    [Fact]
    public void Heartbeating_keeps_a_long_running_job_out_of_reach_of_other_workers()
    {
        var job = NewJob();
        job.TryClaim("worker-1", Lease, _clock);

        // Four renewals carry the job well past its original lease.
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(4));
            job.Heartbeat("worker-1", Lease, _clock);
            job.TryClaim("worker-2", Lease, _clock).ShouldBeFalse("a renewed lease must be respected");
        }

        job.Attempt.ShouldBe(1, "renewal is not a new attempt");
        job.Succeed("worker-1", _clock);
        job.State.ShouldBe(JobState.Succeeded);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(20, 300)]
    public void Backoff_grows_exponentially_and_is_capped(int attempt, int expectedSeconds) =>
        JobRecord.BackoffFor(attempt).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
}
