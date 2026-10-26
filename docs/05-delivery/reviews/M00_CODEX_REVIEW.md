# M00 Independent Technical, Code, and QA Review

**Milestone:** M00 — Project Bootstrap & Architecture Validation  
**Reviewer:** Codex, acting as Independent Technical Architect, Senior Code Reviewer, and QA Lead  
**Review date:** 2026-10-20  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

M00 has a strong, executable skeleton. The documented modular-monolith layering is present, the API and frontend both start, the initial migration is in sync with the EF model, PostgreSQL-backed integration tests execute rather than skip, and all standard backend and frontend quality gates pass. I independently ran 163 backend tests and 50 frontend/shared-contract tests: 213 passed, 0 failed, and 0 skipped. The production frontend build, live API health probes, local PostgreSQL migration, formatting, linting, typechecking, and runtime dependency audits also passed.

M00 is nevertheless not complete. Seven high-severity findings invalidate foundational correctness claims that M01 is expected to build upon:

1. A stale job worker can complete or fail a lease now owned by another worker, and the dispatcher does not heartbeat long-running work.
2. A job that repeatedly crashes its worker can be reclaimed beyond `MaxAttempts` forever instead of reaching `Dead`.
3. Job idempotency is global rather than organization-scoped, can return one organization's job to another, and does not implement the documented concurrent “return existing” behavior.
4. Aggregate collections expose their mutable backing lists, allowing application code to bypass contract immutability and the no-functional-loss gate.
5. Screen approval mutates the prior approved version before validating the requested approval, so a rejected command can leave the aggregate with no approved version.
6. The singular contract/design-system API and source-of-truth model are not backed by unambiguous aggregate cardinality or database constraints.
7. The Design System lifecycle is claimed as M00-verified without a corresponding lifecycle test suite, discard operation, or payload hash equivalent.

These are implementation and evidence failures, not requests to implement M01–M06 features early.

No P0 issue was found. The unauthenticated M00 API currently exposes health and provider diagnostics only, so the absence of authentication is an explicitly documented scope boundary rather than a current project-data vulnerability. The API must not be exposed to an untrusted network before M01 authorization exists.

M01 should not begin as an accepted milestone on this foundation. The P1 corrections below should be completed and regression-tested first. Separately, live Stitch generation remains externally blocked by a usable provider credential and authorization for a capped billable verification run.

## 2. Review scope and method

All required review documents were present and read:

- `CLAUDE.md`
- `AGENTS.md`
- `README.md`
- `IMPLEMENTATION_PLAN.md`
- `docs/01-product/PRD.md`
- `docs/01-product/MVP_SCOPE.md`
- `docs/02-architecture/SYSTEM_ARCHITECTURE.md`
- `docs/02-architecture/DATA_MODEL.md`
- `docs/02-architecture/API_CONTRACTS.md`
- `docs/03-design/UX_WORKFLOW.md`
- `docs/04-engineering/TEST_STRATEGY.md`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md`

I also read all seven accepted ADRs, all six spike reports and their index, the security requirements and open assumptions, and inspected the domain, application, infrastructure, API, worker, migrations, test projects, frontend, shared contracts, Docker Compose file, and CI workflow. Conclusions below are based on source inspection and commands run during this review, not solely on the implementation status report.

## 3. Acceptance criteria matrix

### 3.1 M00 milestone criteria

|   # | Criterion                                                        | Result   | Independent evidence                                                                                                                                                                          |
| --: | ---------------------------------------------------------------- | -------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
|   1 | Documented repository structure and working application skeleton | **PASS** | Nine .NET projects, Next.js frontend, contracts workspace, buildable solution, and documented boundaries are present.                                                                         |
|   2 | Frontend and API can start locally                               | **PASS** | API listened on `http://localhost:5080`; frontend production server listened on `http://localhost:3100`; `curl /` returned `200 text/html`.                                                   |
|   3 | API health checks pass                                           | **PASS** | `/health`, `/health/live`, and `/health/ready` returned HTTP 200 against a running API; readiness included a healthy database check.                                                          |
|   4 | PostgreSQL migrations run in a clean/real environment            | **PASS** | All 24 integration tests provisioned real PostgreSQL databases and ran without skips. `database update` reported the current local database up to date; pending-model check was clean.        |
|   5 | Unit and integration test infrastructure works                   | **PASS** | 124 unit, 24 integration, 15 architecture, 14 frontend, and 36 contract tests passed.                                                                                                         |
|   6 | Core module boundaries documented                                | **PASS** | Architecture documents and 15 passing architecture tests establish the intended dependency direction. Finding M00-CX-004 identifies an invariant-encapsulation gap not caught by those tests. |
|   7 | New-product and redesign workflows specified                     | **PASS** | PRD, MVP scope, UX workflow, and screen inventory describe both workflows and milestone allocation.                                                                                           |
|   8 | External provider limitations explicitly documented              | **PASS** | Spike reports clearly record missing credentials and unknown response mappings. The capability advertisement inconsistency is captured below.                                                 |
|   9 | M01 broken into implementable tasks                              | **PASS** | Twelve scoped tasks and acceptance criteria exist. Several need P1 foundation corrections added as prerequisites.                                                                             |
|  10 | No unapproved deployment or external spending                    | **PASS** | No deployment/provider generation was performed; billable operations remain disabled by default.                                                                                              |

### 3.2 Foundational product and architecture claims assigned to M00

