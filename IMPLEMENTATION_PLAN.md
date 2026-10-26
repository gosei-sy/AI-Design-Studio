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

**Wave 1 is the authorized scope.** Work outside it still needs authorization.
No task is complete until its acceptance criteria have been executed with
evidence. Current wave state:

| Task         | State                       | Gate                                                           |
| :----------- | :-------------------------- | :------------------------------------------------------------- |
| **M01-01**   | 🚧 Offline prep done        | Live execution blocked on **D-1/D-2**; A-08 and A-09 stay open |
| **M01-02**   | ⏸ **Stopped at a decision** | **D-7** — authentication mechanism and identity provider, Open |
| **M01-03**   | ⛔ Not started              | **D-3** unresolved, and M01-02 not accepted                    |
| M01-04 … 06  | ⛔ Not started              | Each waits on its predecessor being accepted                   |
| M01-07/08/09 | ⛔ Not started              | Gated on M01-01 resolving **A-08** positively                  |
| M01-10       | ⛔ Not started              | Needs M01-04 … M01-09 accepted                                 |
| **M01-11**   | ✅ **Browser unblocked**    | 4/4 specs execute and pass; acceptance needs the CI run        |
| **M01-12**   | ⛔ **BLOCKED**              | Docker is not installed in this environment                    |

