# SPIKE-06: Executing a durable background job

**Status:** ✅ **Verified against PostgreSQL 15.19**
**Date:** 2026-10-20

---

## Question

Can background work survive a worker crash, avoid duplicate billable calls, and
let several workers run concurrently — without a message broker?

The stakes are specific: AI generation is slow, externally rate-limited and
**billable**. Losing a job wastes a user's time; duplicating one costs money.

---

## Verified: the state machine

`JobRecord` with 29 unit tests against a controllable clock, so lease expiry and
backoff are verified in milliseconds rather than by sleeping.

| Invariant                                                          | Verified |
| :----------------------------------------------------------------- | :------- |
| Enqueue starts `Pending` with 0 attempts                           | ✅       |
| Kind and idempotency key required                                  | ✅       |
| Claim takes a lease and increments the attempt                     | ✅       |
| A second worker cannot claim a live lease                          | ✅       |
| **A lost claim race does not consume an attempt**                  | ✅       |
| A delayed job is not claimable before `RunAfter`                   | ✅       |
| An expired lease lets another worker reclaim                       | ✅       |
| **Reclaiming counts as a new attempt**                             | ✅       |
| Heartbeat extends the lease, holder only                           | ✅       |
| `Succeed` clears the lease and records completion                  | ✅       |
| Completing a non-running job fails                                 | ✅       |
| Retryable failure reschedules with backoff                         | ✅       |
| Retries exhausted → `Dead`                                         | ✅       |
| **Non-retryable failure → `Dead` immediately, attempts remaining** | ✅       |
| Cancel allowed from `Pending`/`Running`, not terminal              | ✅       |
| A cancelled job is never claimed again                             | ✅       |
| Backoff: 2s, 4s, 8s, 16s … capped at 300s                          | ✅       |

Two of these deserve attention:

**"A lost claim race does not consume an attempt."** Losing a claim race is
normal operation when several workers poll the same table — it is not a failure
and must not count against `MaxAttempts`. `TryClaim` returns `false` rather than
throwing for exactly this reason.

**"Reclaiming counts as a new attempt."** Without this, a job that reliably kills
its worker would be reclaimed forever. With it, such a job reaches `Dead` and
becomes visible.

---

## Verified: durability against real PostgreSQL

26 integration tests. These behaviours **cannot** be verified with an in-memory
provider, because they depend on PostgreSQL row locking.

### Crash recovery

```
1. Worker "worker-doomed" claims the job with a 30-second lease.
2. It never completes or fails it — simulating a crash.
3. A second worker attempts a claim → gets nothing. A live lease is respected.
4. Clock advances past the lease expiry.
5. The second worker claims successfully, Attempt == 2.
```

No broker. No operator intervention. No dead-letter queue to drain.

### Concurrent claiming

Six jobs, two workers, both claiming three concurrently:

```csharp
var claimA = workerA.ClaimAsync("worker-a", 3, Lease, ct);
var claimB = workerB.ClaimAsync("worker-b", 3, Lease, ct);
var results = await Task.WhenAll(claimA, claimB);

idsA.Overlaps(idsB).ShouldBeFalse();     // no job claimed twice
(idsA.Count + idsB.Count).ShouldBe(6);   // and none missed
```

This is what `FOR UPDATE SKIP LOCKED` buys:

```sql
SELECT * FROM app.jobs
WHERE (state = 'Pending' AND run_after <= @now)
   OR (state = 'Running' AND lease_expires_at <= @now)
ORDER BY run_after
LIMIT @batch
FOR UPDATE SKIP LOCKED
```

Without `SKIP LOCKED`, worker B would block behind worker A's transaction
instead of taking different rows — N workers would serialise into one.

### Idempotency, enforced twice

At the application level:

```csharp
var job = await second.EnqueueAsync(org, JobKinds.GenerateScreen, "{}", key, ct);
job.Id.ShouldBe(firstId);   // returns the existing job
```

And at the database level, which is what holds under an API race:

```sql
UNIQUE (organization_id, kind, idempotency_key)
```

Verified by inserting a duplicate directly and asserting
`PostgresErrorCodes.UniqueViolation`. A companion test confirms the scoping is
per-kind: the same key is legal for a different job kind.

### Failure classification

```
Retryable     → Pending, run_after = now + backoff, not immediately claimable
Non-retryable → Dead at attempt 1, with max_attempts 5 remaining
Dead          → never claimed again
```

The non-retryable path is the money-saving one. `MISSING_CANVAS_CREDENTIALS`
cannot succeed on retry; retrying it three times against a billable endpoint
wastes attempts and spend.

