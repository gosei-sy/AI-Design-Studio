# Response to the M00 Independent Review

**Review:** [M00_CODEX_REVIEW.md](M00_CODEX_REVIEW.md) · **Decision:** CHANGES REQUIRED
**Response date:** 2026-10-20 · **Author:** Lead Developer

---

## Summary

**All eleven findings were reproduced and accepted. All eleven are fixed, each
with regression coverage.** No finding was disputed or deferred.

The review was correct on every point, including the two that were genuine
evidence failures rather than code defects: M00 claimed Design System lifecycle
parity that had no test suite behind it (CX-007), and claimed schema/type
alignment that the tests did not actually prove (CX-010). Those were violations
of this project's own rule 5 — do not claim a feature works without
verification — and that is the more serious category of the two.

| Suite                         |  Before |   After |    Added |
| :---------------------------- | ------: | ------: | -------: |
| Unit                          |     124 | **182** |      +58 |
| Architecture                  |      15 |  **16** |       +1 |
| Integration (real PostgreSQL) |      24 |  **38** |      +14 |
| Frontend                      |      14 |  **14** |        — |
| Shared contracts              |      36 |  **65** |      +29 |
| **Total**                     | **213** | **315** | **+102** |

All 315 pass. Every quality gate passes. One new migration,
`20261020094048_ReviewCorrections`, applied and verified against PostgreSQL
15.19.

---

## Finding-by-finding

### CX-001 — Job completion/failure not fenced by lease ownership · **FIXED**

Confirmed exactly as described. `CompleteAsync`/`FailAsync` took only a job id,
the dispatcher never heartbeated, and a batch of 5 with a 300s lease against a
180s provider timeout meant later jobs could expire before being started.

**Changes**

- `JobRecord.Succeed` and `Fail` now take a `leaseOwner` and go through
  `EnsureLeaseHeldBy`, which raises `job.lease_not_held` on a stale write.
- `IJobQueue.CompleteAsync`/`FailAsync` carry the lease owner;
  `HeartbeatAsync` became `TryHeartbeatAsync` returning `bool`, so a worker
  _learns_ it lost the lease instead of taking an exception.
- `JobDispatcher` renews the lease on a background loop for the duration of each
  handler, cancels renewal when the handler returns, and tolerates a lost lease
  on the failure path rather than throwing a second error out of it.
- `BatchSize` default changed 5 → **1**, with the reasoning recorded on the
  property: sequential execution means a batch of N leases work that will not
  start for up to (N−1) handler runtimes. Raising it now requires concurrent
  execution.
- `HeartbeatIntervalSeconds` (60s) added.

**Tests** — `A_stale_lease_owner_cannot_complete_or_fail_the_job`,
`Only_the_current_lease_owner_can_complete_the_job`,
`Heartbeating_keeps_a_long_running_job_out_of_reach_of_other_workers`, and
against the database:
`A_stale_worker_cannot_complete_a_job_another_worker_reclaimed`,
`A_lost_lease_makes_the_heartbeat_report_failure_rather_than_throw`.

### CX-002 — Crash-reclaimed jobs could exceed `MaxAttempts` forever · **FIXED**

Confirmed. `TryClaim` incremented `Attempt` with no ceiling, and the claim query
ignored the budget — so a job configured for three attempts was reclaimed as
4, 5, 6 … The documentation claimed the opposite, which made this a code _and_
documentation defect.

**Changes** — `TryClaim` refuses a job whose budget is spent;
`HasExpiredLease` and `IsAttemptBudgetExhausted` expose the condition; the new
`FailExhaustedLease` terminates it. `PostgresJobQueue.ClaimAsync` dead-letters
such rows during the sweep — the only place it can happen, since a dead process
never calls `Fail` — and logs it at error level.

**Tests** — `Repeated_lease_expiry_exhausts_the_attempt_budget` walks the full
budget; `A_job_that_repeatedly_crashes_its_worker_is_dead_lettered` does the
same against PostgreSQL and asserts it stays dead on later sweeps.

### CX-003 — Idempotency not tenant-safe; race contract unmet · **FIXED**