Three items carried over from M00 — see
[Carried into M01](#carried-into-m01-from-m00).

---

## M00 — Foundation ✅ Complete

**Independently re-accepted on 2026-10-21, decision APPROVED.** Detailed status
in
[docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md).

Delivered: 9 .NET projects, 7 domain modules with their invariants, a
capability-based provider abstraction with a Stitch adapter, a durable job
queue, artifact normalization, local asset storage, a Next.js skeleton, shared
contract schemas, CI, and 436 passing tests.

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

| Item                                                                       | Status at M00 closure                                                              | Owning task |
| :------------------------------------------------------------------------- | :--------------------------------------------------------------------------------- | :---------- |
| **D-1 / D-2** — Stitch account, credential, and capped spend authorization | ⛔ **Unresolved.** Product Owner decision; no provider call was ever made          | **M01-01**  |
| **M01-11** — Playwright Chromium not installed; E2E specs unexecuted       | ⛔ **Unresolved.** 4 specs written, `browserType.launch: Executable doesn't exist` | **M01-11**  |
| **M01-12** — Docker not installed; `docker-compose.yml` unexecuted         | ⛔ **Unresolved.** `docker: command not found` (exit 127)                          | **M01-12**  |

The E2E specs and `docker-compose.yml` remain **written but unverified**, and are
labelled as such wherever they are referenced. Neither was counted toward the 436.

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
their own listed dependencies, which A-08 does not change. M01-03 still waits on
M01-02, M01-05 and M01-06 still wait on M01-04, and M01-03 still needs the D-3
role-model decision.

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

**Size:** M · **Depends on:** A-01 decision · **Risk:** 🟡 Medium

**Acceptance:**

- All non-health endpoints require authentication; unauthenticated requests get
  `401` with problem details.
- A user identity is available to the domain for `ApprovedBy` / `WaivedBy`.
- Integration tests cover authenticated and unauthenticated paths.
- Security headers (CSP, HSTS, X-Content-Type-Options) set — closes S-25.

**Open decision for the Product Owner:** the four-role model (A-01). Needed
before M01-03.

---

#### M01-03 — Organization-scoped authorization

**Size:** M · **Depends on:** M01-02 · **Risk:** 🟡 Medium

**Acceptance:**

- Every query is organization-scoped; a cross-organization read returns `404`,
  not `403` (no existence disclosure).
- The four roles from A-01 are enforced on each endpoint.
- An integration test proves a user in organization A cannot read organization
  B's project, contract, screen or asset.
- An architecture or analyzer test catches a query missing its organization
  filter. _(Design this deliberately — it is the rule most easily forgotten
  later.)_

---

#### M01-04 — Project endpoints

**Size:** S · **Depends on:** M01-03

**Acceptance:**

- `POST /api/v1/projects` returns `201`; `GET` list and by-id are
  organization-scoped.
- `kind` cannot be changed — no endpoint accepts it after creation.
- Domain errors surface as problem details with their codes.

---

#### M01-05 — Design contract endpoints and schema validation

**Size:** L · **Depends on:** M01-04 · **Risk:** 🟡 Medium

The first place the shared schemas are enforced at the boundary.

**Acceptance:**

- Draft create / update / approve / discard, and version history, per
  [API_CONTRACTS.md §4](docs/02-architecture/API_CONTRACTS.md).
- A payload failing `design-contract.schema.json` returns `400` with the
  failing JSON pointers.
- A flow step referencing a screen key absent from `/screens` returns `422` —
  closes AC-2.7, the referential rule JSON Schema cannot express.
- `contract.draft_already_open` returns `409`.
- `contract.version_immutable` returns `409`.
- Integration tests cover each transition and each rejection.

**Note:** use the same JSON Schema files the frontend uses. Two copies of a
contract definition will diverge.

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

These four specs are **not** part of the 436 tests reported as passing.

**Acceptance:**

- `npm run test:e2e --workspace frontend` runs green locally and in CI.
- The CI `e2e` job passes with real output, not a skip.
- TEST_STRATEGY's "⚠ unverified" note on E2E is removed.

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

| Risk                                         | Impact                       | Mitigation                                                 |
| :------------------------------------------- | :--------------------------- | :--------------------------------------------------------- |
| **A-08 resolves negatively**                 | 🔴 Architecture change       | M01-01 first; escalate rather than work around             |
| Provider response shapes differ from assumed | 🟡 Rework in the adapter     | Field reads are isolated and null-tolerant                 |
| Generation cost higher than expected         | 🟡 Quota moves earlier       | `BillableOperationsEnabled` off by default; plan step free |
| Role model (A-01) wrong                      | 🟡 Rework in M01-02/03       | Decide before M01-03 starts                                |
| Generation is asynchronous provider-side     | 🟡 Handler needs a poll loop | `Heartbeat` already supports long work                     |

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

| #       | Decision                                                   | Needed by         | Owner                                  |
| :------ | :--------------------------------------------------------- | :---------------- | :------------------------------------- |
| D-1     | **Provision a Stitch account and API key**                 | **Before M01-01** | Product Owner                          |
| D-2     | Authorize spend for live verification, with a cap          | Before M01-01     | Product Owner                          |
| D-3     | Confirm or collapse the four-role model (A-01)             | Before M01-03     | Product Owner                          |
| ~~D-4~~ | ~~Accept the seven M00 ADRs~~ — ✅ **RESOLVED 2026-10-21** | —                 | Solution Architect                     |
| D-5     | Deployment target and secret store                         | Before M02        | Solution Architect                     |
| D-6     | Handoff package format (A-13)                              | Before M03        | Product Owner + Developer rep          |
| **D-7** | **Authentication mechanism and identity provider**         | **Before M01-02** | **Product Owner + Solution Architect** |

**D-4 RESOLVED — The seven M00 ADRs are architecturally accepted. The
independent M00 re-acceptance and APPROVED decision include this architectural
sign-off.** Recorded 2026-10-21.

**D-7 — authentication mechanism and identity provider. Status: Open.** Raised
by the M01 Wave 1 audit, which found that no identity provider or authentication
mechanism is recorded anywhere in the repository. It **blocks M01-02**, and is
owned jointly by the Product Owner and the Solution Architect because it is both
a product and an architecture choice.

**D-7 is distinct from D-3.** D-3 selects the authorization **role model**; D-7
selects how a request proves **who** it is. Either answer is compatible with any
version of the other. Options, corrections and the independent recommendation are
in [the Wave 1 report](docs/05-delivery/M01_WAVE1_REPORT.md) § 5.

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

| Task                               | Added constraint                                                                                                                                                                                                                                                                                                               |
| :--------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **M01-02** Authentication          | Reviewer identity must come from the authenticated principal for `approvedBy`, `waivedBy` **and** `rejectedBy`. The domain now requires a reviewer on rejection too.                                                                                                                                                           |
| **M01-03** Authorization           | Job queries at the API boundary need their own organization-scoped query. `IJobQueue.GetAsync` is deliberately unfiltered for worker-internal use; reusing it would be a cross-tenant read.                                                                                                                                    |
| **M01-04** Project endpoints       | Reject an out-of-range `kind` with a clear `400` before it reaches the domain, so the client sees a validation error rather than `project.invalid_kind` as a `409`.                                                                                                                                                            |
| **M01-05** Contract endpoints      | A project has **at most one** contract (unique `project_id`). Creation must handle the conflict rather than assuming check-then-create.                                                                                                                                                                                        |
| **M01-06** Design system endpoints | Same cardinality rule. The aggregate now has `DiscardDraft` and `TokensHash`, so the route surface can mirror M01-05 exactly, as AC-3.1 requires.                                                                                                                                                                              |
| **M01-07** Enqueue recovery        | On losing an enqueue race, the losing transaction must be **rolled back** and recovery must use a **clean unit of work** — a failed `SaveChanges` leaves the change tracker dirty. Required by the re-acceptance review.                                                                                                       |
| **M01-07** Generation handler      | Handlers must be idempotent _and_ tolerate losing the lease mid-flight. The dispatcher renews the lease and **cancels the handler** when renewal reports loss, so a handler must honour its `CancellationToken` rather than running to completion. `BatchSize` defaults to 1 — raising it requires concurrent execution first. |
| **M01-08** Screen endpoints        | The reject endpoint persists reason, reviewer and timestamp. Whether resubmission retains prior review history is still open; a full review-event record is M02 work with the review UI.                                                                                                                                       |
| **M01-09** Job endpoints           | Must distinguish _retries exhausted_ from _non-retryable_, and from the new _lease expired with no attempts left_, whose `LastError` explains that the worker terminated mid-execution. Three different user-facing messages.                                                                                                  |
