# ADR-0005: Durable background jobs in PostgreSQL

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Solution Architect, Lead Developer

## Context

AI generation is slow (seconds to minutes), externally rate-limited, and
**billable**. It cannot run on a request thread, and losing or duplicating a job
has a direct financial cost.

Requirements:

1. An enqueued job survives a worker crash.
2. A duplicate submission does not produce a second billable call.
3. A crashed worker's in-flight job is picked up by another worker.
4. Several workers can poll without serialising on each other.
5. A non-retryable error (bad credentials) does not burn retry attempts.
6. Enqueueing is atomic with the state change that requested the work.

Requirement 6 is the decisive one. With an external broker, "commit the row,
then publish the message" has a window where the row is committed and the
message lost. Closing it needs either a transactional outbox (a database queue
plus a broker) or distributed transactions.

## Decision

The job queue is a table in the application's own PostgreSQL database
(`app.jobs`), polled by `AIDesignStudio.Worker`.

### Mechanism

**Claiming is a lease, not a dequeue.** A worker sets `state = 'Running'`,
`lease_owner`, and `lease_expires_at`. A job is claimable when it is `Pending`
and due, **or** `Running` with an expired lease — which is the crash-recovery
path, with no broker and no operator action.

**`FOR UPDATE SKIP LOCKED` for concurrency.** The claim query locks candidate
rows and skips those another worker holds. Without `SKIP LOCKED`, N workers
would serialise behind the first eligible row.

```sql
SELECT * FROM app.jobs
WHERE (state = 'Pending' AND run_after <= @now)
   OR (state = 'Running' AND lease_expires_at <= @now)
ORDER BY run_after
LIMIT @batch
FOR UPDATE SKIP LOCKED
```

> **Refined by amendment A6:** `@now` is resolved by the database with
> `clock_timestamp()` rather than bound from the application. The shape above is
> what was originally decided; see Amendments for why binding it was wrong.

**Idempotency is a unique index.** `(kind, idempotency_key)` is unique, so a
double-submit collapses onto one job even when two API calls race past the
pre-insert lookup.

> **Corrected by amendment A3:** the index is scoped by organization —
> `(organization_id, kind, idempotency_key)`. The text above records what was
> originally decided; see Amendments below for why it was wrong.

**`EnqueueAsync` does not call `SaveChanges`.** The caller owns the transaction,
which is what makes enqueueing atomic with the triggering change — requirement 6.

**Retry classification comes from the provider.** `ProviderException.IsRetryable`
is derived from the provider's own error code. Retryable failures reschedule with
exponential backoff (2s, 4s, 8s … capped at 5 minutes) via `run_after`;
non-retryable failures go straight to `Dead` with attempts remaining.

**Polling, not push.** A two-second poll adds negligible latency to work that
takes minutes. If volume ever justifies it, `LISTEN/NOTIFY` can wake the loop
without changing the durability model.

## Consequences

**Accepted:**

- Polling costs a cheap indexed query every two seconds per worker.
- Throughput is bounded by PostgreSQL. Nowhere near a constraint at this
  volume; provider rate limits bind first by orders of magnitude.
- `LeaseDurationSeconds` must exceed the longest handler runtime, or a healthy
  worker's job is reclaimed and executed twice. Handlers must be idempotent
  regardless — a lease can expire mid-flight.
- Dead jobs accumulate. Retention is deferred to M02.

**Gained:**

- No broker to operate, secure or pay for.
- Atomic enqueue-with-state-change.
- The queue is inspectable with SQL, and job state is visible in the same
  backup and restore as everything else.

## Verification

Proven against real PostgreSQL in
`backend/tests/AIDesignStudio.IntegrationTests/DurableJobQueueTests.cs` — these
behaviours cannot be verified with an in-memory provider:

- `Two_workers_claiming_concurrently_take_disjoint_jobs`
- `A_job_orphaned_by_a_crashed_worker_is_reclaimed_after_its_lease_expires`
- `A_retryable_failure_is_rescheduled_and_not_immediately_reclaimable`
- `A_non_retryable_failure_moves_the_job_straight_to_dead`
- `Enqueueing_the_same_idempotency_key_twice_returns_the_original_job`

State-machine transitions are covered separately in `JobRecordTests`, against a
controllable clock so lease expiry and backoff are deterministic.