| Claim                                                               | Result           | Reason                                                                                                                                                                                        |
| ------------------------------------------------------------------- | ---------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Independent web application skeleton                                | **PASS**         | API and Next.js processes build and run independently.                                                                                                                                        |
| Both new-design and redesign directions are represented             | **PASS**         | Both are specified; redesign artifact normalization and inventory domain models exist. Later workflow UI/API features were correctly not required in M00.                                     |
| Design Contract versioning and approval invariants                  | **FAIL**         | Normal lifecycle tests pass, but mutable version collections and ambiguous per-project cardinality bypass the claimed source-of-truth boundary.                                               |
| Design System versioning behaves identically to contract versioning | **FAIL**         | No lifecycle test suite, no discard operation despite the domain message/API shape, and no approval payload hash equivalent.                                                                  |
| Screen version provenance and approval                              | **FAIL**         | Provenance/drift tests pass, but an invalid approval can supersede the existing approved version before throwing. Rejection reasons are not retained.                                         |
| Durable background jobs                                             | **FAIL**         | PostgreSQL locking/backoff tests pass, but stale-worker writes are not lease-fenced and crash reclamation ignores the attempt limit.                                                          |
| Duplicate billable submissions collapse safely                      | **FAIL**         | The unique index prevents duplicate rows globally, but tenant scoping is wrong and the documented race behavior does not return the existing job.                                             |
| Screenshot fidelity is not overstated                               | **PASS**         | A screenshot normalizes to `ArtifactFidelity.Visual` with no semantic tree or tokens; tests passed.                                                                                           |
| No-functional-loss handoff gate                                     | **FAIL**         | `CoveringScreenIds` and the inventory item collection can be externally mutated after validation.                                                                                             |
| Provider abstraction and cost guard                                 | **PASS**         | Capability interfaces, registry, environment-only credentials, subprocess argument handling, and default-off billable guard are present and tested. Live provider mappings remain unverified. |
| Authentication and organization authorization                       | **NOT VERIFIED** | Explicitly planned for M01, not an M00 acceptance failure. Current API surface must remain private.                                                                                           |
| Live Stitch generation and retrieval                                | **BLOCKED**      | Stitch CLI is absent on this host; no usable credential or authorized spend is available.                                                                                                     |
| Docker Compose                                                      | **BLOCKED**      | Docker is not installed in the review environment.                                                                                                                                            |
| Browser E2E suite                                                   | **BLOCKED**      | Test discovery ran, but all four tests stopped before browser execution because the Playwright Chromium binary is absent.                                                                     |

## 4. Findings

Findings are ordered by severity. “Blocks M01” means the finding must be corrected before the affected M01 work is treated as safely implementable or complete.

### M00-CX-001 — P1 — Job completion and failure are not fenced by lease ownership

**Location**

- `backend/src/AIDesignStudio.Application/Jobs/IJobQueue.cs:40-45`
- `backend/src/AIDesignStudio.Infrastructure/Jobs/PostgresJobQueue.cs:106-130`
- `backend/src/AIDesignStudio.Worker/JobDispatcher.cs:97-107`
- `backend/src/AIDesignStudio.Worker/JobDispatcher.cs:138-164`
- `backend/src/AIDesignStudio.Worker/appsettings.json:21-28`

**Evidence**

`CompleteAsync` and `FailAsync` accept only a job id, whereas only `HeartbeatAsync` accepts and verifies `leaseOwner`. The persistence implementation reloads the current row and calls `Succeed` or `Fail` without checking who owns the current lease. `JobRecord.Succeed` likewise has no owner argument.

The dispatcher claims a batch and processes it sequentially. It never calls the implemented heartbeat operation. Defaults permit five jobs to be leased for 300 seconds while each provider command may take up to 180 seconds. A later job in the batch can therefore expire before or during its first execution. Another worker may reclaim it, after which the stale worker can still complete or fail the new owner's attempt.

**Why it matters**

This violates the ownership semantics of ADR-0005 and can corrupt job state. More importantly, two workers may execute the same billable operation while each believes it owns the job. Handler idempotency reduces downstream duplication but does not make an external provider call free or reversible.

**Recommended correction**

Carry an attempt/lease token into `CompleteAsync` and `FailAsync`, and perform a conditional update requiring the current owner/token and `Running` state. Treat a zero-row update as a lost lease, not success. Heartbeat active long-running jobs, and avoid leasing more sequential work than can remain valid; either process the claimed batch concurrently within a configured limit or claim one job per available execution slot. Add database-backed tests proving a stale owner cannot complete/fail and that a long-running handler retains its lease.

**Blocks M01:** **Yes** — M01-07 generation handler and any billable job execution.

### M00-CX-002 — P1 — Crash-reclaimed jobs can exceed `MaxAttempts` indefinitely

**Location**

- `backend/src/AIDesignStudio.Domain/Jobs/JobRecord.cs:106-131`
- `backend/src/AIDesignStudio.Domain/Jobs/JobRecord.cs:172-195`
- `backend/src/AIDesignStudio.Infrastructure/Jobs/PostgresJobQueue.cs:68-89`
- `docs/02-architecture/SYSTEM_ARCHITECTURE.md:174-177`

**Evidence**

The claim query selects every expired `Running` row without considering `attempt` or `max_attempts`. `TryClaim` increments `Attempt` but never rejects or dead-letters an exhausted job. The maximum is enforced only by `Fail`, which a process that crashes never invokes. Repeated lease expiry therefore produces attempts 4, 5, 6, and onward on a job configured for three attempts.

