# Implementation Plan

| Milestone | Scope                                                  | Status                                                             |
| :-------- | :----------------------------------------------------- | :----------------------------------------------------------------- |
| **M00**   | Foundation, architecture validation, technical spikes  | ✅ **Complete — independently re-accepted, APPROVED** (2026-10-21) |
| **M01**   | Authentication, core CRUD, live generation             | 🚧 **In progress** — authorized by the Product Owner 2026-10-21    |
| **M02**   | Review UI, import UI, redesign workflow, Azure storage | Not started                                                        |
| **M03**   | Handoff packages, variant exploration, history         | Not started                                                        |

M00 is closed. **M01 is in progress**, explicitly authorized by the Product Owner
on 2026-10-21 — that authorization is what closed rule 9's start gate, not the
M00 approval by itself.

**Wave 3 is the authorized scope**, and within it **M01-03 only**. Wave 1 is
complete and its corrections approved; **M01-02 was independently accepted on
2026-11-03**. Work outside the authorized wave still needs authorization.
**M01-04 was explicitly authorized on 2026-11-19 ahead of M01-03's acceptance,
and M01-05 the same day on the owner's delegation** — the sequencing below still
holds for everything after them, and both stay unmerged until what they build on
is accepted. **M01-06 has not started.**
No task is complete until its acceptance criteria have been executed with
evidence. Current wave state:

| Task         | State                                                       | Gate                                                                                                                                                                                                                        |
| :----------- | :---------------------------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **M01-01**   | 🚧 Offline prep done                                        | Live execution blocked on **D-1/D-2**; A-08 and A-09 stay open                                                                                                                                                              |
| **M01-02**   | ✅ **APPROVED with non-blocking findings**                  | Independently accepted 2026-11-03. D-8 unresolved (pre-production gate); MFA recovery codes remain a recorded gap                                                                                                           |
| **M01-03**   | 🔬 **Round-2 corrections applied — awaiting re-acceptance** | Three review rounds, nine findings, all corrected. W3-RR-001 accepted in round 2. **D-3 and D-9 resolved** (ADR-0009, as amended by A1–A4). Evidence: [round-2 response](docs/05-delivery/reviews/M01_WAVE3_R2_RESPONSE.md) |
| **M01-04**   | 🔬 **Implemented, not reviewed**                            | Authorized 2026-11-19 ahead of M01-03 acceptance; branch `gosei-sy/m01-04-project-endpoints`, unmerged until M01-03 is accepted. Assumption A-16 open                                                                       |
| **M01-05**   | 🔬 **Implemented, not reviewed**                            | Delegated authorization 2026-11-19; branch `gosei-sy/m01-05-contract-endpoints`, stacked on M01-04, unmerged. ADR-0010 proposed                                                                                             |
| M01-06       | ⛔ Not started                                              | Waits on its predecessor being accepted                                                                                                                                                                                     |
| M01-07/08/09 | ⛔ Not started                                              | Gated on M01-01 resolving **A-08** positively                                                                                                                                                                               |
| M01-10       | ⛔ Not started                                              | Needs M01-04 … M01-09 accepted                                                                                                                                                                                              |
| **M01-11**   | ✅ **Evidence complete — awaiting acceptance**              | **9/9 specs pass locally and in CI** (first CI run 44509571762, 2026-11-19), including the browser auth flow                                                                                                                |
| **M01-12**   | ⛔ **BLOCKED**                                              | Docker is not installed in this environment                                                                                                                                                                                 |