## Not chosen

- **Azure Service Bus / RabbitMQ** — introduces the lost-message window this
  decision exists to avoid, plus infrastructure to operate.
- **Hangfire / Quartz.NET** — would work, and both are reasonable. Rejected
  because the lease-and-retry semantics we need are ~200 lines, we need to
  control retry classification per provider error code, and a library's schema
  in our database is a dependency we would not be able to change.
- **In-memory `Channel<T>`** — loses every enqueued job on restart.

## Revisit when

- Job volume makes polling latency or database load material.
- We need fan-out to consumers outside this system.

---

## Amendments

The decision above stands. These are corrections to its **stated semantics**,
made after the M00 independent review found the implementation and the
description diverging. Recorded here rather than by editing the decision, so the
original text and what was wrong about it both remain visible.

### A1 — Lease ownership fences every worker write (2026-10-20)

**Finding:** M00-CX-001. `CompleteAsync` and `FailAsync` took only a job id, so a
worker whose lease had lapsed mid-execution could still report a result for an
attempt another worker now owned. Two workers could each execute the same
billable provider call while each believed it held the job.

**Correction:** every worker-initiated transition — `Succeed`, `Fail`,
`Heartbeat` — requires the current lease owner and raises `job.lease_not_held`
otherwise. The dispatcher renews the lease for the duration of each handler, so
a slow provider call no longer looks like a crashed worker, and `BatchSize`
defaults to 1 because sequential execution of a larger batch leases work that
may expire before it starts.

### A2 — Lease expiry consumes an attempt (2026-10-20)

**Finding:** M00-CX-002. This document stated that reclaiming counts as a new
attempt "so a job that reliably kills its worker still reaches `Dead` instead of
looping forever". That was not implemented: the claim query ignored
`max_attempts`, and the ceiling was only enforced in `Fail`, which a dead process
never calls.

**Correction:** a job whose budget is spent is not claimable, and the claim sweep
dead-letters a row whose lease lapsed with no attempts remaining
(`FailExhaustedLease`). The claim sweep is the only place this can happen, since
the worker is gone. The statement in the decision is now true.

### A3 — Idempotency is scoped to the organization (2026-10-20)

**Finding:** M00-CX-003. The unique index was `(kind, idempotency_key)`. Keys are
client-supplied, so one tenant's key could collide with another's and the
pre-insert lookup would return a job belonging to a different organization.

**Correction:** the index is `(organization_id, kind, idempotency_key)` and the
lookup filters by organization. `ix_jobs_organization_id` was also added, which
both this document and SECURITY_REQUIREMENTS had claimed existed.

### A4 — The concurrent-duplicate contract, stated accurately (2026-10-20)

**Finding:** M00-CX-003. This document said a concurrent duplicate submission
returns the existing job. Under a true race it does not: both callers miss the
lookup, and the loser's commit fails on the unique index.

**Correction — and why the behaviour was not changed.** Implementing
insert-or-select inside `EnqueueAsync` would require it to own the transaction,
which would give up the atomic enqueue-with-state-change that is the central
reason for choosing a database queue (requirement 6). The design is therefore
unchanged and the contract is now stated precisely:

- The pre-insert lookup collapses the **common** case — a client retrying a
  submission — onto one job.
- The unique index is the backstop under a **true** race, and the loser's commit
  fails.
- A caller that must survive that race catches the unique violation and calls
  `FindByIdempotencyKeyAsync`, which returns the winner's job.

Verified by `Losing_an_enqueue_race_resolves_to_the_winning_job`, which drives
the race and asserts exactly one job exists afterwards.

### Tests added for these amendments

Unit: `A_stale_lease_owner_cannot_complete_or_fail_the_job`,
`Only_the_current_lease_owner_can_complete_the_job`,
`Repeated_lease_expiry_exhausts_the_attempt_budget`,
`An_exhausted_expired_lease_can_be_dead_lettered`,
`Heartbeating_keeps_a_long_running_job_out_of_reach_of_other_workers`.

Integration: `A_stale_worker_cannot_complete_a_job_another_worker_reclaimed`,
`A_lost_lease_makes_the_heartbeat_report_failure_rather_than_throw`,
`A_job_that_repeatedly_crashes_its_worker_is_dead_lettered`,
`The_same_idempotency_key_in_two_organizations_creates_two_jobs`,
`Losing_an_enqueue_race_resolves_to_the_winning_job`.