This directly contradicts the documented statement that reclaiming counts as a new attempt “so a job that reliably kills its worker still reaches `Dead` instead of looping forever.” Existing tests cover one reclaim, not repeated crash exhaustion.

**Why it matters**

A poison job or provider interaction that terminates the worker can loop forever, consume worker capacity, and repeatedly trigger external side effects or spend.

**Recommended correction**

Make expired-lease reclamation atomically dead-letter a row whose next attempt would exceed `MaxAttempts`, or model lease expiry as a failed attempt before any new claim. Add unit and PostgreSQL integration tests that advance the clock through every allowed lease and assert the terminal state and exact attempt count.

**Blocks M01:** **Yes** — M01-07 generation jobs.

### M00-CX-003 — P1 — Job idempotency is not tenant-safe and does not meet the documented race contract

**Location**

- `backend/src/AIDesignStudio.Infrastructure/Jobs/PostgresJobQueue.cs:24-50`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:181-196`
- `backend/src/AIDesignStudio.Application/Jobs/IJobQueue.cs:14-25`
- `docs/02-architecture/API_CONTRACTS.md:101-105`
- `docs/02-architecture/SYSTEM_ARCHITECTURE.md:267-270`
- `docs/02-architecture/decisions/ADR-0005-durable-jobs-in-postgresql.md:52-57`

**Evidence**

The pre-insert lookup uses only `(Kind, IdempotencyKey)`, and the unique index is also only `(kind, idempotency_key)`. `OrganizationId` is omitted. Two organizations using the same client-generated key collide; the second call returns the first organization's `JobRecord` object. A read-only query against the migrated database confirmed:

```text
CREATE UNIQUE INDEX ix_jobs_kind_idempotency_key
  ON app.jobs USING btree (kind, idempotency_key)
```

It also confirmed that `jobs.organization_id` has no index, contrary to `SYSTEM_ARCHITECTURE.md:269-270`.

The documented concurrent behavior is also not implemented. Two transactions can both observe no row and add a job. The database prevents the second insert, but `EnqueueAsync` deliberately does not save, catch the unique violation, or re-query; the losing caller receives an exception rather than the existing job promised by the interface and ADR.

**Why it matters**

Once enqueue endpoints exist, this becomes a cross-tenant disclosure and denial-of-service issue as well as an API-contract failure. There is no currently exposed enqueue endpoint in M00, so it is a pre-M01 vulnerability rather than an exploitable current endpoint.

**Recommended correction**

Scope lookup and uniqueness by `OrganizationId`, add the documented organization index, and implement a transaction-safe insert-or-select path that returns the existing same-tenant job under a real race. Ensure future job status/cancel queries are organization-scoped even though worker-internal operations are not. Test same-key/same-tenant, same-key/different-tenant, and truly concurrent inserts.

**Blocks M01:** **Yes** — M01 authorization, generation enqueue, and job status/cancel endpoints.

### M00-CX-004 — P1 — Public aggregate collections permit invariant bypass

**Location**

- `backend/src/AIDesignStudio.Domain/DesignContracts/DesignContract.cs:39`
- `backend/src/AIDesignStudio.Domain/DesignSystems/DesignSystemVersion.cs:34`
- `backend/src/AIDesignStudio.Domain/Screens/Screen.cs:50`
- `backend/src/AIDesignStudio.Domain/Redesign/FeatureInventory.cs:28-34`
- `backend/src/AIDesignStudio.Domain/Redesign/FeatureInventory.cs:75-93`
- `backend/src/AIDesignStudio.Domain/Redesign/FeatureInventory.cs:103-106`
- `backend/tests/AIDesignStudio.ArchitectureTests/ModuleBoundaryTests.cs:219-233`

**Evidence**

The contract, design system, screen, and inventory expose `_versions`/`_items` directly as `IReadOnlyList<T>`. The runtime object is still `List<T>`, so a caller can cast it back and call `Clear`, `Remove`, or `Add`. `FeatureInventoryItem.CoveringScreenIds` is even exposed directly as a public `List<Guid>`.

For example, after `MarkCovered` validates at least one non-empty screen id, a caller can execute `item.CoveringScreenIds.Clear()`. `Coverage` remains `Covered`, while `BlockingItems` checks only for `Unaddressed`; `EnsureReadyForHandoff` then passes an item with no covering screen. Similar mutation can remove approved versions from aggregate histories without using the guarded domain operations.

The architecture test checks public property setters only. It does not detect mutable collection instances returned through read-only interfaces.

**Why it matters**

This bypasses the no-functional-loss guard (ADR-0007) and weakens Design Contract immutability/source-of-truth guarantees (ADR-0002). These are core product invariants, not stylistic encapsulation preferences.

**Recommended correction**

Keep EF-compatible private backing collections but return non-castable read-only wrappers or immutable snapshots. Expose `CoveringScreenIds` as a genuinely read-only collection. Add architecture/domain tests that attempt external mutation and prove the aggregate and coverage gate cannot be changed outside domain methods.

**Blocks M01:** **Yes** for contract/design-system/screen application services; the inventory-specific exploit otherwise blocks the later redesign handoff milestone.

### M00-CX-005 — P1 — A rejected screen approval can supersede the current approved version

**Location**

- `backend/src/AIDesignStudio.Domain/Screens/Screen.cs:120-142`
- `backend/src/AIDesignStudio.Domain/Screens/Screen.cs:218-237`
- `backend/tests/AIDesignStudio.UnitTests/ScreenApprovalTests.cs:67-75`
- `backend/tests/AIDesignStudio.UnitTests/ScreenApprovalTests.cs:173-183`
- `docs/04-engineering/TEST_STRATEGY.md:170-175`

**Evidence**

After contract-drift checks, `ApproveVersion` calls `ApprovedVersion?.Supersede()` before `version.Approve(...)`. The latter is where `InReview` state and a nonblank approver are validated. If the target is Draft, Rejected, already Approved, or the approver is blank, the method throws after mutating the previously approved version to `Superseded`.

The current tests reject an invalid transition and blank approver only when no prior approved version exists. They do not assert the “rejected operation changed nothing” rule for an aggregate that already has an approval.

**Why it matters**

An invalid command can leave the in-memory aggregate with no approved version. That violates AC-4.3/AC-4.5 and the project's own test strategy. Depending on error handling and unit-of-work lifetime, a later save can persist the unintended supersession.

**Recommended correction**

Validate every target transition and approver precondition before superseding the prior approval, or make the transition atomic inside the aggregate. Add regression cases for Draft, Rejected, already Approved, and blank approver with an existing approved version, asserting every version remains unchanged after rejection.

**Blocks M01:** **Yes** — screen review/approval application services.

### M00-CX-006 — P1 — Contract and Design System cardinality conflicts with the singular source-of-truth API

**Location**

- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:47-52`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:82-88`
- `docs/02-architecture/API_CONTRACTS.md:220-237`
- `docs/02-architecture/DATA_MODEL.md:70-79`
- `docs/02-architecture/DATA_MODEL.md:117-131`

**Evidence**

The planned API is singular: `/projects/{id}/contract` and `/projects/{id}/design-system`, returning “current approved version plus any open draft.” The domain factory methods can create unlimited aggregate roots for the same project, and both `project_id` indexes are non-unique. The migrated database independently confirmed only:

```text
CREATE INDEX ix_design_contracts_project_id ON app.design_contracts (project_id)
CREATE INDEX ix_design_systems_project_id   ON app.design_systems (project_id)
```

The filtered approval indexes enforce one approved version per aggregate id, not one contract/design system in force per project. Two aggregates for one project can therefore each have an approved version.

**Why it matters**

The API cannot deterministically answer which contract or design system is “the” project source of truth. A check-then-create application implementation would also be race-prone without a database constraint.

**Recommended correction**

Resolve the cardinality before M01-05/06. The current API and terminology imply one aggregate of each type per project, in which case make `project_id` unique and add concurrent-creation integration tests. If multiple aggregates are intentional, the approved ADRs/API/data model must explicitly define selection and routes before implementation; do not silently choose in application code.

**Blocks M01:** **Yes** — M01-05 and M01-06.

### M00-CX-007 — P1 — Design System lifecycle is claimed complete without implementation/test parity

**Location**

- `docs/01-product/PRD.md:118-126`
- `docs/02-architecture/API_CONTRACTS.md:55-64`
- `docs/02-architecture/API_CONTRACTS.md:220-237`
- `docs/04-engineering/TEST_STRATEGY.md:31-41`
- `backend/src/AIDesignStudio.Domain/DesignSystems/DesignSystemVersion.cs:62-96`
- `backend/src/AIDesignStudio.Domain/DesignSystems/DesignSystemVersion.cs:99-157`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:89-101`