Confirmed on all three sub-points, including that `jobs.organization_id` had no
index despite two documents claiming it did.

**Changes**

- Unique index is now `(organization_id, kind, idempotency_key)`; the lookup
  filters by organization.
- `ix_jobs_organization_id` added, making the documentation true.
- `FindByIdempotencyKeyAsync` added so a caller that loses the race can resolve
  the winner's job.
- `IJobQueue.EnqueueAsync`'s documentation now states the real semantics
  precisely, including why `EnqueueAsync` cannot itself catch the violation: it
  does not own the commit, which is what makes enqueueing atomic with the
  triggering state change (ADR-0005).

On the review's suggestion to implement insert-or-select inside `EnqueueAsync` —
that would require owning the transaction, which would give up atomic enqueue.
The recovery path is therefore exposed to the caller and tested, and the ADR
amended to say so rather than continuing to promise behaviour the design cannot
provide.

**Tests** — `The_same_idempotency_key_in_two_organizations_creates_two_jobs`
(the cross-tenant case), `The_database_rejects_a_duplicate_key_within_one_organization`,
and `Losing_an_enqueue_race_resolves_to_the_winning_job`, which drives a real
race and asserts exactly one job exists afterwards.

### CX-004 — Public aggregate collections permitted invariant bypass · **FIXED**

Confirmed, including the specific exploit: `MarkCovered` validates, caller calls
`CoveringScreenIds.Clear()`, item stays `Covered` with nothing covering it, and
`EnsureReadyForHandoff` passes — ADR-0007 defeated in one call.

**Changes** — all four aggregate collections return `AsReadOnly()` wrappers.
`FeatureInventoryItem.CoveringScreenIds` moved to a private backing field with a
read-only property, mapped to the same `covering_screen_ids` column via EF field
mapping (no schema change).

**Tests** — `Covering_screens_cannot_be_cleared_from_outside_the_aggregate`,
`A_catalogued_feature_cannot_be_removed_from_outside_the_aggregate`, plus
history-mutation tests on all four aggregates, and a round-trip test proving the
field mapping works.

The review also noted the architecture test only checked public setters. Added
`Domain_entities_never_hand_out_a_mutable_collection`, which instantiates each
aggregate and inspects what is actually returned rather than the declared type.
**Verified to fail on the regression**: reintroducing `=> _versions` produces
`["DesignContract.Versions returns a mutable List`1"]`.

### CX-005 — A rejected approval could supersede the current approval · **FIXED**

Confirmed. `ApproveVersion` superseded first and validated second, so an invalid
command left the aggregate with no approved version.

**Changes** — `ScreenVersion.EnsureApprovable` validates state and approver
without mutating; `ApproveVersion` calls it before touching anything.

**Tests** — `A_rejected_approval_leaves_an_existing_approval_intact` as a theory
over all four cases the review named (Draft, Rejected, already-Approved, blank
approver), each **with an existing approval** — which is what the original tests
lacked — plus `Contract_drift_leaves_an_existing_approval_intact`.

### CX-006 — Per-project cardinality not enforced · **FIXED**

Confirmed: `project_id` was non-unique on both tables while the API is singular.

**Resolution** — the review asked us to decide rather than let application code
choose. **Decision: one contract and one design system per project**, which is
what the singular API, the "source of truth" language and the existing
`feature_inventories` unique index already implied. `project_id` is now UNIQUE on
`design_contracts` and `design_systems`.

**Tests** — `A_project_cannot_have_two_design_contracts`,
`A_project_cannot_have_two_design_systems`.

### CX-007 — Design System lifecycle claimed without parity · **FIXED**

Confirmed, and the most serious finding in evidence terms: PRD AC-3.1 claimed
M00-verified parity, there was no `DesignSystemTests`, `DiscardDraft` did not
exist despite the aggregate's own error telling callers to use it, and there was
no approval hash.

Per the review's instruction not to weaken AC-3.1 to fit the implementation, the
implementation was brought up to the criterion.

**Changes** — `DesignSystem.DiscardDraft` added; `DesignSystemVersion.TokensHash`
(SHA-256, fixed at approval) added with `ComputeHash`, mirroring the contract.