---

## Verified: atomic enqueue

`EnqueueAsync` deliberately does **not** call `SaveChanges`:

```csharp
var job = JobRecord.Enqueue(...);
db.Jobs.Add(job);
// No SaveChangesAsync: the caller commits, so enqueueing is atomic with
// whatever state change requested the work.
return job;
```

This closes the window an external broker would open. With a broker, "commit the
row, then publish" can commit and then lose the message. Here, the row and the
job commit together or not at all.

The integration test asserts the contract explicitly — the job is not visible
until the caller's `SaveChangesAsync`.

---

## Verified: the dispatcher

`JobDispatcher` is a `BackgroundService` that polls, claims and dispatches.

| Behaviour                                                                      | Why                                                                                         |
| :----------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------ |
| The poll loop survives any exception, including the database being unreachable | A dispatcher that dies on a transient outage is not durable                                 |
| Each job runs in its own DI scope                                              | Prevents DbContext and per-job state leaking between jobs                                   |
| `ProviderException` is classified by `IsRetryable`                             | Retry policy follows the provider's own error code                                          |
| Shutdown rethrows rather than failing the job                                  | Leaves the lease to expire so another worker takes it, without consuming a retry            |
| An unknown job kind fails **non-retryably**                                    | The deployed binary has no handler; retrying cannot help, so it surfaces instead of looping |
| Lease owner is `machine:pid:guid`                                              | An orphaned lease can be traced to a real process                                           |

M00 ships the dispatcher with **no handlers registered**. The mechanism is what
needed proving; a handler wrapping an unverified provider call (SPIKE-02) would
be untested code pretending to work.

---

## Design notes

**Why polling.** Work takes minutes; a two-second poll adds negligible latency
and costs one cheap indexed query. If volume ever justifies it, `LISTEN/NOTIFY`
can wake the loop without changing the durability model.

**Why not Hangfire or Quartz.** Both would work and both are reasonable. The
lease-and-retry semantics we need are about 200 lines, we need retry
classification driven by provider error codes, and a library's schema inside our
database is a dependency we would not be able to change. Recorded in ADR-0005.

**Handlers must be idempotent.** A lease can expire mid-flight, so the same
payload can legitimately be delivered twice. This is stated in `IJobHandler`'s
documentation because it is a requirement on every future handler, not an
implementation detail.

**`LeaseDurationSeconds` bounds how long a crashed worker's job stays
unavailable, not how long a handler may take.** The dispatcher renews the lease
while a handler runs, and cancels the handler if renewal reports the lease
lost. `BatchSize` defaults to 1, because jobs in a batch run sequentially and a
larger batch would lease work that may expire before it starts.

---

## Not verified

| Item                                     | Milestone                                                                           |
| :--------------------------------------- | :---------------------------------------------------------------------------------- |
| An actual job handler end to end         | M01 — blocked on SPIKE-02                                                           |
| Job status endpoint                      | M01                                                                                 |
| Dead job retention and archival          | M02 — dead jobs currently accumulate                                                |
| Behaviour under thousands of queued jobs | Post-MVP. Provider rate limits bind first by orders of magnitude.                   |
| Multi-instance worker deployment         | Mechanism verified with 2 concurrent workers in-process; deployment topology is M02 |

---

## Tests

`backend/tests/AIDesignStudio.UnitTests/JobRecordTests.cs` — 29 tests
`backend/tests/AIDesignStudio.IntegrationTests/DurableJobQueueTests.cs` — 26 tests
`backend/tests/AIDesignStudio.IntegrationTests/JobDispatcherTests.cs` — 2 tests
`backend/tests/AIDesignStudio.UnitTests/JobDispatcherLeaseTests.cs` — 3 tests

---

## Corrections after review

This spike was first reported as fully verified. Two independent review rounds
found that it was not, and both were right. Recorded here because the original
claim is part of the evidence trail.

### Round 1 — M00-CX-001/002/003

Three defects the spike's own tests did not reach:

- Completion and failure were not fenced by lease ownership, so a stale worker
  could overwrite the attempt of the worker that reclaimed its job.
- The claim sweep ignored `max_attempts`, so a job that killed its worker was
  reclaimed forever — the exact opposite of what this document claimed.
- Idempotency was global rather than per-organization, so a client-supplied key
  could return another tenant's job.

### Round 2 — M00-RR-001

The round 1 fix validated the lease **in memory** and then saved by primary key.
That left the original interleaving intact:

1. Worker A loads the job; its lease is still valid.
2. The lease expires and worker B reclaims the job.
3. Worker A saves by primary key and overwrites B's attempt.