**Evidence**

PRD AC-3.1 says Design System versioning and approval behave identically to FR-2 and marks the behavior M00-verified. API contracts say `design_system.*` has the same shape as `contract.*` and the route surface mirrors the contract surface, which includes draft deletion. The Design System aggregate's own error at line 68 tells callers to “Edit or discard” the open draft, but no discard operation exists. Unlike contract approval, Design System approval stores no payload hash.

There is no `DesignSystemTests` lifecycle suite. Running the backend test list filtered for Design System names found only:

```text
StitchThemeProjectionTests.An_exactly_representable_design_system_projects_without_warnings
```

The test strategy's unit-test inventory likewise contains no Design System lifecycle tests. The unique approval index exists, but no Design System persistence test directly proves it rejects a concurrent second approval.

**Why it matters**

The milestone claims evidence that does not exist and M01-06 is expected to mirror M01-05 on top of this aggregate. Contract lifecycle regressions can therefore be introduced into the Design System path without a failing test.

**Recommended correction**

Bring the Design System lifecycle into the accepted AC-3.1 parity: implement the required draft operation(s), define and store the approval integrity metadata required by the acceptance criteria, and add unit plus real-PostgreSQL tests matching the contract invariants. Do not weaken AC-3.1 to fit the implementation.

**Blocks M01:** **Yes** for M01-06; other M01 tasks are not directly blocked by this finding.

### M00-CX-008 — P2 — Screen rejection requires a reason but discards it

**Location**

- `backend/src/AIDesignStudio.Domain/Screens/Screen.cs:203-216`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:108-137`
- `docs/01-product/PRD.md:133-146`
- `docs/02-architecture/API_CONTRACTS.md:239-250`

**Evidence**

`ScreenVersion.Reject(reason)` validates that `reason` is nonblank and then changes only `ReviewState`; it does not retain the reason. The migrated `screen_versions` table has no rejection reason, rejected-by, or rejected-at columns. The planned API explicitly accepts `{ reason }`.

**Why it matters**

The author cannot retrieve the feedback needed to revise the screen, and the human review record cannot explain why the state changed. Requiring data and immediately discarding it provides only ceremony, not review traceability.

**Recommended correction**

Persist normalized rejection reason and attribution/timestamp from the authenticated reviewer. Define whether resubmission retains prior review history or moves it to a separate review-event record, then test that behavior.

**Blocks M01:** **Yes** for the screen rejection endpoint; **No** for beginning unrelated M01 tasks.

### M00-CX-009 — P2 — `ProjectKind` accepts values outside the documented workflows

**Location**

- `backend/src/AIDesignStudio.Domain/Projects/DesignProject.cs:10-17`
- `backend/src/AIDesignStudio.Domain/Projects/DesignProject.cs:32-50`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:37-44`
- `docs/02-architecture/DATA_MODEL.md:53-66`