**Tests** — new `DesignSystemTests`, 20 cases mirroring `DesignContractTests`
case for case, plus three integration tests including
`The_database_rejects_a_second_approved_design_system_version`, which the review
noted had never been proven directly.

### CX-008 — Rejection reason required then discarded · **FIXED**

**Changes** — `Reject(reason, rejectedBy, clock)` persists `RejectionReason`,
`RejectedBy` and `RejectedAt`.

**Tests** — `A_rejection_retains_its_reason_reviewer_and_timestamp`,
`A_rejection_requires_a_reviewer_identity`, and a persistence round trip.

On the review's open question — whether resubmission retains prior review
history: for now the fields hold the most recent rejection, and resubmission
leaves them in place as the reason the author is revising. A full review-event
history is M02 work with the review UI, and is recorded as such rather than
implied.

### CX-009 — `ProjectKind` accepted undefined values · **FIXED**

**Changes** — `Enum.IsDefined` guard raising `project.invalid_kind`, plus a
database check constraint `ck_projects_kind`.

**Tests** — a theory over `0`, `3`, `999`, `-1`, and
`The_database_rejects_an_unknown_project_kind` asserting the constraint by name.

### CX-010 — Schema/type alignment was sampled, not enforced · **FIXED**

The review was right that the claim overstated the evidence.

**Changes** — new `schema-alignment.test.ts` implementing a genuinely
bidirectional check. Declared key lists are pinned to the TypeScript types at
compile time via an `AssertSameKeys` conditional type, and compared to the
schemas at runtime. Every required field is asserted to fail when omitted, and
every optional field to pass.

**Verified in both directions**: adding a property to the schema fails the
runtime test; removing one from TypeScript fails `tsc` with
`Type 'true' is not assignable to type 'never'`.

The overstated claim in `API_CONTRACTS.md` has also been corrected.

### CX-011 — Readiness could throw, violating its contract · **FIXED**

**Changes** — `CheckReadinessAsync` checks `JsonValueKind.Object` before reading
properties and returns an unavailable readiness describing the unexpected shape.

**Tests** — a theory over `[]`, `null`, a string, a number, `true` and an array
of objects, plus `Readiness_never_throws_whatever_the_runner_returns` which
includes a runner that throws outright.

---

## Documentation discrepancies

All eleven from review §9 are resolved. Most became true once the code was
fixed; these needed separate correction:

| #   | Discrepancy                                   | Resolution                                                                                |
| :-- | :-------------------------------------------- | :---------------------------------------------------------------------------------------- |
| 1   | Status declared M00 complete                  | Now **"corrections applied, awaiting re-acceptance"**                                     |
| 2   | `organization_id` index claimed but absent    | Index added — the claim is now true                                                       |
| 3   | Race returns the existing job                 | ADR-0005 amended with the real semantics and the recovery path                            |
| 4   | Crash-loop reaches `Dead`                     | Now true, and tested                                                                      |
| 5   | Design System parity verified                 | Now true, with 23 tests                                                                   |
| 6   | Durable jobs fully verified                   | Spike 06 now lists fencing, heartbeat and exhaustion coverage                             |
| 7   | Capability advertisement threshold            | ADR-0003 amended to define the two levels explicitly (below)                              |
| 8   | 36 tests prove alignment                      | Corrected; replaced with a real bidirectional check                                       |
| 9   | Frontend server could not run                 | **Corrected — the reviewer started it.** Criterion 2 is now PASS                          |
| 10  | `@google/stitch` not reproducible on the host | SPIKE-01 now states that evidence came from a downloaded tarball, with the exact commands |
| 11  | Rejection reason discarded                    | Fixed in code; API contract documents the persisted fields                                |

### On capability advertisement (discrepancy 7)

The review correctly identified that "declared capability" conflated two
different things. ADR-0003 is amended to name them:

- **Request-verified** — request shaping is verified (via `--schema` and
  `--dry-run`) but no live success response has been mapped. Generation, import,
  retrieval and capture are here.
- **Response-verified** — a live call has been made and its response mapping is
  covered by a test fixture. Nothing is here yet.

