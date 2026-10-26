# Response to M00 Correction Round 3

**Decision under response:** CHANGES REQUIRED (round 3)
**Response date:** 2026-10-20 · **Author:** Lead Developer
**Prior rounds:** [round 1](M00_CODEX_REVIEW_RESPONSE.md) · [round 2](M00_RE_REVIEW_RESPONSE.md)

---

## Summary

All three findings accepted and fixed. R3-001 is the one that matters, and the
criticism lands on the same spot for the third time: each round I corrected the
_shape_ of the fence and left a hole in it.

- Round 1: no predicate at all — validated in memory, saved by primary key.
- Round 2: added the predicate, but compared against a timestamp captured before
  an `await`.
- Round 3: the predicate is resolved by PostgreSQL while it executes.

The pattern worth naming: **a guard is only as atomic as the step that evaluates
it.** Moving a check into the application layer did not make it atomic (round 2's
finding), and putting it in SQL did not make it atomic either while its operand
was still bound from the application (this round's finding).

Measured totals, run after the corrections:

| Suite                         | Round 2 |     Now |   Added |
| :---------------------------- | ------: | ------: | ------: |
| Unit                          |     182 | **185** |      +3 |
| Architecture                  |      16 |  **16** |       — |
| Integration (real PostgreSQL) |      44 |  **52** |      +8 |
| Frontend                      |      14 |  **14** |       — |
| Shared contracts              |     137 | **163** |     +26 |
| **Total**                     | **393** | **430** | **+37** |

Integration tests **passed, not skipped** — PostgreSQL 15.19 was reachable and
`skipped: 0` is in the output below.

---

## R3-001 — Lease handling is now fail-closed · **FIXED**

### A. Write-time lease validity

**Root cause.** `PostgresJobQueue` captured `clock.UtcNow` into a local, then
`await`-ed a read, then bound that stale local into the expiry comparison:

```csharp
var now = clock.UtcNow;                        // captured before the await
var job = await LoadForTransitionAsync(...);   // await — time passes here
... AND lease_expires_at > {now}               // compared against the old value
```

A lease expiring in that window still satisfied the comparison, so the write was
allowed on an expired lease. Fail-open. Round 2 closed the _ownership_ gap and
left a _time_ gap.

**Files and lines changed**

| File                                                                     | Change                                                                            |
| :----------------------------------------------------------------------- | :-------------------------------------------------------------------------------- |
| `backend/src/AIDesignStudio.Infrastructure/Jobs/PostgresJobQueue.cs:187` | `SimulatedWriteTime` replaces the captured local                                  |
| …`:206–214`                                                              | `CompleteAsync` — expiry, `completed_at`, `updated_at` all resolved at write time |
| …`:245–249`                                                              | `FailAsync` — same                                                                |
| …`:270–275`                                                              | `TryHeartbeatAsync` — same                                                        |
| …`:89–91`                                                                | `ClaimAsync` aligned to the same expression, so the queue has one time model      |
| `backend/src/AIDesignStudio.SharedKernel/ISimulatedClock.cs`             | New — the test seam, documented as such                                           |

**Correction.** Lease validity is evaluated by PostgreSQL while it executes the
statement:

```sql
AND lease_expires_at > COALESCE({simulatedNow}::timestamptz, clock_timestamp())
```

All four required predicates are preserved: correct id, `state = 'Running'`,
current owner, unexpired **at write time**. Zero affected rows means lease loss
and raises `job.lease_not_held`; nothing is overwritten.

`clock_timestamp()` rather than `now()` deliberately — `now()` is the transaction
start time and would reintroduce the same gap for a statement running late in a
transaction.

**Clock semantics** are documented in **ADR-0005 amendment A6**, including the
table of which concern resolves against which clock, and — stated plainly — the
limit of the test seam: a simulated instant is bound as a parameter before the
call, so the simulated path is used only for _scheduling_ determinism and never
to prove write-time semantics. The tests that prove write-time resolution run
with a real `SystemClock` against `clock_timestamp()`.

### B. Fail closed when the heartbeat throws

**Root cause.** `RenewLeaseAsync` cancelled the handler only on
`TryHeartbeatAsync` returning `false`. An exception — PostgreSQL unreachable —
faulted the renewal task, nothing observed it, and the handler continued
potentially billable work until it returned on its own.

**Files and lines changed** — all in
`backend/src/AIDesignStudio.Worker/JobDispatcher.cs`:

| Lines   | Change                                                                                                                     |
| :------ | :------------------------------------------------------------------------------------------------------------------------- |
| 251–259 | New `LeaseState`: tracks whether ownership can still be proven                                                             |
| 383–391 | Heartbeat **exception** → mark uncertain, log, `CancelAsync`                                                               |
| 397–403 | Heartbeat **false** → mark uncertain, log, `CancelAsync`                                                                   |
| 371–378 | Coordinated shutdown and handler-finished → return, **not** lease loss                                                     |
| 185–196 | Success path records nothing if ownership became uncertain                                                                 |
| 294–316 | `RecordFailureAsync` refuses to write once ownership is uncertain                                                          |
| 269–286 | `ObserveRenewalAsync` awaits the renewal task; unexpected faults are logged, not swallowed and not rethrown from `finally` |

**Behaviour now**

| Outcome                     | Action                                              |
| :-------------------------- | :-------------------------------------------------- |
| Heartbeat returns `false`   | Mark uncertain, cancel handler, record nothing      |
| Heartbeat throws            | Mark uncertain, log, cancel handler, record nothing |
| Shutdown / handler finished | Return. Clean; the lease is left to expire.         |

Inability to prove ownership is treated exactly as losing it: a worker that
cannot reach the database cannot know whether its lease expired, and the safe
assumption is that it did. Documented as **ADR-0005 amendment A7**.

### C. Tests exercise production code

**Root cause of the previous test's inadequacy.**
`The_conditional_update_predicate_matches_no_row_for_a_stale_lease` executed a
**copy of the SQL owned by the test**. It would have kept passing with the
production predicate deleted. It measured the test, not the system. Removed.

**Tests added** — all drive `PostgresJobQueue` or `JobDispatcher` directly:

| # (review) | Test                                                                  | File                               |
| :--------- | :-------------------------------------------------------------------- | :--------------------------------- |
| 1          | `A_lease_expiring_between_the_read_and_the_write_cannot_complete`     | `DurableJobQueueTests.cs`          |
| 1          | `A_lease_expiring_between_the_read_and_the_write_cannot_fail_the_job` | ″                                  |
| 1          | `A_lease_expiring_before_the_write_cannot_be_renewed`                 | ″                                  |
| 1          | `A_lease_lapsing_by_elapsed_time_during_the_write_is_refused`         | ″                                  |
| 2          | `A_lease_reassigned_between_the_read_and_the_write_cannot_complete`   | ″                                  |
| 2          | `A_caller_who_never_held_the_lease_is_refused_before_any_write`       | ″                                  |
| 3          | `An_expired_stored_lease_is_refused_by_the_database_predicate`        | ″                                  |
| 6          | `The_current_lease_holder_can_renew_then_complete`                    | ″                                  |
| 6          | `The_current_lease_holder_can_record_a_failure`                       | ″                                  |
| 4          | `A_heartbeat_reporting_loss_cancels_the_running_handler`              | `JobDispatcherLeaseTests.cs` (new) |
| 5          | `A_heartbeat_that_throws_cancels_the_running_handler`                 | ″                                  |
| 7          | `Shutdown_cancellation_is_not_classified_as_lease_loss`               | ″                                  |

**Determinism.** No `Thread.Sleep`. The concurrency seams are:

- `LeaseExpiryInterceptor` — an EF `DbCommandInterceptor` that mutates the row on
  a separate connection immediately before the queue's `UPDATE` reaches the
  server, landing inside a window otherwise microseconds wide. It asserts
  `Fired`, so a test cannot silently pass without the seam engaging.
- A scripted `IJobQueue` for the dispatcher cases, so each heartbeat outcome is
  immediate and exact. Those tests also assert `CompleteCalls == 0` and
  `FailCalls == 0` — the fail-closed rule, not just the cancellation.

**One honest caveat.** `A_lease_lapsing_by_elapsed_time_during_the_write_is_refused`
uses a real two-second delay inside the interceptor, because that is the only
shape that distinguishes write-time resolution from an early capture: rewriting
the stored expiry _backwards_ is refused by both implementations. It is
deterministic — the delay exceeds the one-second lease by a wide margin — but it
is wall-clock, not simulated. Recorded rather than hidden.

### Deliberate regression results

Each protected behaviour was temporarily reverted, the suite run, and the
implementation restored.

| #   | Regression introduced                                                    | Result                                                                                                                                                                                                                                                                                                                    |
| :-- | :----------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| R1  | Removed `AND lease_owner = {leaseOwner}` (3 sites)                       | **4 failed** / 51: `A_lease_reassigned_between_the_read_and_the_write_cannot_complete`, `A_worker_that_loses_its_lease_between_reading_and_writing_cannot_complete`, `A_lost_lease_makes_the_heartbeat_report_failure_rather_than_throw`, `JobDispatcherTests.Losing_the_lease_mid_execution_cancels_the_running_handler` |
| R2  | Removed `AND lease_expires_at > COALESCE(...)` (3 sites)                 | **5 failed** / 51, including all three read/write-gap tests and `An_expired_stored_lease_is_refused_by_the_database_predicate`                                                                                                                                                                                            |
| R3  | `SimulatedWriteTime` → always the application clock (the R3-001A defect) | **1 failed** / 52: `A_lease_lapsing_by_elapsed_time_during_the_write_is_refused`                                                                                                                                                                                                                                          |
| R4  | Removed `CancelAsync` on heartbeat **exception**                         | **1 failed** / 185: `A_heartbeat_that_throws_cancels_the_running_handler` (timeout)                                                                                                                                                                                                                                       |
| R5  | Removed `CancelAsync` on heartbeat **false**                             | **1 failed** / 185: `A_heartbeat_reporting_loss_cancels_the_running_handler` (timeout)                                                                                                                                                                                                                                    |

R3 is the important row. On the first attempt it did **not** fail, because my
tests only rewrote the stored expiry backwards — which a stale timestamp also
rejects. I added `A_lease_lapsing_by_elapsed_time_during_the_write_is_refused`
specifically to close that, and R3 then failed as it should. Worth stating: the
regression run caught an inadequate test, not just an inadequate implementation.

---

## R3-002 — Schema and type claims are now truthful · **FIXED**

Took the **preferred** option: changed the representation so TypeScript can
express it exactly. No approximation claim was needed.

**Root cause.** `modes` (an enum array) lived inside `color`, whose
`additionalProperties` is a hex string. A TypeScript index signature must cover
every property, so it had to admit `HexColor | ColorMode[]` — which made
`{ primary: "#fff", danger: ["light"] }` type-check while failing validation.
The modelling smell was that `modes` is not a colour role and did not belong
among them.

**Files changed**

| File                                                                                                        | Change                                                                                            |
| :---------------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------ |
| `packages/contracts/schemas/design-system.schema.json`                                                      | `color` → `{ roles, modes }`; `roles` carries `additionalProperties: hexColor`; `color` is closed |
| `packages/contracts/src/index.ts:110–121`                                                                   | `roles` index signature is homogeneous `HexColor \| undefined`                                    |
| `backend/src/.../Stitch/StitchThemeProjector.cs`                                                            | Reads `color.roles.*` and `color.modes`; warning paths are now `/color/roles/<role>`              |
| `StitchThemeProjectionTests.cs`, `contracts.test.ts`, `schema-alignment.test.ts`, `schema-coverage.test.ts` | Fixtures and expected paths updated                                                               |
| `docs/02-architecture/API_CONTRACTS.md`                                                                     | Suite count and description corrected                                                             |

**Verified.** The exact payload the review cited is now a compile error:

```
src/__probe.ts(7,38): error TS2322: Type 'string[]' is not assignable to type 'string'.
```

### `additionalProperties` coverage is generated

The review was right that `schema-coverage.test.ts` claimed generation while
enumerating by hand. It now derives samples from each discovered node's own
schema — `$ref` resolution, pattern-constrained strings, plain strings, closed
objects — and carries a guard:

```
the generator derives samples for every discovered map, or it is classified
```

Any discovered map the generator cannot handle must appear in
`UNSUPPORTED_ADDITIONAL_PROPERTIES` with a reason. That map is currently **empty**,
and a separate test asserts every entry in it states a reason. Closed objects are
also enumerated from discovery rather than listed.

**Deliberate regression:**

| #   | Regression                                 | Result                                                                                                        |
| :-- | :----------------------------------------- | :------------------------------------------------------------------------------------------------------------ |
| R6  | Disabled the pattern-string generator case | **3 failed**: `color.roles.*`, `spacing.*`, `breakpoints.*` accept-case tests                                 |
| R7  | Made `$ref` maps underivable               | **1 failed**: the classification guard — `expected [ 'color.roles.*', 'spacing.*', …(1) ] to deeply equal []` |

No code generation was implemented, and nothing claims it was. ADR-0005 and the
test strategy describe exactly what the three contract suites do.

---

## R3-003 — Documentation reconciled from measured results · **FIXED**

**Root cause.** The round-2 sweep normalised to a figure I had measured _before_
finishing the work, then stale values re-entered from documents the sweep's
regexes missed after Prettier reflowed their tables. I also left a count in the
status file that no longer matched anything.

This round: the suite was run **first**, and every figure below is taken from
that output.

| Item (review)                                                              | Fixed                                                                                                                                     |
| :------------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------- |
| `IMPLEMENTATION_PLAN.md` said "M00 — Foundation (complete)"                | Now "(corrections applied, awaiting re-acceptance)", and the milestone table row matches                                                  |
| `README.md` said M00 complete                                              | Now "corrections applied, awaiting independent re-acceptance"                                                                             |
| `IMPLEMENTATION_STATUS.md` mixed 379, 236, 65 with 393                     | All replaced with measured values; the two remaining historical figures are labelled "at that point" in the change log                    |
| `TEST_STRATEGY.md` listed 38 integration / 65 contract against a 393 total | Table rebuilt from measurement: 185 / 16 / 52 / 14 / 163 = 430                                                                            |
| `API_CONTRACTS.md` said "two suites / 65 tests" but listed three           | Now three suites, 163 tests, each described by what it actually does                                                                      |
| Spike summaries disagreed on how many are verified                         | Both now say **3 verified, 2 partially verified, 1 blocked**, counted from the table, which is declared the source                        |
| SPIKE-06 did not reflect the final lease implementation                    | New "Round 3" section: both defects, the current predicate, an evidence table, and an explicit statement of what the tests do _not_ prove |

Sweep performed with `rg` across `*.md` and `*.cs`, excluding `node_modules`,
`obj`, `bin`, and the historical review documents — which retain their original
counts as dated evidence, per the review's allowance.

---

## Verification — exact output

```
$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```
$ dotnet test -p:TestingPlatformShowTestsFailure=true
  AIDesignStudio.UnitTests          passed (4s 778ms)
  AIDesignStudio.ArchitectureTests  passed (4s 283ms)
  AIDesignStudio.IntegrationTests   passed (13s 448ms)

Test run summary: Passed!
  total: 253
  failed: 0
  succeeded: 253
  skipped: 0
  duration: 13s 914ms
```

Per suite, measured individually:

```
UnitTests:         succeeded=185  skipped=0
ArchitectureTests: succeeded=16   skipped=0
IntegrationTests:  succeeded=52   skipped=0
```

**Integration tests passed rather than skipped.** `skipped: 0`, against
PostgreSQL 15.19.

```
$ dotnet format --verify-no-changes
exit=0        (no output)
```

```
$ dotnet-ef migrations has-pending-model-changes \
    --project backend/src/AIDesignStudio.Infrastructure \
    --startup-project backend/src/AIDesignStudio.Api
Build succeeded.
The Entity Framework tools version '10.0.12' is older than that of the runtime '10.0.13'. ...
No changes have been made to the model since the last migration.
```

The EF tool/runtime version notice is a warning from the globally installed tool,
not a project issue.

```
$ npm test
  frontend:            Tests  14 passed (14)
  packages/contracts:  Tests  163 passed (163)
```

```
$ npm run typecheck
> tsc --noEmit                      (frontend — no output)
> @aids/contracts tsc --noEmit      (no output)

$ npm run lint
> frontend@0.1.0 lint
> eslint                            (no output)

$ npm run format:check
Checking formatting...
All matched files use Prettier code style!

$ npm run build --workspace frontend
✓ Compiled successfully
Route (app)
┌ ◐ /
└ ○ /_not-found

$ npm run audit:prod
found 0 vulnerabilities
```

```
$ dotnet list package --vulnerable --include-transitive
The given project `AIDesignStudio.Api` has no vulnerable packages given the current sources.
... (all nine projects report no vulnerable packages)
```

**Total: 430 tests passing, 0 failing, 0 skipped.**

---

## Remaining external blockers

Unchanged, and none of them were worked around in product code.

| Blocker                            | Exact reason                                                                                                                                                                                                                         | Owner                            |
| :--------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :------------------------------- |
| **Playwright E2E**                 | The Playwright 1.64 Chromium revision is not installed. Specs are discovered and the Next production server starts (confirmed in round 1); only the browser binary is absent. CI runs `npx playwright install --with-deps chromium`. | M01-11                           |
| **Docker Compose**                 | Docker is not installed on this host. PostgreSQL 15.19 was installed natively and all 52 integration tests ran against it, so the dependency is verified while the compose wrapper is not.                                           | M01-12                           |
| **Live Stitch verification**       | No credential and no spend authorization. `BillableOperationsEnabled` remains `false`.                                                                                                                                               | **D-1**, **D-2** — Product Owner |
| **Authentication / authorization** | Out of M00 scope by design; the API must stay off untrusted networks.                                                                                                                                                                | M01-02, M01-03                   |

M01 has not been started. Nothing was committed, pushed, deployed, or sent to a
provider; no billable operation was enabled and no money was spent. No guard or
acceptance criterion was weakened — AC-3.1 and the invariant guards are unchanged
except to be enforced more strictly.

---

M00 corrections applied — awaiting independent re-acceptance.