**Evidence**

The model documents `NewProduct | Redesign` as the only immutable project kinds. `DesignProject.Create` validates organization and name but not that `kind` is a defined enum member. A caller can pass `(ProjectKind)999`; EF stores enum values as strings and the database has no check constraint.

**Why it matters**

An invalid immutable workflow kind can be persisted, leaving later workflow gates unable to select valid behavior. This becomes reachable when M01 project input binding is implemented unless every boundary duplicates the missing domain check.

**Recommended correction**

Reject undefined enum members in the aggregate and add a database check constraint for the two persisted string values. Add domain and persistence tests. The API should also use explicit string-enum validation and return a clear client error.

**Blocks M01:** **Yes** for M01-04 project creation; **No** for earlier provider/auth work.

### M00-CX-010 — P2 — Shared schema/TypeScript “alignment” is sampled, not enforced

**Location**

- `docs/02-architecture/API_CONTRACTS.md:91-100`
- `packages/contracts/src/contracts.test.ts:23-43`
- `packages/contracts/src/contracts.test.ts:45-200`
- `packages/contracts/src/index.ts`

**Evidence**

The architecture document says 36 tests assert that the JSON Schemas and TypeScript mirrors stay aligned. The tests type selected fixtures and validate those fixtures with AJV, then assert schema identity/version. This catches divergence affecting those examples, but it is not a bidirectional structural comparison. Optional fields can be added, removed, renamed, or have constraints changed in one representation without any selected fixture or compile-time assertion failing.

All 36 tests passed; the issue is the scope of what they prove, not their execution.

**Why it matters**

M01 will use the JSON Schema at the API boundary and TypeScript types in the UI. Undetected drift can make the frontend accept data the API rejects, or omit fields the API requires.

**Recommended correction**

Establish one source of truth and generate the other representation, or add a structural equivalence check with compile-time positive/negative fixtures for every property and required/optional rule. Until then, change evidence claims to “sampled compatibility,” not full alignment.

**Blocks M01:** **No**, but should be corrected while M01 schema validation is wired.

### M00-CX-011 — P2 — Provider readiness can violate its “must never throw” contract

**Location**

- `backend/src/AIDesignStudio.Application/Providers/IDesignProvider.cs:36-40`
- `backend/src/AIDesignStudio.Infrastructure/Providers/Stitch/StitchCommandRunner.cs:196-199`
- `backend/src/AIDesignStudio.Infrastructure/Providers/Stitch/StitchDesignProvider.cs:77-106`

**Evidence**

The runner treats any valid bare JSON value as a successful result. `CheckReadinessAsync` catches exceptions only around `_runner.RunAsync`, then calls `TryGetProperty` on `result.Data` without first verifying `JsonValueKind.Object`. Valid bare JSON such as `[]`, `null`, or a string therefore causes `InvalidOperationException` outside the catch block, despite the interface's explicit “Must never throw” contract.

**Why it matters**

A provider CLI output change or malformed-but-valid JSON can turn diagnostics into an unhandled error and potentially fail the provider readiness endpoint.

**Recommended correction**

Validate the root kind before reading properties, convert unexpected shapes to unavailable/malformed readiness, and add tests for every valid non-object JSON kind.

**Blocks M01:** **No**.

## 5. Architecture assessment

### What aligns with the accepted architecture

- The solution follows the documented dependency direction: Domain uses SharedKernel; Application defines ports and depends on Domain; Infrastructure implements persistence, storage, artifacts, and provider adapters; API and Worker are composition/presentation hosts.
- The seven domain modules are represented without direct navigation across module aggregate roots. Cross-module references are ids as documented.
- PostgreSQL is the only runtime infrastructure dependency. No broker, Redis, second database, or undeclared platform was introduced.
- The job queue uses `FOR UPDATE SKIP LOCKED`, persisted leases, retry classification, backoff, and real PostgreSQL integration tests. The architectural choice is proportionate for the expected volume; the defects are in lease correctness, not in choosing PostgreSQL.
- Provider-specific code is isolated in Infrastructure behind capability interfaces. The default-off billing guard and dry-run planning surface are appropriate.
- Asset storage is behind `IAssetStorage`, hashes streaming content, and confines local storage keys. This is a suitable M00 abstraction without prematurely adding cloud infrastructure.
- CI has separate backend, frontend, and E2E jobs, a real PostgreSQL service, migration drift checking, format/lint/type/build gates, browser installation, and dependency auditing.

### Architectural concerns

- The job API models heartbeat ownership but not completion/failure ownership. That is an application-port design defect, not just a repository implementation bug.
- Claims are leased in batches without coupling lease duration to queue wait plus execution time. The dispatcher execution model and lease model are inconsistent.
- Per-project source-of-truth cardinality is left to convention while the API is singular. This must be resolved at the domain/database boundary before CRUD endpoints are built.
- Returning mutable backing collections defeats aggregate boundaries despite the passing dependency architecture tests.
- Capability discovery is structurally sound, but the terminology “declared capability” currently mixes implemented command shaping with live-verified response mapping. Generation/import/retrieval are advertised while their successful response shapes remain explicitly unverified; variants are withheld for that same reason. M01-01 should define the threshold consistently.