### A5 — Lease fencing is a conditional UPDATE, not an in-memory check (2026-10-20)

**Finding:** M00-RR-001. Amendment A1 added lease-ownership checks, but the
persistence layer validated them **in memory** and then saved by primary key.
The interleaving A1 was written to prevent therefore remained:

1. Worker A loads the job; its lease is still valid.
2. The lease expires and worker B reclaims the job.
3. Worker A saves by primary key and overwrites B's attempt.

**Correction.** `CompleteAsync`, `FailAsync` and `TryHeartbeatAsync` each issue a
single conditional `UPDATE`:

```sql
UPDATE app.jobs SET ...
WHERE id = @jobId
  AND state = 'Running'
  AND lease_owner = @leaseOwner
  AND lease_expires_at > @now
```

Zero rows affected means the lease was lost between the read and the write, and
the caller is told so. The domain check remains as a fast path that produces a
precise error, but the database is the authority — only it can evaluate the
predicate and apply the write in one step.

`lease_expires_at > @now` also closes a narrower hole: an expired lease conferred
rights to its original owner while nobody had reclaimed the job, because the
owner string still matched.

Entities are loaded `AsNoTracking`, so EF cannot flush a tracked copy and
reintroduce the unconditional write-by-primary-key. `GetAsync` is untracked for
the same reason — a tracked read would return a snapshot predating the raw
update and hide the committed row.

**Also corrected:** the dispatcher's renewal loop returned on losing the lease,
leaving the handler running potentially billable work for a job another worker
owned. It now cancels the handler's cancellation token.

**Why this is worth recording.** Moving a check from "absent" to "present in the
application layer" does not make it atomic. The first correction was a real
improvement and still wrong.

**Tests:** `A_worker_that_loses_its_lease_between_reading_and_writing_cannot_complete`,
`The_conditional_update_predicate_matches_no_row_for_a_stale_lease`,
`An_expired_lease_is_refused_even_though_the_owner_still_matches`,
`A_current_lease_holder_can_complete_fail_and_renew`, and
`JobDispatcherTests.Losing_the_lease_mid_execution_cancels_the_running_handler`.
The predicate test and the cancellation test were each verified to fail on the
reverted code.

### A6 — One authoritative clock, resolved at the database write (2026-10-20)

**Finding:** M00-R3-001A. Amendment A5 added the conditional `UPDATE`, but the
expiry comparison bound an application timestamp captured **before** an
asynchronous read:

```csharp
var now = clock.UtcNow;                       // captured here
var job = await LoadForTransitionAsync(...);  // await
... AND lease_expires_at > {now}              // compared against the old value
```

A lease could therefore expire between that capture and the statement executing,
and the stale timestamp still satisfied the comparison. Fail-open: the write was
allowed on an expired lease. A5 closed the _ownership_ gap and left a _time_ gap.

**Clock semantics, stated.** The queue has one time authority, and it is
PostgreSQL:

| Concern                         | Resolved by                                                                         |
| :------------------------------ | :---------------------------------------------------------------------------------- |
| Lease expiry in `WHERE` clauses | `clock_timestamp()`, evaluated while the statement executes                         |
| `completed_at`, `updated_at`    | The same expression, so a row's timestamps and the predicate that admitted it agree |
| Domain pre-checks (`JobRecord`) | `IClock`, as a fast path producing a precise error. **Not** the authority.          |
| Backoff and `run_after`         | `IClock`, because scheduling is a future instant and needs no linearization         |

`clock_timestamp()` rather than `now()`: `now()` returns the _transaction_ start
time, which would reintroduce the same gap for a statement running late in a
transaction.

The claim query uses the same expression. Selection there is fail-closed in any
case — rows are locked `FOR UPDATE` and `TryClaim` re-checks them — but sharing
one expression means the queue has a single time model rather than two that
usually agree.

**The test seam.** Deterministic tests need controllable time, which a server
clock does not offer. `ISimulatedClock` marks a clock whose instants are
simulated; the queue then substitutes that instant for `clock_timestamp()`:

```sql
AND lease_expires_at > COALESCE({simulatedNow}::timestamptz, clock_timestamp())
```

