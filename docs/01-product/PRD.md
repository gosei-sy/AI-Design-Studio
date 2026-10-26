# Product Requirements — AI Design Studio

**Milestone:** M00. **Status:** awaiting product and architecture acceptance.

> **Evidence boundary.** Nothing here is validated against real users. No user
> interviews, surveys or usage data informed this document. It records the
> product direction given by the Product Owner, UX Lead and Solution Architect,
> plus constraints measured directly from `@google/stitch` 0.11.1.
> Every statement that would ordinarily rest on user research is listed in
> [OPEN_ASSUMPTIONS.md](OPEN_ASSUMPTIONS.md) instead of being asserted here.

---

## 1. Problem

Teams using AI design tools (Google Stitch, Claude Design and similar) get fast
output and inconsistent results. Three failures recur:

1. **No source of truth.** The design lives in whichever provider produced it.
   Prompts are not reproducible, output is non-deterministic, and "what was
   agreed" exists only in someone's memory.
2. **Silent drift.** A design system is agreed, then individual screens diverge
   from it because nothing checks conformance and providers cannot express the
   full token set anyway.
3. **Functional loss in redesigns.** Redesigning an existing application from
   screenshots loses capabilities that nobody noticed were missing — because an
   omission and a decision look identical at the end.

## 2. What this product is

An independent web application that makes the **Design Contract** and the
**approved Design System** the source of truth, and treats AI provider output as
evidence that the contract was satisfied.

It supports two workflows:

- **New Product Design** — brief → UX architecture → design system → generated
  screens → review → developer handoff.
- **Existing Application Redesign** — screenshots and a feature inventory →
  consistent redesign with no unintentional functional loss.

### What it is not

- Not a design tool. It does not render or edit visual designs; providers do.
- Not an AI provider. It orchestrates providers behind a capability abstraction.
- Not a code generator. Handoff produces specifications, not an application.

## 3. Users

| Role               | Needs                                                             | Authority                           |
| :----------------- | :---------------------------------------------------------------- | :---------------------------------- |
| Product Owner      | A contract that captures intent; visibility into what was dropped | Approves contracts; waives features |
| UX Lead / Designer | A design system that survives provider limitations                | Authors drafts; reviews screens     |
| Developer          | An unambiguous handoff with stated gaps                           | Consumes handoff                    |
| Reviewer           | A checkable comparison of output against obligations              | Approves or rejects screen versions |

Role definitions are a design input, not a research finding — see
[OPEN_ASSUMPTIONS.md](OPEN_ASSUMPTIONS.md) A-01.

## 4. Principles

1. **The Design Contract is the source of truth** — not any provider's output
   (ADR-0002).
2. **Approval means immutability.** Once approved, a contract or design system
   version cannot change. Changing it means a new version that supersedes the
   old (ADR-0002).
3. **Provider limitations are reported, never absorbed.** Where a provider
   cannot express part of the design system, the user sees exactly which token
   was dropped or approximated, and why (ADR-0004).
4. **Fidelity is never overstated.** An uploaded screenshot is a visual
   reference. It is not an editable design and the system never implies it is
   (ADR-0006).
5. **Silence is not a decision.** A redesign cannot complete while any
   catalogued feature is undecided. Dropping one requires a reason and a named
   owner (ADR-0007).
6. **Capabilities are discovered, not assumed.** The system asks a provider what
   it can do and gates the UI on the answer (ADR-0003).
7. **Cost is opt-in.** Billable provider calls are disabled by default per
   environment; planning and readiness checks are free.

## 5. Functional requirements

Each requirement has testable acceptance criteria. **[M00]** marks those already
met and verified; the rest state their milestone.

### FR-1 — Projects

A project is created with a workflow kind that cannot later change.

- **AC-1.1** A project requires an organization and a non-empty name.
  _Verified: `DesignProjectTests` via domain guards `project.organization_required`,
  `project.name_required`._ **[M00]**
- **AC-1.2** `kind` is `NewProduct` or `Redesign`, set at creation, with no API
  to change it. _Verified: `A_project_round_trips_with_its_workflow_kind_stored_as_text`._
  **[M00]**
- **AC-1.3** Projects are organization-scoped in every query. **[M01]**

### FR-2 — Design Contract

- **AC-2.1** Version numbers are dense and monotonic from 1. _Verified:
  `StartDraft_assigns_version_numbers_from_one_and_increments`._ **[M00]**