There is no evidence of unnecessary distributed infrastructure or broad scope creep. The Worker and provider adapter go beyond a minimal skeleton but are directly connected to validating accepted architectural risks, so their inclusion is reasonable.

## 6. Security assessment

### Verified controls

- No non-development credential was found in inspected configuration. Provider credentials are inherited from the environment and are not placed in process arguments.
- `ProcessStartInfo.ArgumentList` with `UseShellExecute = false` avoids shell interpolation. Sensitive argument names are redacted from debug logs.
- Provider readiness returns credential names rather than values. Live readiness and health responses did not expose the local connection string or credential values.
- Billable Stitch operations default to disabled in both API and Worker configuration, and unit tests for the guard passed. This review did not enable billing or call a live billable provider operation.
- Local storage validates key shape and resolved path confinement. Path traversal unit tests passed.
- Imported HTML is parsed rather than evaluated; script/style nodes are excluded from normalized structure. Screenshot imports correctly remain visual-only.
- `dotnet list package --vulnerable --include-transitive` reported no vulnerable NuGet packages from current sources. `npm audit --omit=dev --audit-level=high` reported zero runtime vulnerabilities.
- CORS is allow-listed for local development rather than wildcarded.

### Current boundary

Authentication, authorization, organization membership, and trusted reviewer identity are absent by explicit M00 design. The currently implemented endpoints are health and read-only provider diagnostics; no project, asset, job, or approval endpoint is exposed. I therefore do not classify missing M01 authentication as an M00 vulnerability. The API must remain off untrusted networks until M01 implements and tests authorization.

### Security-relevant corrections and future gates

- M00-CX-003 must be resolved before any organization can enqueue or inspect jobs; the present key scope can disclose a different tenant's job object.
- Future `GET /jobs/{id}` and cancellation operations must use an organization-scoped application query. The worker's unscoped internal lookup must not be reused at the API boundary.
- Before upload endpoints are exposed, add request/body size limits, verified content sniffing rather than trusting MIME/extension, decompression/resource limits, and a malware-handling policy appropriate to the deployment.
- Route capture accepts a URL and output/auth-state paths. Before it is exposed, enforce SSRF controls (scheme, resolved address ranges, redirects, DNS rebinding policy) and server-generated confined paths.
- Persist review identity from the authenticated principal rather than accepting free-form `approvedBy`/`waivedBy` values from clients.
- Rate limits and organization spend quotas remain appropriately scheduled for M02, but the M01 billing path should not be exposed publicly without at least authorization and the existing global billing guard.

## 7. Test and execution results

All results below were produced during this independent review.

### 7.1 Environment

| Command                                  | Result                    |
| ---------------------------------------- | ------------------------- |
| `dotnet --version`                       | `10.0.400`                |
| `node --version`                         | `v26.7.0`                 |
| `npm --version`                          | `11.19.0`                 |
| `dotnet-ef --version`                    | `10.0.12`                 |
| `psql --version`                         | PostgreSQL client `15.19` |
| `pg_isready -h localhost -p 5432`        | accepting connections     |
| `docker --version`                       | command not found         |
| `stitch --version` / `command -v stitch` | not installed/on PATH     |
| `npm ls @google/stitch --all`            | empty dependency tree     |

### 7.2 Backend and database

| Command                                                                                                                                               | Exit | Actual outcome                                                                                                                              |
| ----------------------------------------------------------------------------------------------------------------------------------------------------- | ---: | ------------------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet build`                                                                                                                                        |    0 | Build succeeded; 0 warnings, 0 errors; all nine projects built.                                                                             |
| `dotnet test -p:TestingPlatformShowTestsFailure=true`                                                                                                 |    0 | 163 total, 163 succeeded, 0 failed, 0 skipped. Unit, integration, and architecture projects all passed.                                     |
| `dotnet format --verify-no-changes`                                                                                                                   |    0 | No output; no formatting changes required.                                                                                                  |
| `dotnet-ef migrations has-pending-model-changes --project backend/src/AIDesignStudio.Infrastructure --startup-project backend/src/AIDesignStudio.Api` |    0 | `No changes have been made to the model since the last migration.` Build succeeded. Warning: EF tool 10.0.12 is older than runtime 10.0.13. |
| `dotnet-ef database update --project backend/src/AIDesignStudio.Infrastructure --startup-project backend/src/AIDesignStudio.Api`                      |    0 | `No migrations were applied. The database is already up to date. Done.` Same tool/runtime warning.                                          |
| `dotnet list package --vulnerable --include-transitive`                                                                                               |    0 | Every project reported no vulnerable packages from current sources.                                                                         |
| Read-only `pg_indexes`/`information_schema` queries                                                                                                   |    0 | Confirmed actual indexes and absence of Design System payload hash / screen rejection metadata described in findings.                       |

Backend suite accounting:

| Suite                         |  Passed | Failed | Skipped |
| ----------------------------- | ------: | -----: | ------: |
| Unit                          |     124 |      0 |       0 |
| Integration (real PostgreSQL) |      24 |      0 |       0 |
| Architecture                  |      15 |      0 |       0 |
| **Backend total**             | **163** |  **0** |   **0** |

### 7.3 Frontend and shared contracts

The initial plain `npm ci` attempt made no useful progress for more than ten minutes in this environment and was terminated with exit 137. A clean offline-preferred retry completed:

| Command                                                           |    Exit | Actual outcome                                                                                                              |
| ----------------------------------------------------------------- | ------: | --------------------------------------------------------------------------------------------------------------------------- |
| `npm ci --prefer-offline --no-audit --no-fund`                    |       0 | Added 729 packages in 9 minutes. npm warned that the `unrs-resolver` postinstall script was not allow-listed.               |
| `npm test`                                                        |       0 | Frontend: 14/14; contracts: 36/36. Vite emitted a future native-config-loader warning for `vitest.config.ts`; tests passed. |
| `npm run typecheck`                                               |       0 | Frontend and `@aids/contracts` TypeScript checks passed.                                                                    |
| `npm run lint`                                                    |       0 | ESLint passed with no output.                                                                                               |
| `npm run format:check`                                            |       0 | `All matched files use Prettier code style!`                                                                                |
| `npm run build --workspace frontend`                              |       0 | Next.js 16.4.2 production build compiled, typechecked, generated 4/4 static pages, and emitted `/` as partial prerender.    |
| `npm run audit:prod`                                              |       0 | `found 0 vulnerabilities`.                                                                                                  |
| `npm run start --workspace frontend -- --port 3100` plus `curl /` | Started | Next reported ready; `curl` returned `200 text/html; charset=utf-8`. Server was then intentionally stopped.                 |

Frontend/shared test accounting:

| Suite                        | Passed | Failed |
| ---------------------------- | -----: | -----: |
| Frontend unit/component      |     14 |      0 |
| Shared contracts             |     36 |      0 |
| **Frontend/contracts total** | **50** |  **0** |

### 7.4 Live API probes

The API was started with `dotnet run --no-build --project backend/src/AIDesignStudio.Api` and listened on `http://localhost:5080`.