Three items carried over from M00 — see
[Carried into M01](#carried-into-m01-from-m00).

---

## M00 — Foundation ✅ Complete

**Independently re-accepted on 2026-10-21, decision APPROVED.** Detailed status
in
[docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md).

Delivered **at M00 closure**: 9 .NET projects, 7 domain modules with their
invariants, a capability-based provider abstraction with a Stitch adapter, a
durable job queue, artifact normalization, local asset storage, a Next.js
skeleton, shared contract schemas, CI, and 436 passing tests. (That figure is
M00's; the current total is in
[IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md).)

Six spikes run: **three verified, two partially verified, one blocked** on
credentials with a reproducible test plan. The authoritative split, per spike, is
the table in [docs/06-spikes/README.md](docs/06-spikes/README.md).

### Review history

Four independent rounds, all returning **CHANGES REQUIRED**, all correct; then
APPROVED.

| Round                   | Findings     | Response                                                                              |
| :---------------------- | :----------- | :------------------------------------------------------------------------------------ |
| 1                       | 7 P1 · 4 P2  | [M00_CODEX_REVIEW_RESPONSE.md](docs/05-delivery/reviews/M00_CODEX_REVIEW_RESPONSE.md) |
| 2                       | 1 P1 · 2 P2  | [M00_RE_REVIEW_RESPONSE.md](docs/05-delivery/reviews/M00_RE_REVIEW_RESPONSE.md)       |
| 3                       | 1 P1 · 2 P2  | [M00_R3_RESPONSE.md](docs/05-delivery/reviews/M00_R3_RESPONSE.md)                     |
| 4                       | 1 P1 · 2 P2  | [M00_R4_RESPONSE.md](docs/05-delivery/reviews/M00_R4_RESPONSE.md)                     |
| Re-acceptance (round 5) | **APPROVED** | —                                                                                     |

All twenty findings fixed with regression coverage. Verified result at closure:

```
436 passed, 0 failed, 0 skipped
185 unit · 16 architecture · 57 integration · 14 frontend · 164 contracts
```

Integration tests **passed, not skipped**, against PostgreSQL 15.19.

### Carried into M01 from M00

Three items were **not** resolved in M00 and are explicitly unresolved at
closure. Approval of M00 did not close them; each is owned by an M01 task.

| Item                                                                       | Status at M00 closure                                                                                                                             | Owning task |
| :------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------ | :---------- |
| **D-1 / D-2** — Stitch account, credential, and capped spend authorization | ⛔ **Unresolved.** Product Owner decision; no provider call was ever made                                                                         | **M01-01**  |
| **M01-11** — Playwright Chromium not installed; E2E specs unexecuted       | ⛔ **Unresolved at M00 closure.** 4 specs written, `browserType.launch: Executable doesn't exist`. _(Resolved later in M01-11: now 7/7 locally.)_ |
| **M01-12** — Docker not installed; `docker-compose.yml` unexecuted         | ⛔ **Unresolved.** `docker: command not found` (exit 127)                                                                                         | **M01-12**  |

**As at M00 closure** the E2E specs and `docker-compose.yml` were written but
unverified, and neither was counted toward the 436. That is a historical
snapshot, not the current state: the E2E suite now passes **9/9 locally and in
CI** (M01-11 — first CI run 2026-11-19, awaiting acceptance), while
`docker-compose.yml` is still unexecuted because Docker is absent (M01-12).

---

## M01 — Authentication, core CRUD, live generation

**Goal:** a team can author and approve a contract and design system, and
generate a real screen against them.

**Entry criteria:** M00 re-accepted — ✅ **met** (APPROVED, 2026-10-21), and
Product Owner authorization to begin — ✅ **met** (2026-10-21).

**A-08 is deliberately not an entry criterion.** M01-01 is the task that
_resolves_ A-08, so requiring A-08 up front would make the milestone
unstartable — the previous wording said exactly that and was self-contradictory.

The constraint is a gate inside the milestone, not at its boundary:

> **M01-01 may start immediately. No provider-dependent implementation beyond
> M01-01 may proceed until A-08 is resolved.**

**Independent of A-08:** M01-02 through M01-06, M01-11 and M01-12 — subject to
their own listed dependencies, which A-08 does not change. M01-04 waits on M01-03
being **accepted**, and M01-05 and M01-06 wait on M01-04. M01-03's own
prerequisites — the D-3 role model and D-9 account proofing — are both resolved.

**Gated by M01-01/A-08:** M01-07, M01-08 and M01-09, and consequently the
**complete M01-10 UI**. M01-10 depends on M01-04 … M01-09, and its acceptance
criteria include generate-with-plan and job status, so M01-10 cannot be
completed or accepted until A-08 is resolved — regardless of how much of the
authoring UI can be built against M01-04 … M01-06 in the meantime.

If A-08 resolves negatively, M01-01's escalation path applies: an ADR and
re-approval, not a workaround.

### Task list

Tasks are ordered by dependency. Each has acceptance criteria that must be
demonstrable, not asserted.

---

#### M01-01 — Resolve the Stitch server-side credential question

**Size:** S (investigation) · **Blocks:** M01-07, M01-08, M01-09 · **Risk:** 🔴 High

**Carried from M00 as unresolved: D-1, D-2, A-08.**

**No preceding engineering-task dependency.** Nothing in M01 must be built before
this task, so it is sequenced first deliberately: it can change the architecture,
and every provider-dependent task behind it is gated on its outcome.

**It is not, however, unblocked.** Executing its live Stitch test plan requires
two Product Owner decisions that remain open:

| Decision | What it must provide                                | Owner         |
| :------- | :-------------------------------------------------- | :------------ |
| **D-1**  | A Stitch account with canvas access, and an API key | Product Owner |
| **D-2**  | Explicit authorization to spend, with a cap         | Product Owner |

Neither can be resolved by engineering. **No provider call has been made, and
none will be made before both exist.** The investigative and documentation
portions of this task — reading the CLI schema, recording the test plan,
preparing fixtures — can proceed without them; the live calls cannot.

Determine whether `STITCH_API_KEY` can be provisioned for unattended
server-side use, whether it is per-user or per-organization, its rate limits,
and its cost per generation.

Execute [SPIKE-02](docs/06-spikes/SPIKE-02-stitch-screen-retrieval.md)'s test
plan, steps 1–4.

**Acceptance:**

- A Stitch account exists with canvas access.
- `GET /api/v1/providers/google-stitch/readiness` returns `isUsable: true`.
- The real response shapes for `generate screen` and `get screen` are recorded
  as test fixtures.
- Observed cost and latency per call are recorded.
- A-08 and A-09 are resolved or escalated in `OPEN_ASSUMPTIONS.md`.

**If it resolves negatively** (no server-usable credential): stop and escalate.
Per-user delegated OAuth changes the authorization model and the job payload.
That needs an ADR and re-approval — **not a workaround**.

---

#### M01-02 — Authentication

**Size:** M · **Depends on:** **D-7** (resolved — Option A, ADR-0008) · **Risk:** 🟡 Medium

**State:** ✅ **Accepted 2026-11-03** — APPROVED with non-blocking findings,
after six independent review rounds (five CHANGES REQUIRED, then approval). Two
findings remain open and were **not** closed by the approval: **D-8**
(password-reset delivery, a fail-closed pre-production gate) and **MFA recovery
codes** (risk R-5).

Authentication does **not** depend on A-01 or D-3: the role model is
authorization and belongs to M01-03.

**Acceptance:**

- All non-health endpoints require authentication; unauthenticated requests get
  `401` with problem details.
- A user identity is available to the domain for `ApprovedBy` / `WaivedBy`.
- Integration tests cover authenticated and unauthenticated paths.
- Security headers (CSP, HSTS, X-Content-Type-Options) set — closes S-25.

**Decisions:** **D-7 resolved** (Option A). **D-3 resolved** — two roles,
Member and Approver — which M01-03 consumes, not this task.

**Raised by the Wave 2 review:** **D-8** (a password-reset delivery transport;
recovery is unavailable without it) remains open, and **D-9** (account proofing)
was **resolved on 2026-11-03** by ADR-0009 — trusted operator provisioning.

**D-8's gate was settled on 2026-11-02** (ADR-0008 **A2**, Proposal 2 Option 2):
it is a **fail-closed pre-production gate**, not a bar on M01-02 acceptance. **No
Production deployment may proceed until D-8 is resolved**, enforced by the host
refusing to start. The transport itself is still unauthorized, so recovery
remains undelivered — which the independent acceptance of M01-02 on 2026-11-03
recorded as a non-blocking finding rather than closing.

---

#### M01-03 — Organization-scoped authorization

**Size:** M · **Depends on:** M01-02 (**accepted 2026-11-03**) · **Risk:** 🟡 Medium

**State:** 🔬 implemented in Wave 3 and submitted for independent review — not
accepted. **D-9 is resolved** by
[ADR-0009](docs/02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md):
account proofing is trusted operator provisioning, public registration grants
nothing and is unmapped outside Development, and one active membership per user
is modelled explicitly.

**Acceptance:**

- Every query is organization-scoped; a cross-organization read returns `404`,
  not `403` (no existence disclosure).
- The **two roles from D-3 — Member and Approver — are enforced** on each
  endpoint. A user may hold both; only Approver may perform approval, rejection
  or waiver transitions.
- An integration test proves a user in organization A cannot read organization
  B's project, contract, screen or asset.
- An architecture or analyzer test catches a query missing its organization
  filter. _(Design this deliberately — it is the rule most easily forgotten
  later.)_

---

#### M01-04 — Project endpoints

**Size:** S · **Depends on:** M01-03

**State:** 🔬 **implemented 2026-11-19, not reviewed, not accepted.** Started
**before M01-03's re-acceptance under explicit authorization** (recorded in
IMPLEMENTATION_STATUS §9), on branch `gosei-sy/m01-04-project-endpoints`, not to
be merged until M01-03 is accepted — it builds on M01-03's authorization model.