- **AC-2.2** At most one draft is open at a time; a second attempt fails with
  `contract.draft_already_open`. _Verified._ **[M00]**
- **AC-2.3** At most one version is `Approved` at any time — in the aggregate and
  in the database. _Verified: `Approving_a_new_version_supersedes_the_previous_approved_version`
  and `The_database_rejects_a_second_approved_contract_version`._ **[M00]**
- **AC-2.4** Editing an `Approved` or `Superseded` version fails with
  `contract.version_immutable`. _Verified: two tests._ **[M00]**
- **AC-2.5** Approval records approver identity, timestamp and a SHA-256 of the
  payload. _Verified: `ApproveDraft_records_approver_timestamp_and_payload_hash`._
  **[M00]**
- **AC-2.6** A contract payload is rejected unless it validates against
  `design-contract.schema.json`. _Schema exists, covered by the 164 shared
  contract tests; API wiring is M01._ **[M00 partial]**
- **AC-2.7** A flow step referencing a screen key absent from `/screens` is
  rejected with 422. **[M01]**

### FR-3 — Design System

- **AC-3.1** Versioning and approval behave identically to FR-2, including draft
  discard and an approval hash. _Verified: `DesignSystemTests` (20 tests) plus
  three persistence tests. M00 originally claimed this parity with no such suite;
  the independent review caught it as CX-007 and the implementation was brought
  up to the criterion rather than the criterion weakened._ **[M00]**
- **AC-3.2** The token model admits tokens no provider can express
  (elevation, motion, breakpoints, component variants). _Verified:
  `accepts sections that no provider theme can express`._ **[M00]**
- **AC-3.3** Projecting onto a provider theme returns one warning per token
  dropped, approximated or snapped. _Verified: 11 projection tests._ **[M00]**
- **AC-3.4** A font outside the provider's 67 families falls back and warns,
  naming the requested family. _Verified._ **[M00]**
- **AC-3.5** Declaring both light and dark modes warns that only one projects.
  _Verified._ **[M00]**
- **AC-3.6** Reviewers see projection warnings before approving screens. **[M02]**

### FR-4 — Screens and review

- **AC-4.1** Every screen version records the contract version id **and**
  payload hash it was produced against. _Verified._ **[M00]**
- **AC-4.2** Approving a version whose contract id or hash differs from the
  contract in force fails with `screen.contract_drift`, and the version's state
  does not change. _Verified: two tests, including the same-id-different-hash
  case._ **[M00]**
- **AC-4.3** A version must pass through `InReview` before approval. _Verified._
  **[M00]**
- **AC-4.4** Rejection requires a reason, and the reason, reviewer and timestamp
  are **retained**. _Verified: `A_rejection_retains_its_reason_reviewer_and_timestamp`
  plus a persistence round trip._ **[M00]**
- **AC-4.5** Approving a version supersedes the previously approved one; exactly
  one stays approved. _Verified in the aggregate and by
  `ux_screen_single_approved`._ **[M00]**
- **AC-4.6** A screen key is unique within its project. _Verified by index._
  **[M00]**
- **AC-4.7** Review shows the screen's obligations from the contract beside the
  artifact. **[M02]**

### FR-5 — Generation

- **AC-5.1** Generation runs as a durable background job, never in-request.
  _Mechanism verified; handlers are M01._ **[M00 partial]**
- **AC-5.2** A duplicate submission with the same idempotency key does not
  create a second billable call. _Verified in the aggregate, the queue and the
  database index._ **[M00]**
- **AC-5.3** A free plan (dry-run) is available before committing spend.
  _Verified: `Planning_is_allowed_when_billable_operations_are_disabled`._
  **[M00]**
- **AC-5.4** Billable calls are refused unless explicitly enabled for the
  environment. _Verified: `Billable_generation_is_refused_when_disabled`._
  **[M00]**
- **AC-5.5** A credential error is not retried. _Verified:
  `A_missing_credential_error_is_classified_as_non_retryable`._ **[M00]**
- **AC-5.6** A generated screen appears as a new screen version with provider
  provenance. **[M01]**

### FR-6 — Redesign without functional loss

- **AC-6.1** A catalogued feature starts `Unaddressed` and blocks handoff.
  _Verified._ **[M00]**
- **AC-6.2** Marking a feature covered requires at least one real screen id.
  _Verified, including the `Guid.Empty` case._ **[M00]**