The lesson worth keeping: moving a check from "absent" to "present in the
application layer" does not make it atomic. Only the database can evaluate a
predicate and apply a write in one step.

**Correction.** Completion, failure and heartbeat are now single conditional
`UPDATE` statements whose `WHERE` clause re-checks `state = 'Running'`, the lease
owner, **and** `lease_expires_at > now`. Zero rows affected means the lease was
lost between the read and the write. Entities are loaded `AsNoTracking`, so EF
cannot flush a tracked copy and reintroduce the unconditional write.

The same round also found that the renewal loop merely returned on losing the
lease, leaving the handler to continue potentially billable work. It now cancels
the handler's token.

### Evidence for the corrections

| Behaviour                                                                 | Test                                                                            |
| :------------------------------------------------------------------------ | :------------------------------------------------------------------------------ |
| A worker that loses its lease between reading and writing cannot complete | `A_worker_that_loses_its_lease_between_reading_and_writing_cannot_complete`     |
| The conditional predicate matches no row for a stale lease                | `The_conditional_update_predicate_matches_no_row_for_a_stale_lease`             |
| An expired lease is refused even when the owner still matches             | `An_expired_lease_is_refused_even_though_the_owner_still_matches`               |
| A current holder can still complete, fail and renew                       | `A_current_lease_holder_can_complete_fail_and_renew`                            |
| Losing the lease mid-execution cancels the running handler                | `JobDispatcherTests.Losing_the_lease_mid_execution_cancels_the_running_handler` |
| A crash-looping job is dead-lettered                                      | `A_job_that_repeatedly_crashes_its_worker_is_dead_lettered`                     |
| Idempotency is organization-scoped                                        | `The_same_idempotency_key_in_two_organizations_creates_two_jobs`                |

Two of these were verified to **fail** on the reverted code: removing the
fencing predicate breaks the predicate test, and removing the handler
cancellation makes the dispatcher test time out.

### A bug the dispatcher test found

Writing `JobDispatcherTests` surfaced a latent crash unrelated to leasing: the
lease-owner identity was built with `[..64]`, which throws when the composed
string is **shorter** than 64 characters — the normal case on a host with a short
name. No prior test constructed a `JobDispatcher`, so nothing reached it. It now
truncates to the column width only when necessary.

### Round 3 — M00-R3-001

Two further defects in the same area, both found by review, both in code that
had already been "fixed" twice.

**A. The expiry comparison used a stale timestamp.** Round 2's conditional
`UPDATE` was correct in shape but bound an application timestamp captured
_before_ an asynchronous read:

```csharp
var now = clock.UtcNow;                       // captured here
var job = await LoadForTransitionAsync(...);  // await
... AND lease_expires_at > {now}              // compared against the old value
```

A lease could expire in that gap and the stale timestamp still satisfied the
comparison. Round 2 closed the _ownership_ gap and left a _time_ gap.

Lease validity is now resolved by PostgreSQL with `clock_timestamp()` while it
executes the statement. `clock_timestamp()` rather than `now()`, because `now()`
is the transaction start time and would reintroduce the same gap. The claim query
uses the same expression, so the queue has one time model. See ADR-0005
amendment A6 for the full clock semantics, including the limit of the test seam.

**B. The renewal loop did not fail closed.** It cancelled the handler when the
heartbeat returned `false`, but if the heartbeat _threw_ — PostgreSQL unreachable
— the renewal task faulted unobserved and the handler kept doing potentially
billable work. Inability to prove ownership is now treated exactly like losing
it, and once ownership is uncertain the dispatcher records nothing. ADR-0005
amendment A7.

### What the current evidence actually proves

| Behaviour                                                             | Test                                                                                                              | Verified to fail on regression                    |
| :-------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------- | :------------------------------------------------ |
| Expiry resolved at the write, not at an earlier capture               | `A_lease_lapsing_by_elapsed_time_during_the_write_is_refused`                                                     | Yes — reverting to the application clock fails it |
| A lease lost in the read/write gap cannot complete, fail or heartbeat | `A_lease_expiring_between_the_read_and_the_write_cannot_*`, `A_lease_expiring_before_the_write_cannot_be_renewed` | Yes — removing the expiry predicate fails 5 tests |
| The owner predicate is enforced in SQL                                | `A_lease_reassigned_between_the_read_and_the_write_cannot_complete`                                               | Yes — removing it fails 4 tests                   |
| The legitimate holder can still act                                   | `The_current_lease_holder_can_renew_then_complete`, `..._can_record_a_failure`                                    | n/a — the positive control                        |
| A heartbeat returning false cancels the handler                       | `JobDispatcherLeaseTests.A_heartbeat_reporting_loss_...`                                                          | Yes — times out without `CancelAsync`             |
| A heartbeat throwing cancels the handler                              | `JobDispatcherLeaseTests.A_heartbeat_that_throws_...`                                                             | Yes — times out without `CancelAsync`             |
| Shutdown is not classified as lease loss                              | `JobDispatcherLeaseTests.Shutdown_cancellation_...`                                                               | n/a — asserts nothing is recorded                 |