**Acceptance:**

- `POST /api/v1/projects` returns `201`; `GET` list and by-id are
  organization-scoped. _Evidence: `ProjectEndpointsTests` —
  `Creating_a_project_returns_201_with_its_location_and_body`,
  `Listing_returns_only_the_callers_organizations_projects`,
  `Another_organizations_project_is_indistinguishable_from_a_missing_one`._
- `kind` cannot be changed — no endpoint accepts it after creation. _Evidence:
  `No_method_on_a_project_changes_its_kind` (PUT and PATCH are `405`, kind
  unchanged) and `Only_project_creation_binds_a_kind`, which walks the route
  table._
- Domain errors surface as problem details with their codes. _Evidence: every
  refusal in the suite asserts a `code` — `request.invalid`,
  `project.not_found`, `authz.no_organization_membership`,
  `authz.member_required`, `auth.antiforgery_failed`._

**Decided while implementing**, each with a test: reading needs any membership
and creating needs Member (assumption **A-16**); `kind` is accepted only by exact
name; names over 200 characters are `project.name_too_long` (a domain rule, since
the column is `varchar(200)`); the list is newest first inside a `projects`
object. **Found and fixed:** a body that is not JSON was a `500` in Development on
every endpoint, auth included (`ThrowOnBadRequest`).