`SystemClock` is not an `ISimulatedClock`, so production always takes the server
clock. The substitution is the only difference between the two paths.

**The limit of that seam, stated plainly.** A simulated instant is bound as a
parameter before the call, so for a simulated clock the value is fixed early —
the very property this amendment removes from production. The simulated path is
therefore used only for _scheduling_ determinism (backoff, delayed jobs,
reclaim-after-expiry) and never to prove write-time semantics. The tests that
prove write-time resolution run with a real `SystemClock` against
`clock_timestamp()`, and reach the window with a command interceptor.

**Tests:**
`A_lease_lapsing_by_elapsed_time_during_the_write_is_refused` is the one that
isolates this amendment — the lease lapses because time passes, which a captured
timestamp accepts and `clock_timestamp()` refuses. Reverting
`SimulatedWriteTime` to the application clock makes it fail;
`A_lease_expiring_between_the_read_and_the_write_cannot_complete` and its
siblings do not catch that regression, because rewriting the stored expiry
backwards is refused by both implementations.

Also:
`A_lease_expiring_between_the_read_and_the_write_cannot_{complete,fail_the_job}`,
`A_lease_expiring_before_the_write_cannot_be_renewed`,
`A_lease_reassigned_between_the_read_and_the_write_cannot_complete` (isolates
the owner predicate), `An_expired_stored_lease_is_refused_by_the_database_predicate`,
`The_current_lease_holder_can_renew_then_complete`.

### A7 — The renewal loop fails closed (2026-10-20)

**Finding:** M00-R3-001B. `RenewLeaseAsync` cancelled the handler only when the
heartbeat returned `false`. If the heartbeat _threw_ — PostgreSQL unreachable —
the renewal task faulted, nothing observed it, and the handler continued
potentially billable work until it returned on its own.

**Correction.** Inability to prove ownership is treated exactly like losing it. A
worker that cannot reach the database cannot know whether its lease expired, and
the safe assumption is that it did.

| Outcome                                   | Action                                            |
| :---------------------------------------- | :------------------------------------------------ |
| Heartbeat returns `false`                 | Mark ownership uncertain, cancel the handler      |
| Heartbeat throws                          | Mark ownership uncertain, log, cancel the handler |
| Coordinated shutdown, or handler finished | Return. Neither is lease loss.                    |

Once ownership is uncertain the dispatcher **records nothing** — no completion,
no failure. A write from here could overwrite the attempt of the worker that
reclaimed the job, and the queue's predicate would refuse it anyway; not
attempting it keeps the intent explicit rather than relying on the fence to catch
us. `LeaseState` carries that decision between the renewal loop and the
execution path.

The renewal task is awaited through `ObserveRenewalAsync`, so an unexpected
fault in the loop is logged rather than discarded. It is not rethrown, because
that runs in a `finally` and would mask the real failure.

**Tests:** `JobDispatcherLeaseTests.A_heartbeat_reporting_loss_cancels_the_running_handler`,
`A_heartbeat_that_throws_cancels_the_running_handler`,
`Shutdown_cancellation_is_not_classified_as_lease_loss`. Each asserts that no
completion and no failure was recorded. Removing either `CancelAsync` call makes
the corresponding test fail by timeout.

### A8 — The claim is one statement, and its lease comes from database time (2026-10-20)

**Finding:** M00-R4-001.

A6 stated that the claim query "uses the same expression" as the fenced writes
and that selection there "is fail-closed in any case". The first half was true
and the second was beside the point. Eligibility was indeed decided by
PostgreSQL — and then `JobRecord.TryClaim` computed the lease from the
**worker's** clock:

```csharp
// eligibility: database time
WHERE run_after <= clock_timestamp() ...
// the lease it produced: application time
LeaseExpiresAt = clock.UtcNow + leaseDuration;
```

So the claim spanned two clocks, and A6's own table — "one time authority, and it
is PostgreSQL" — did not hold for the one write that creates a lease. A6's text
is left as written; this amendment is the correction.

**Why it mattered more than it looks.** The failure is asymmetric:

| Worker clock vs. database | Consequence                                                                                                                                                  |
| :------------------------ | :----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Behind                    | The stored expiry is already past. The worker's own first heartbeat is refused by A5's predicate, and the job returns to the pool having consumed an attempt |
| Ahead                     | The lease outlives its nominal duration. Crash recovery is delayed by the skew                                                                               |