| Request                                         | Result                                                            |
| ----------------------------------------------- | ----------------------------------------------------------------- |
| `GET /health`                                   | HTTP 200, `{"status":"ok"}`                                       |
| `GET /health/live`                              | HTTP 200, Healthy (`self`)                                        |
| `GET /health/ready`                             | HTTP 200, Healthy (`database`)                                    |
| `GET /api/v1/providers/`                        | HTTP 200; returned the registered `google-stitch` capability list |
| `GET /api/v1/providers/google-stitch/readiness` | HTTP 200; `isInstalled:false`, `isUsable:false`, CLI absent       |
| `GET /swagger/v1/swagger.json`                  | HTTP 200                                                          |

The process shut down cleanly after the probes.

### 7.5 Blocked or unavailable checks

| Command/check                                            | Result                                                                                                | Classification                                                                               |
| -------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `docker compose config`                                  | Exit 127: `/bin/bash: docker: command not found`                                                      | **BLOCKED by environment**; compose syntax/runtime not independently verified.               |
| `npm run test:e2e --workspace frontend` with API running | Exit 1; 4 tests reported failed before execution because `chromium_headless_shell-1248` did not exist | **BLOCKED by missing Playwright browser**, not a demonstrated application assertion failure. |
| Live Stitch status/schema/dry-run                        | CLI absent                                                                                            | **BLOCKED by environment**.                                                                  |
| Live Stitch generation/retrieval/import                  | CLI and credentials absent; billable authorization absent                                             | **BLOCKED**. No spend attempted.                                                             |

The Playwright command did successfully start its Next.js web server and discover all four tests. CI contains `npx playwright install --with-deps chromium`, so the browser prerequisite is represented there, but this review did not download the large browser artifact solely to change an environment limitation.

## 8. Technical spike verification

|   # | Spike                           | Classification         | Independent assessment                                                                                                                                                                                                                                                                            | Needed for full verification                                                                                                                            |
| --: | ------------------------------- | ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
|   1 | Stitch SDK/CLI integration      | **PARTIALLY VERIFIED** | Adapter/runner source is present; recorded-output unit tests and readiness API behavior passed. The implementation uses the CLI subprocess rather than a library SDK. The current host has neither `stitch` nor local `@google/stitch`, so prior direct CLI observations could not be reproduced. | Install pinned `@google/stitch` and rerun status/schema/dry-run commands. Authentication is not required for all three according to the recorded spike. |
|   2 | Screen generation and retrieval | **BLOCKED**            | Request shaping and fake response parsing are tested. No successful live response has ever verified `id`, `project`, `canvasUrl`, `html`/`markup`, or render URL mappings.                                                                                                                        | A service-usable Stitch credential, successful auth, and explicit authorization for a capped billable test plan.                                        |
|   3 | Screenshot import               | **PARTIALLY VERIFIED** | Local screenshot normalization, visual-only fidelity, asset storage, and relevant tests passed. The provider `upload screen` success envelope remains unexecuted, and no upload API exists yet.                                                                                                   | Install CLI and execute the credentialed import test plan; M02 supplies the product upload workflow.                                                    |
|   4 | Design artifact normalization   | **VERIFIED**           | HTML/DOM normalization, script exclusion, malformed-input degradation, token candidates, color warning, and screenshot fidelity tests passed.                                                                                                                                                     | No external service required for the demonstrated M00 scope.                                                                                            |
|   5 | Design Contract versioning      | **PARTIALLY VERIFIED** | Lifecycle, hashing, PostgreSQL round-trip, JSONB, and single-approved-per-aggregate tests passed. Mutable collection exposure and per-project cardinality mean the complete source-of-truth invariant is not held.                                                                                | Correct M00-CX-004/006 and add their tests.                                                                                                             |
|   6 | Durable background processing   | **PARTIALLY VERIFIED** | Real PostgreSQL proves migrations, disjoint claims, one crash reclaim, backoff, non-retryable dead-lettering, and basic idempotency. Stale completion fencing, heartbeats, crash exhaustion, tenant scoping, and concurrent return-existing behavior are not verified and contain defects.        | Correct M00-CX-001/002/003 and add multi-worker integration tests.                                                                                      |