**Verified by mutation:** removing the tenant filter on reads fails four tests,
removing the antiforgery filter fails one, removing the Member check fails one.

---

#### M01-05 — Design contract endpoints and schema validation

**Size:** L · **Depends on:** M01-04 · **Risk:** 🟡 Medium

**State:** 🔬 **implemented 2026-11-19, not reviewed, not accepted.** Started
ahead of M01-04's acceptance — and so of M01-03's — on the repository owner's
delegation ("take the best option"; recorded in IMPLEMENTATION_STATUS §9). On
branch `gosei-sy/m01-05-contract-endpoints`, stacked on M01-04, unmerged.

The first place the shared schemas are enforced at the boundary.

**Acceptance:**

- Draft create / update / approve / discard, and version history, per
  [API_CONTRACTS.md §4](docs/02-architecture/API_CONTRACTS.md). _Evidence:
  `ContractEndpointsTests` §2–§3; the surface is now API_CONTRACTS §3c._
- A payload failing `design-contract.schema.json` returns `400` with the
  failing JSON pointers. _Evidence:
  `A_payload_that_breaks_the_schema_is_400_with_the_failing_pointers_and_stores_nothing`._
- A flow step referencing a screen key absent from `/screens` returns `422` —
  closes AC-2.7, the referential rule JSON Schema cannot express. _Evidence:
  `A_flow_step_naming_an_unlisted_screen_is_422_and_stores_nothing`._
- `contract.draft_already_open` returns `409`. _Evidence:
  `A_second_draft_while_one_is_open_is_409_and_the_first_is_untouched`._
- `contract.version_immutable` returns `409`. _Evidence:
  `Editing_an_approved_contract_is_409_version_immutable_and_changes_nothing`._
- Integration tests cover each transition and each rejection. _Evidence: 49
  cases in `ContractEndpointsTests`, on every route for tenancy, anonymity,
  membership and antiforgery._