The "behind" case is the bad one, and note what produces it: the fencing added by
A5 and A6 is what converts the skew into lost work. A correct guard fed an
inconsistent operand rejects valid writes as confidently as invalid ones. Clock
skew between an application host and its database is ordinary — NTP step
corrections, suspended VMs, container clock drift — so this is a routine
condition, not an exotic one.

**Decision.** The claim is a single data-modifying statement. PostgreSQL selects
the eligible rows and writes every persisted field of the claim — `state`,
`lease_owner`, `lease_expires_at`, `attempt`, `updated_at` — with all instants
taken from one materialised `clock_timestamp()`:

```sql
WITH write_time AS MATERIALIZED (
    SELECT COALESCE(@simulated_now::timestamptz, clock_timestamp()) AS now
),
candidate AS (
    SELECT j.id FROM app.jobs j
    WHERE (j.state = 'Pending' AND j.run_after <= (SELECT now FROM write_time))
       OR (j.state = 'Running' AND j.lease_expires_at <= (SELECT now FROM write_time))
    ORDER BY j.run_after LIMIT @max_jobs
    FOR UPDATE SKIP LOCKED
)
UPDATE app.jobs j
SET state = 'Running',
    attempt = j.attempt + 1,
    lease_owner = @lease_owner,
    lease_expires_at = (SELECT now FROM write_time) + make_interval(secs => @lease_seconds),
    updated_at = (SELECT now FROM write_time)
WHERE j.id IN (SELECT id FROM candidate)
  AND j.attempt < j.max_attempts
RETURNING j.id
```

`MATERIALIZED` is load-bearing. `clock_timestamp()` is volatile, so inlining it
at each of the four sites would evaluate it four times, and the instant that
admitted a row would differ from the expiry stored against it by however long the
statement took. Materialising pins one instant per statement.

There is no `started_at` column in `app.jobs`; the attempt's start is implied by
`updated_at` on the claiming transition. Nothing to set.

**What is preserved.** `FOR UPDATE SKIP LOCKED` and concurrent-claim safety; the
`maxJobs` batch limit; max-attempt enforcement, now as the SQL predicate
`j.attempt < j.max_attempts`; dead-lettering of exhausted expired rows, moved
into a second statement in the same transaction so its terminal timestamps come
from the same clock; idempotency; and the `ISimulatedClock` seam.

**Two models of one transition.** `JobRecord.TryClaim` remains — the unit tests
use it to put a job into `Running`, and it documents the state machine — but it is
no longer what the queue calls. Two descriptions of one rule drift apart unless
something fails when they do, which was the lesson of the copied-SQL test A5's
round removed. `DurableJobQueueTests.The_sql_claim_agrees_with_the_domain_claim_model`
asserts the SQL and the domain agree on state, attempt and owner. Expiry is
excluded on purpose: disagreeing about it is the point of this amendment. The
dead-letter message is shared rather than restated, via
`JobRecord.ExhaustedLeaseErrorTemplate`, whose `{0}`/`{1}` the infrastructure
rewrites into PostgreSQL `format()` placeholders.

**The seam's limit, restated.** `SkewedSystemClock` in the integration tests is
deliberately **not** an `ISimulatedClock`. The simulated seam substitutes its
instant for the server's, which makes the two clocks indistinguishable — exactly
the property these tests must break. Skewing a real clock against a real server is
the only arrangement in which "which clock did this value come from" is an
answerable question.

**Tests:** `A_worker_clock_an_hour_behind_the_database_still_mints_a_usable_lease`
(asserts the lease is live **and** renewable, not merely plausible-looking),
`A_worker_clock_an_hour_ahead_of_the_database_does_not_lengthen_its_lease`,
`The_persisted_lease_expiry_is_database_claim_time_plus_the_lease_duration`
(bracketed between two server reads, so the assertion needs no tolerance),
`Eight_workers_racing_for_one_job_produce_exactly_one_claim`,
`The_sql_claim_agrees_with_the_domain_claim_model`.

Deriving the expiry from application time instead fails the first three. Removing
`AND j.attempt < j.max_attempts` fails
`A_job_that_repeatedly_crashes_its_worker_is_dead_lettered`.