- **AC-6.3** Waiving a feature requires both a reason and a named owner, and a
  rejected waiver leaves coverage unchanged. _Verified._ **[M00]**
- **AC-6.4** The handoff gate names the blocking features. _Verified._ **[M00]**
- **AC-6.5** Coverage and waiver states are mutually exclusive and transitions
  clear the other's data. _Verified: two tests._ **[M00]**
- **AC-6.6** Feature inventory can be imported from a list. **[M02]**

### FR-7 — Import

- **AC-7.1** A raster upload yields `Visual` fidelity with no tree and no
  tokens, and a warning stating the limit. _Verified:
  `A_screenshot_yields_visual_fidelity_only_with_no_structure_or_tokens`._
  **[M00]**
- **AC-7.2** A DOM snapshot yields a semantic element tree and observed style
  values as **candidates**. _Verified: 8 parser tests._ **[M00]**
- **AC-7.3** Malformed input degrades fidelity and warns; it never throws.
  _Verified: two tests._ **[M00]**
- **AC-7.4** An incoherent palette (>40 distinct colours) is flagged for
  consolidation. _Verified._ **[M00]**
- **AC-7.5** Import responses always state fidelity explicitly. **[M01]**

### FR-8 — Handoff

- **AC-8.1** Handoff is blocked for a redesign project with undecided features.
  _Gate verified; endpoint is M02._ **[M00 partial]**
- **AC-8.2** A handoff package states the approved contract version, design
  system version, approved screens, and **every projection warning**. **[M03]**
- **AC-8.3** A handoff package states which screens were never verified against
  a live provider. **[M03]**

## 6. Non-functional requirements

| NFR   | Requirement                                                            | Status                                                           |
| :---- | :--------------------------------------------------------------------- | :--------------------------------------------------------------- |
| NFR-1 | API p95 < 300 ms for non-generation endpoints                          | Not measured                                                     |
| NFR-2 | Generation never blocks a request thread                               | Met by design (ADR-0005)                                         |
| NFR-3 | A worker crash loses no enqueued job (crash-looping jobs reach `Dead`) | **Verified**                                                     |
| NFR-4 | No credential value is ever logged, returned or committed              | **Verified** (redaction, names-only readiness, health leak test) |
| NFR-5 | Migrations apply cleanly to an empty database                          | **Verified** (every integration run provisions a fresh database) |
| NFR-6 | Every business invariant has an automated test                         | **Verified** for shipped invariants (436 tests)                  |
| NFR-7 | WCAG 2.1 AA for the studio UI                                          | Not started — no product UI yet                                  |
| NFR-8 | No unapproved production deployment or external spend                  | **Met** — billable calls default off; nothing deployed           |

## 7. Explicitly out of scope

Permanently, not just for now:

- Rendering or editing visual designs in-app.
- Generating application code.
- Real-time multi-user collaborative editing.
- Replacing an AI provider's canvas.
- OCR or layout inference on screenshots (ADR-0006).

## 8. Known product limitations

Stated here rather than discovered later:

1. **Inventory quality bounds the redesign guarantee.** The system guarantees
   nothing _catalogued_ is lost silently. It cannot know about a feature nobody
   recorded.
2. **Provider previews will differ from the approved design system.** This is
   inherent (ADR-0004), surfaced rather than solved.
3. **Dark mode needs a second provider design system.** Stitch carries one
   `colorMode`.
4. **Generation is not reproducible.** The same prompt and contract can yield
   different screens. Versioning tracks what was produced, not how to reproduce
   it.
5. **No live provider verification yet.** Generation, variants, import and
   retrieval are unverified against a real Stitch account —
   [../06-spikes/README.md](../06-spikes/README.md).

## 9. References

- [USER_STORIES.md](USER_STORIES.md), [MVP_SCOPE.md](MVP_SCOPE.md),
  [OPEN_ASSUMPTIONS.md](OPEN_ASSUMPTIONS.md)
- [../03-design/UX_WORKFLOW.md](../03-design/UX_WORKFLOW.md),
  [../03-design/SCREEN_INVENTORY.md](../03-design/SCREEN_INVENTORY.md)
- [../02-architecture/SYSTEM_ARCHITECTURE.md](../02-architecture/SYSTEM_ARCHITECTURE.md),
  [../02-architecture/decisions/](../02-architecture/decisions/)