**Note:** use the same JSON Schema files the frontend uses. Two copies of a
contract definition will diverge. _Done: the file is embedded, not copied. But
the same file still means a second implementation reading it, so the API's
validator is tested against a 157-case corpus recording **Ajv's** verdicts
(`SchemaConformanceTests`, guarded from the other side by `conformance.test.ts`).
The mechanism is **ADR-0010, proposed**._

**Decided while implementing**, each with a test and each open to review:

- **The validator is in-house** (ADR-0010). The usual library now carries a
  fee-bearing licence for commercial use.
- **Stricter than the schema where the store demands it:** duplicate members,
  U+0000, unpaired surrogates and inexact numbers are 400, not a 500 at save.
- **`jsonb` stores the value, not the bytes**, so the hash is of the stored
  text. The schema's description said "verbatim" and has been corrected.
- **Writes are serialized per project.** An approval racing an edit otherwise
  leaves an approved version that disagrees with its hash.
- **Roles are route policies with coded 403s**; a route-table test proves no
  write route lacks one. Authoring is Member, approval is Approver (A-16).
- **Bodies are capped at 1 MiB** — provisional; payload limits proper are M02.

**Found and fixed:** the host's domain-error handler sent `application/json`,
not `application/problem+json`. No endpoint had returned a domain refusal until
now, so nothing had read the header.

**Not done, deliberately:** duplicate screen or flow keys are accepted (no
stated rule); two people editing one draft are serialized, not warned (needs a
version token from the editor, M01-10); the design system schema has no
conformance corpus yet (M01-06).

---

#### M01-06 — Design system endpoints and projection endpoint

**Size:** M · **Depends on:** M01-05

**Acceptance:**

- Draft / approve / history endpoints mirroring M01-05.
- `POST /api/v1/projects/{id}/design-system/projections/{providerId}` returns
  the native payload **and** `warnings[]`, with **no provider call and no cost**.
- An integration test asserts a warning is returned for an unsupported font.
- `404` for an unknown provider; `404` when the provider lacks
  `IDesignSystemProjectionCapability`.

---

#### M01-07 — Generation job handler

**Size:** L · **Depends on:** M01-01, M01-05 · **Risk:** 🔴 High

The first real handler. The queue mechanism is verified; this is the payload and
the provider call.

**Acceptance:**

- `IJobHandler` for `screen.generate` registered in the Worker.
- `POST /api/v1/screens/{id}/generate` returns `202` with a job id, requires
  `Idempotency-Key`, and enqueues **in the same transaction** as the screen
  state change.
- A successful job creates a `ScreenVersion` with `ProviderGenerated` origin,
  the provider artifact id, and the contract version id **and payload hash**.
- The handler is idempotent: the same payload delivered twice produces one
  screen version. _(A lease can expire mid-flight — this is a requirement, not a
  nicety.)_
- `MISSING_CANVAS_CREDENTIALS` fails the job non-retryably.
- `POST /api/v1/screens/{id}/generate/plan` returns a `GenerationPlan` free of
  cost.
- **Declare `IScreenVariantCapability`** once its response mapping is verified
  (SPIKE-02 step 7), with tests. Until then it stays undeclared.
- An integration test covers the handler against a recorded provider response.

---

#### M01-08 — Screen endpoints and review transitions

**Size:** M · **Depends on:** M01-07

**Acceptance:**

- List, create, submit, approve, reject endpoints.
- `screen.contract_drift` returns `409` **and names which contract version is
  now in force**, so the UI can explain the cause rather than show a bare error
  (assumption A-12).
- Exactly one approved version per screen, verified through the API.

---

#### M01-09 — Job status endpoints

**Size:** S · **Depends on:** M01-07

**Acceptance:**

- `GET /api/v1/jobs/{id}` returns state, attempt, `maxAttempts`, `lastError`.
- `POST /api/v1/jobs/{id}/cancel`; `409 job.already_terminal` when terminal.
- The response distinguishes **retries exhausted** from **non-retryable**. These
  need different user-facing copy (S-12) — conflating them sends users to the
  wrong fix.

---

#### M01-10 — Studio UI for authoring