A capability is declared at **request-verified**, because the alternative is
declaring nothing at all until credentials exist, which would make the registry
useless during M00–M01. `IScreenVariantCapability` is withheld because it is not
even request-verified — there is no `--dry-run` evidence for it. The readiness
endpoint already reports usability separately, so a caller can distinguish
"declared" from "callable right now". M01-01 records response-verification as it
is obtained.

---

## Spike classifications

The review found "5 verified, 1 unverifiable" too broad. Adopted, with the
post-fix position stated:

| Spike                       | Review             | Now                    | Note                                                                                                                                       |
| :-------------------------- | :----------------- | :--------------------- | :----------------------------------------------------------------------------------------------------------------------------------------- |
| 01 Stitch auth/capabilities | Partially verified | **Partially verified** | Unchanged. Evidence is real but was gathered from a downloaded tarball; the CLI is not installed on the review host. SPIKE-01 now says so. |
| 02 Screen generation        | Blocked            | **Blocked**            | Unchanged. Needs D-1/D-2.                                                                                                                  |
| 03 Screenshot import        | Partially verified | **Partially verified** | Unchanged. Local path verified; provider upload envelope unexecuted.                                                                       |
| 04 Artifact normalization   | Verified           | **Verified**           | Unchanged.                                                                                                                                 |
| 05 Versioned contracts      | Partially verified | **Verified**           | CX-004 and CX-006 fixed and tested, which were the stated qualifications.                                                                  |
| 06 Durable jobs             | Partially verified | **Verified**           | CX-001/002/003 fixed, with multi-worker integration tests — the stated gaps.                                                               |

---

## Verification

```
dotnet build                          0 warnings, 0 errors
dotnet format --verify-no-changes     clean
dotnet test                           236 passed, 0 failed, 0 skipped
  Unit                                182
  Architecture                         16
  Integration (real PostgreSQL)        38
npm test                              14 frontend + 65 contracts
npm run typecheck / lint              clean
npm run format:check                  clean
npm run audit:prod                    0 vulnerabilities
dotnet-ef migrations has-pending      "No changes ... since the last migration"
dotnet-ef database update             ReviewCorrections applied
```

Schema objects confirmed directly in PostgreSQL:

```
ix_jobs_organization_id_kind_idempotency_key  UNIQUE (organization_id, kind, idempotency_key)
ix_jobs_organization_id                       (organization_id)
ix_design_contracts_project_id                UNIQUE (project_id)
ix_design_systems_project_id                  UNIQUE (project_id)
ck_projects_kind                              CHECK (kind IN ('NewProduct','Redesign'))
design_system_versions.tokens_hash            added
screen_versions.rejection_reason/_by/_at      added
```

---

## Still outstanding

Not fixed, because they are environmental rather than code defects. The review
classified them the same way.

| Item                              | Status                                                                                             | Resolves in             |
| :-------------------------------- | :------------------------------------------------------------------------------------------------- | :---------------------- |
| Live Stitch generation (SPIKE-02) | **Blocked** — no credential, no authorized spend                                                   | M01-01, needs D-1 + D-2 |
| `docker-compose.yml` unexecuted   | **Blocked** — Docker absent on both hosts                                                          | M01-12                  |
| Playwright E2E unexecuted         | **Blocked** — Chromium not installed; reviewer confirmed discovery works and the web server starts | M01-11                  |
| Authentication / authorization    | **Not in M00 scope**                                                                               | M01-02, M01-03          |

The review's security recommendations for not-yet-built surfaces — upload size
and content-sniffing limits, SSRF controls on route capture, identity from the
authenticated principal rather than client-supplied `approvedBy`, and
organization-scoped job queries — are recorded in
[SECURITY_REQUIREMENTS.md](../../04-engineering/SECURITY_REQUIREMENTS.md) §10 as
gates on the milestones that introduce them.

---

## Milestone position

M00 is **not** self-declared complete. The P1 corrections the review required
are implemented and regression-tested, and the documentation now matches the
code, but acceptance is the reviewer's and the architect's call.

Per rule 9, M01 has not been started.