The status report's “5 verified, 1 unverifiable” summary is therefore too broad. The executable evidence is substantial, but spikes 1, 3, 5, and 6 have the stated qualification when evaluated against their complete claims.

## 9. Documentation integrity

### Consistent and accurate

- M00 versus later milestone scope is generally explicit; the documents do not pretend that CRUD, auth, generation handlers, review UI, or handoff already exist.
- All seven ADRs match the broad technology choices in code.
- Screenshot fidelity limits and lossy Design System projection are consistently described and tested.
- Missing Stitch credentials, unknown live response shapes, default-off billing, and Docker limitations are openly documented.
- The recorded test totals match this independent run: 163 backend plus 50 frontend/contracts.

### Discrepancies

1. `IMPLEMENTATION_STATUS.md:3-4` declares M00 complete, but the P1 findings above break M00-assigned durable job, source-of-truth, screen approval, Design System, and no-functional-loss invariants.
2. `SYSTEM_ARCHITECTURE.md:269-270` and `SECURITY_REQUIREMENTS.md:157-159` state that `JobRecord.OrganizationId` is indexed. The EF model, migration, and live database show no such index.
3. ADR-0005 and `API_CONTRACTS.md` say concurrent duplicate submissions return the existing job. The current unit of work can only surface the unique constraint violation after both lookups miss.
4. `SYSTEM_ARCHITECTURE.md:176-177` says a crash-loop job reaches `Dead`. The actual claim path has no maximum-attempt check.
5. PRD AC-3.1 and the status report claim Design System lifecycle parity is verified; there is no lifecycle test suite and the aggregate lacks the advertised discard operation and contract-equivalent approval hash.
6. Spike 06 and the status report call durable jobs fully verified, but neither tests nor implementation cover stale-owner completion, heartbeats, or repeated crash exhaustion.
7. The spike index says unverified capabilities are absent or isolated, yet the adapter advertises generation, import, and retrieval while their successful response mappings remain unverified; only variants are withheld for that reason. The distinction should be made consistent.
8. `API_CONTRACTS.md:98-99` overstates the 36 shared-contract tests as proving schema/type alignment; they prove selected fixture compatibility and schema identity.
9. The status report's prior claim that the frontend server could not run in the sandbox is no longer true for this review environment: the production server started and served HTTP 200. Browser E2E remains blocked for the narrower reason that Chromium is not installed.
10. The spike environment names `@google/stitch` 0.11.1, but it is not installed globally or in the current npm dependency tree. Prior command evidence may be valid, but it was not independently reproducible on this host.
11. The planned rejection API accepts a reason, while the domain and database discard it; the documents do not state this limitation.

## 10. Required corrections

### Required before accepting M00 or starting affected M01 implementation

1. Fence job heartbeat, completion, and failure by lease owner/attempt token; make the dispatcher maintain leases and use a safe claim/execution model.
2. Ensure repeated lease expiry consumes the configured attempt budget and deterministically reaches `Dead`.
3. Make idempotency organization-scoped and implement/test atomic return-existing behavior under concurrency. Add the missing job organization index.
4. Replace externally mutable aggregate collection exposures and add invariant-bypass regression tests.
5. Make screen approval validation atomic and prove every rejected approval leaves all versions unchanged.
6. Resolve and enforce contract/Design System cardinality per project before implementing singular endpoints.
7. Complete and independently test the Design System lifecycle required by AC-3.1.
8. Persist screen rejection feedback and reviewer attribution before exposing the rejection endpoint.
9. Reject invalid `ProjectKind` values in the domain and database before project creation is exposed.

### Important but non-blocking for initial M01 work

10. Strengthen or accurately describe JSON Schema/TypeScript alignment evidence.
11. Make provider readiness total over every possible runner result and add malformed-shape tests.
12. Reconcile provider capability advertisement with the level of live verification required by ADR-0003.
13. Update milestone/spike/security documents after corrections so claims reference the new tests rather than existing passing totals alone.

### External/environment prerequisites

- Obtain and validate a server-usable Stitch credential (open assumption A-08 / decision D-1).
- Obtain explicit authorization and a cap before any live billable verification (D-2). Keep `BillableOperationsEnabled=false` until then.
- Run Docker Compose in a Docker-capable environment.
- Install Playwright Chromium and run the four E2E tests with API and PostgreSQL available.

## 11. M01 readiness decision

**Decision: CHANGES REQUIRED**

M00 is not genuinely complete, and M01 cannot safely begin as a whole on the current foundation. The build is healthy and the architecture direction is viable, so this is not a recommendation to redesign the system or add infrastructure. It is a requirement to correct the P1 invariant and queue semantics that M01's CRUD, approval, authorization, and billable-generation work would otherwise embed.

Isolated work that does not rely on the affected behavior—such as reproducing the provider CLI spike in a properly provisioned environment or preparing the authentication integration—can proceed in parallel at the owner's discretion. M00 should remain unaccepted until the P1 corrections have automated regression coverage and the full backend/frontend/database gates are rerun. M01's core provider deliverable will additionally remain blocked until D-1 and D-2 are resolved.