**Size:** L · **Depends on:** M01-04 … M01-09

Screens S-02 through S-12 from
[SCREEN_INVENTORY.md](docs/03-design/SCREEN_INVENTORY.md).

**Acceptance:**

- Sign-in, project list and create, project overview, contract editor, design
  system editor, projection preview, generate-with-plan, job status.
- **An approved contract or design system renders visibly read-only.** Offering
  an edit affordance that the API then refuses is worse than not offering it.
- The free plan step is a visible stage before any spend.
- Every state listed per screen in SCREEN_INVENTORY is implemented, including
  empty, loading and error.
- Component tests per screen; axe-core accessibility checks added to the
  Playwright run.

---

#### M01-11 — Unblock and run the E2E suite

**Size:** S · **Risk:** 🟢 Low

**Carried from M00 as unresolved.** M00 wrote four Playwright specs and could
not execute them. The blocker is the Playwright Chromium binary, not the Node
server — the independent review started the production server and confirmed the
specs are discovered. The exact failure recorded at M00 closure:

```
browserType.launch: Executable doesn't exist at
  .../ms-playwright/chromium_headless_shell-1248/chrome-headless-shell-linux64/chrome-headless-shell
```

**As at M00 closure** those four specs were not part of the 436 tests then
reported as passing. The suite is now **7 specs, 7 passing locally**, and is
still counted separately from the backend/frontend totals because it needs two
running processes.

**Acceptance:**