**What it does not prove.** The write-time tests reach the read/write window with
an EF command interceptor, not by genuine multi-process contention. The
predicate is the same one that ships and the interleaving is the real one, but
behaviour under sustained concurrent load from separate processes is still
untested — recorded as a gap rather than implied away.

### A test-quality defect found in round 3

Round 2's `The_conditional_update_predicate_matches_no_row_for_a_stale_lease`
executed a **copy of the SQL owned by the test**. It passed, and it would have
kept passing if the production predicate had been deleted. It has been removed
and replaced with the tests above, which drive `PostgresJobQueue` itself.

Worth recording as a category: a test that re-implements the thing it checks
measures the test, not the system.

### Round 4 — M00-R4-001

**The finding.** Rounds 1–3 fenced the writes a worker makes _while holding_ a
lease. Nothing had examined where the lease comes from. Claim eligibility was
decided by PostgreSQL — correct since round 3 — and then `JobRecord.TryClaim`
computed `LeaseExpiresAt` from the **worker's** clock. One transition, two
clocks.

Round 3's amendment A6 had asserted "one time authority, and it is PostgreSQL",
and it was wrong about the single write that creates a lease.

**Why it is worse than cosmetic.** A worker whose clock is behind the database
stores an expiry already in the past. Its own first heartbeat is then refused by
the round-2 predicate, and the job returns to the pool having consumed an
attempt. Three jobs' worth of skew and the job is dead-lettered without ever
having run.

Note what produces the failure: the fence added in rounds 2 and 3 is what turns
clock skew into lost work. **A correct guard fed an inconsistent operand rejects
valid writes as confidently as invalid ones.** The guard was never the problem in
any of these four rounds — the operand was, twice.

Clock skew between an application host and its database is routine: NTP step
corrections, suspended VMs, container drift. This is an ordinary condition.

**The correction.** One data-modifying statement selects the eligible rows and
writes `state`, `lease_owner`, `lease_expires_at`, `attempt` and `updated_at`,
all from a single materialised `clock_timestamp()`. `MATERIALIZED` matters:
`clock_timestamp()` is volatile, so inlining it would evaluate it per reference
and the instant admitting a row would differ from the expiry stored against it.
Full rationale in ADR-0005 amendment A8.

| Behaviour                                       | Test                                                                        | Verified to fail on regression                   |
| :---------------------------------------------- | :-------------------------------------------------------------------------- | :----------------------------------------------- |
| A slow worker clock still mints a usable lease  | `A_worker_clock_an_hour_behind_the_database_still_mints_a_usable_lease`     | Yes — application-time expiry fails it           |
| A fast worker clock does not lengthen the lease | `A_worker_clock_an_hour_ahead_of_the_database_does_not_lengthen_its_lease`  | Yes — same regression                            |
| Expiry is database claim time plus the duration | `The_persisted_lease_expiry_is_database_claim_time_plus_the_lease_duration` | Yes — same regression                            |
| One job, many claimers, exactly one winner      | `Eight_workers_racing_for_one_job_produce_exactly_one_claim`                | n/a — contention control                         |
| The SQL and domain claims do not diverge        | `The_sql_claim_agrees_with_the_domain_claim_model`                          | n/a — anti-drift guard                           |
| The attempt budget is enforced by the claim SQL | `A_job_that_repeatedly_crashes_its_worker_is_dead_lettered`                 | Yes — dropping `attempt < max_attempts` fails it |

The skew tests use `SkewedSystemClock`, which is deliberately **not** an
`ISimulatedClock`: the simulated seam substitutes its own instant for the
server's, which makes the two clocks indistinguishable. Skewing a real clock
against a real server is the only arrangement in which "which clock did this
value come from" is answerable.

**Still not proven.** Everything in the "what it does not prove" note above
stands. `Eight_workers_racing_for_one_job_produce_exactly_one_claim` is eight
concurrent connections inside one process, which exercises PostgreSQL's row
locking but is not multi-process load. And no test here proves the _absolute_
correctness of the server clock — only that the queue consistently uses it.