- `npm run test:e2e --workspace frontend` runs green locally and in CI.
  _Evidence, 2026-11-19: 9/9 locally; CI run
  [44509571762](https://github.com/gosei-sy/AI-Design-Studio/actions/runs/44509571762)
  on branch `gosei-sy/study-handoff-codebase-review` at `10dbef7`, via
  `workflow_dispatch`._
- The CI `e2e` job passes with real output, not a skip. _Evidence: the job log
  reads `9 passed (12.8s)` after migrations applied and `API ready after 10s`._
- TEST_STRATEGY's "⚠ unverified" note on E2E is removed. ✅ _(done)_

**State:** every criterion now has evidence; **not accepted** — that is the
reviewer's call, not this document's. The green run is on a branch, not `main`,
and it took two CI-configuration fixes the workflow had always needed but never
exercised, because CI had never run: `global.json` named a runtime version
(`10.0.0`) where `setup-dotnet` requires an SDK version, and the e2e job applied
migrations without restoring first (`NETSDK1004`).

---

#### M01-12 — Verify `docker-compose.yml`

**Size:** XS · **Risk:** 🟢 Low

**Carried from M00 as unresolved.** Closes accepted risk R-4. The file was
written in M00 and never executed, because Docker is not installed —
`docker compose config` exited 127 with `docker: command not found`. M00 used a
natively installed PostgreSQL 15.19 instead, so the compose file is the only part
of the local-development story that remains unverified.

**Acceptance:**

- `docker compose up -d postgres` starts a healthy container.
- Migrations apply against it.
- The full test suite passes with `AIDS_TEST_CONNECTION` pointing at it.
- R-4 removed from SECURITY_REQUIREMENTS §8.

---

### M01 milestone acceptance criteria

1. All tasks' acceptance criteria met, with evidence.
2. A team can author, approve and version a contract and a design system through
   the UI.
3. **A real screen is generated through Stitch** against an approved contract,
   and appears as a screen version with full provenance.
4. A screen produced against a superseded contract cannot be approved through
   the API.
5. Authentication and organization-scoped authorization are enforced, with a
   cross-tenant test proving isolation.
6. The E2E suite runs green in CI.
7. `docker-compose.yml` verified.
8. No credential value is logged or returned — re-verified.
9. `IMPLEMENTATION_STATUS.md` and `OPEN_ASSUMPTIONS.md` updated.
10. Full suite green; real counts reported.

### M01 risks

| Risk                                          | Impact                       | Mitigation                                                 |
| :-------------------------------------------- | :--------------------------- | :--------------------------------------------------------- |
| **A-08 resolves negatively**                  | 🔴 Architecture change       | M01-01 first; escalate rather than work around             |
| Provider response shapes differ from assumed  | 🟡 Rework in the adapter     | Field reads are isolated and null-tolerant                 |
| Generation cost higher than expected          | 🟡 Quota moves earlier       | `BillableOperationsEnabled` off by default; plan step free |
| ~~Role model (A-01) wrong~~ — resolved by D-3 | 🟢 Closed                    | Two roles selected 2026-10-21                              |
| Generation is asynchronous provider-side      | 🟡 Handler needs a poll loop | `Heartbeat` already supports long work                     |

---

## M02 — Review, import, redesign

**Goal:** both workflows are usable end to end, except handoff.

| Task   | Scope                                                                       |
| :----- | :-------------------------------------------------------------------------- |
| M02-01 | Screen review UI with contract obligations shown beside the artifact (S-13) |
| M02-02 | Projection warnings surfaced in the design system UI (S-09, closes AC-3.6)  |
| M02-03 | Asset upload endpoints and import pipeline (S-15, S-16)                     |
| M02-04 | Import UI, with `Visual` and `Structural` as **separate result states**     |
| M02-05 | Feature inventory UI and coverage mapping (S-17, S-18)                      |
| M02-06 | Handoff readiness endpoint and UI (S-19)                                    |
| M02-07 | Azure Blob Storage adapter behind `IAssetStorage`                           |
| M02-08 | Rate limiting and per-organization quota (closes S-18)                      |
| M02-09 | Job retention and archival                                                  |
| M02-10 | Version comparison view (S-14)                                              |

**Key acceptance:** a redesign project cannot reach handoff with any undecided
feature, demonstrated through the UI; and an uploaded screenshot's UI carries no
affordance implying editable structure.

---

## M03 — Handoff

| Task   | Scope                                                                   |
| :----- | :---------------------------------------------------------------------- |
| M03-01 | Handoff package generation (S-20)                                       |
| M03-02 | **Package states every projection warning and every unverified screen** |
| M03-03 | Variant exploration UI (S-21), gated on M01-07 declaring the capability |
| M03-04 | Project history and audit views                                         |

**Key acceptance:** the package states what was approved **and what is
uncertain**. A handoff that hides known gaps is worse than no handoff, because
it is trusted.

---

## Decisions needed from stakeholders

| #       | Decision                                                                                                               | Needed by                                                                                                                                                                                                                                                | Owner                                  |
| :------ | :--------------------------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :------------------------------------- |
| D-1     | **Provision a Stitch account and API key**                                                                             | **Before M01-01**                                                                                                                                                                                                                                        | Product Owner                          |
| D-2     | Authorize spend for live verification, with a cap                                                                      | Before M01-01                                                                                                                                                                                                                                            | Product Owner                          |
| ~~D-3~~ | ~~Confirm or collapse the four-role model (A-01)~~ — ✅ **RESOLVED 2026-10-21**: two roles, Member + Approver          | —                                                                                                                                                                                                                                                        | Product Owner                          |
| ~~D-4~~ | ~~Accept the seven M00 ADRs~~ — ✅ **RESOLVED 2026-10-21**                                                             | —                                                                                                                                                                                                                                                        | Solution Architect                     |
| D-5     | Deployment target and secret store                                                                                     | Before M02                                                                                                                                                                                                                                               | Solution Architect                     |
| D-6     | Handoff package format (A-13)                                                                                          | Before M03                                                                                                                                                                                                                                               | Product Owner + Developer rep          |
| **D-8** | **A password-reset delivery transport** (authorized mail service)                                                      | **Before any Production deployment** — ADR-0008 **A2** (accepted 2026-11-02) makes this a fail-closed pre-production gate, enforced by the host refusing to start. It no longer gates M01-02 acceptance; recovery stays undelivered until it is resolved | **Product Owner + Solution Architect** |
| ~~D-9~~ | ~~**Account proofing**~~ — ✅ **RESOLVED 2026-11-03**: trusted operator provisioning (ADR-0009)                        | —                                                                                                                                                                                                                                                        | Product Owner + Solution Architect     |
| ~~D-7~~ | ~~Authentication mechanism and identity provider~~ — ✅ **RESOLVED 2026-10-21**: Option A, local Identity + cookie/BFF | —                                                                                                                                                                                                                                                        | Product Owner + Solution Architect     |

> **D-7 RESOLVED — Option A: ASP.NET Core Identity backed by PostgreSQL, with
> HttpOnly cookie sessions over a same-origin BFF proxy. Its password lifecycle, recovery,
> lockout, MFA policy, antiforgery protection and Data Protection key
> persistence are explicit M01 scope.**
>
> **D-3 RESOLVED — collapse A-01 to two roles for the MVP: Member and Approver.
> A user may hold both. Only Approver may perform approval, rejection or waiver
> transitions.**

Both recorded 2026-10-21 by the Product Owner. D-7 selected Option A over the
independent recommendation of Option B; the trade-off that choice accepts is
enumerated in [ADR-0008](docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md).

D-3 decides **authorization**, which is **M01-03**. M01-02 therefore seeds no
role and enforces none.

**D-4 RESOLVED — The seven M00 ADRs are architecturally accepted. The
independent M00 re-acceptance and APPROVED decision include this architectural
sign-off.** Recorded 2026-10-21.

**D-7 is resolved** — see the quoted decision above and ADR-0008. It is listed once, in the decisions table; the earlier duplicate section that still called it open has been removed (W2-010).

D-1 and D-2 remain on the critical path. M01's central deliverable — live
generation — cannot start without them, and M01-01's live test plan cannot be
executed without them.

---

## Appendix — M01 prerequisites from the M00 review

The independent review noted that several M01 tasks needed foundation
corrections added as prerequisites. All of those corrections landed in M00
([response](docs/05-delivery/reviews/M00_CODEX_REVIEW_RESPONSE.md)), so the
tasks below are unblocked — but each now has a constraint that did not exist
when it was written. Recorded here so the constraint is not rediscovered mid-task.

| Task                               | Added constraint                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| :--------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **M01-02** Authentication          | Reviewer identity must come from the authenticated principal for `approvedBy`, `waivedBy` **and** `rejectedBy`. The domain now requires a reviewer on rejection too.                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| **M01-03** Organization scoping    | Tenant ownership is **transitive** through the project, so carrying `OrganizationId` only on `DesignProject` leaves contracts, design systems, screens and inventories unfilterable (W3-001). A guard that discovers its own population can only confirm the author's model — partition the whole EF model instead. **A denormalized key needs a constraint, not a comment**: "cannot drift" was true of mutation and false of creation, so the pair needs a composite foreign key (W3-RR-001). Every role change must persist its own actor (W3-002), and an upgrade must state what it did about rows predating the audit chain (W3-RR-002). |
| **M01-04** Project endpoints       | Reject an out-of-range `kind` with a clear `400` before it reaches the domain, so the client sees a validation error rather than `project.invalid_kind` as a `409`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| **M01-05** Contract endpoints      | A project has **at most one** contract (unique `project_id`). Creation must handle the conflict rather than assuming check-then-create.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| **M01-06** Design system endpoints | Same cardinality rule. The aggregate now has `DiscardDraft` and `TokensHash`, so the route surface can mirror M01-05 exactly, as AC-3.1 requires.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| **M01-07** Enqueue recovery        | On losing an enqueue race, the losing transaction must be **rolled back** and recovery must use a **clean unit of work** — a failed `SaveChanges` leaves the change tracker dirty. Required by the re-acceptance review.                                                                                                                                                                                                                                                                                                                                                                                                                       |
| **M01-07** Generation handler      | Handlers must be idempotent _and_ tolerate losing the lease mid-flight. The dispatcher renews the lease and **cancels the handler** when renewal reports loss, so a handler must honour its `CancellationToken` rather than running to completion. `BatchSize` defaults to 1 — raising it requires concurrent execution first.                                                                                                                                                                                                                                                                                                                 |
| **M01-08** Screen endpoints        | The reject endpoint persists reason, reviewer and timestamp. Whether resubmission retains prior review history is still open; a full review-event record is M02 work with the review UI.                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| **M01-09** Job endpoints           | Must distinguish _retries exhausted_ from _non-retryable_, and from the new _lease expired with no attempts left_, whose `LastError` explains that the worker terminated mid-execution. Three different user-facing messages.                                                                                                                                                                                                                                                                                                                                                                                                                  |
