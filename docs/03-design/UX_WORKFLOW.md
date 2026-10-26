# UX Workflow

Two workflows share the same spine — **contract → design system → screens →
review → handoff** — and differ in how they start and what gates them at the
end.

Stages marked **(M00 ◐)** have their domain rules implemented and verified but
no UI yet. Nothing in M00 ships product UI.

---

## 1. Shared spine

```
  ┌──────────┐   ┌───────────────┐   ┌─────────┐   ┌────────┐   ┌─────────┐
  │  Intake  │──►│   Contract    │──►│ Design  │──►│Screens │──►│ Review  │──► Handoff
  │          │   │ draft→approve │   │ System  │   │generate│   │ approve │
  └──────────┘   └───────────────┘   └─────────┘   └────────┘   └─────────┘
                        │                  │            │            │
                        │                  │            │            │
                   immutable          projection    provenance   drift guard
                   once approved      warnings      recorded     blocks stale
```

### Why the order is enforced

Generation before an approved contract has nothing to be judged against, and the
review stage would have no obligations to check. The sequence is not
bureaucratic: each stage produces the input the next one is measured by.

The product's main adoption risk is exactly this — users want to generate
immediately (assumption A-02). The mitigation is to make contract authoring fast
and partially inferable, **not** to make it optional.

---

## 2. New Product Design

### Stage 1 — Intake

**Input:** a product brief, in prose.
**Output:** a project with kind `NewProduct`.

| Rule                                               | Status |
| :------------------------------------------------- | :----- |
| An organization and a name are required            | M00 ◐  |
| `kind` is fixed at creation and has no change path | M00 ◐  |

### Stage 2 — Design Contract

**Output:** an approved contract version with a recorded payload hash.

```
   (none) ──StartDraft──► Draft ──ApproveDraft──► Approved ──────► Superseded
                            │                                         ▲
                            └──DiscardDraft──► (removed)              │
                                                                       │
                                     next approval supersedes ─────────┘
```

The user authors: information architecture (flows and their ordered steps),
per-screen obligations (purpose, required elements, required **states**),
accessibility target, content rules, and explicitly-recorded assumptions.

| Rule                                                           | Status |
| :------------------------------------------------------------- | :----- |
| One draft open at a time, so reviewers have a single candidate | M00 ◐  |
| Only a draft is editable                                       | M00 ◐  |
| Exactly one approved version, enforced in the database too     | M00 ◐  |
| Approval records approver, timestamp, SHA-256 of the payload   | M00 ◐  |
| Payload must validate against the shared JSON Schema           | M00 ◐  |

**UX note — required states.** The contract asks for `empty`, `loading`,
`error`, `offline` states per screen because generative tools routinely produce
only the happy path. Listing them makes the omission visible at review rather
than at implementation.

**UX note — assumptions.** The contract has a first-class `assumptions` array.
Unvalidated beliefs get recorded as assumptions rather than written in as
requirements, so they can be challenged later.

### Stage 3 — Design System

**Output:** an approved token set, plus a clear picture of what the provider
cannot represent.

Same three-state versioning as the contract, for the same reason.

**The projection step is a user-facing stage, not an implementation detail.**
Before generating anything, the user sees which of their tokens the configured
provider will actually apply:

| Their design system says     | Provider will apply | They see                                                                                                                            |
| :--------------------------- | :------------------ | :---------------------------------------------------------------------------------------------------------------------------------- |
| `bodyFont: "Acme Grotesk"`   | `INTER`             | ⚠ _Acme Grotesk is not one of Stitch's 67 supported families. Substituted INTER; the provider preview will not use the brand font._ |
| `color.modes: [light, dark]` | `LIGHT`             | ⚠ _Stitch's theme carries one colorMode. Only the first mode is applied._                                                           |
| `shape.radiusBase: "10px"`   | `ROUND_EIGHT`       | ⚠ _Stitch exposes roundness as a five-step enum. 10px snapped to ROUND_EIGHT (8px)._                                                |
| `color.danger`               | —                   | ⚠ _Stitch has no theme slot for the 'danger' colour role. Enforced studio-side only._                                               |
| `elevation`                  | —                   | ⚠ _Stitch's theme model has no elevation or shadow tokens._                                                                         |

Warnings are **normal output**, not errors. Seeing them is the point: a user who
knows the preview will use Inter is not surprised by it, and knows not to judge
typography from the preview.

Status: projection and warnings M00 ◐ (15 tests); endpoint M01; UI M02.

### Stage 4 — Screen generation

**Output:** screen versions with recorded provenance.

```
  Declare screen (from a contract obligation)
          │
          ├──► Plan  (free, no provider call) ──► user sees the operations
          │
          └──► Generate (billable, enqueued) ──► job ──► new screen version
```

| Rule                                                                   | Status                         |
| :--------------------------------------------------------------------- | :----------------------------- |
| Planning is free and works with no credentials                         | **M00 ✅**                     |
| Billable calls refused unless enabled for the environment              | **M00 ✅**                     |
| Generation runs as a durable job, never in-request                     | M00 ✅ (queue) / M01 (handler) |
| A duplicate submission returns the existing job                        | **M00 ✅**                     |
| Each version records contract id + payload hash + provider artifact id | **M00 ✅**                     |
| Credential errors are not retried                                      | **M00 ✅**                     |

**UX note — plan before spend.** The free dry-run exists because generation costs
money and the user should be able to see what a request will do first. It is
surfaced as a visible step, not hidden behind a confirm dialog.

### Stage 5 — Review

```
   Draft ──Submit──► InReview ──Approve──► Approved ──► Superseded
              ▲          │                     │
              └─Reject───┘                     │
             (reason required)                 │
                                    drift guard blocks approval
                                    of stale output
```

| Rule                                                                | Status     |
| :------------------------------------------------------------------ | :--------- |
| Must pass through `InReview`; no direct approval                    | **M00 ✅** |
| Rejection requires a reason                                         | **M00 ✅** |
| A rejected version can be resubmitted                               | **M00 ✅** |
| Approval blocked if the contract moved on (`screen.contract_drift`) | **M00 ✅** |
| A blocked approval leaves state unchanged                           | **M00 ✅** |
| Exactly one approved version per screen                             | **M00 ✅** |
| Reviewer sees the contract's obligations beside the artifact        | M02        |

**UX note — drift is friction by design.** When the contract changes, screens
produced against the old version can no longer be approved. This will feel like
the system undoing work (assumption A-12). The UI must explain _which_ contract
change caused it and offer regeneration, rather than presenting a bare error.

### Stage 6 — Handoff

**Output:** a specification stating what was approved **and what is uncertain**.

A handoff package must include every projection warning and every screen never
verified against a live provider. A handoff that hides known gaps is worse than
no handoff, because it is trusted.

Status: M03.

---

## 3. Existing Application Redesign

Differs at both ends: it starts with an inventory, and it is gated by coverage.

```
  ┌────────────────┐   ┌──────────────┐   ┌──────────┐
  │ Intake:        │──►│ Import       │──►│ Contract │──► Design System ──► Screens
  │ feature        │   │ screenshots  │   │          │
  │ inventory      │   │ or DOM       │   └──────────┘
  └────────────────┘   └──────────────┘
          │                                                         │
          │                                                         ▼
          │                                                   ┌──────────┐
          └──────── coverage mapping ─────────────────────────►│  Review  │
                                                               └────┬─────┘
                                                                    │
                                                 ┌──────────────────▼─────────────────┐
                                                 │ Handoff gate:                      │
                                                 │ every feature Covered or Waived    │
                                                 └────────────────────────────────────┘
```

### Stage R1 — Feature inventory (first, not last)

The inventory comes **before** import, deliberately. Cataloguing capabilities
while looking at the existing application is more reliable than reconstructing
them from screenshots afterwards.

| Rule                                                        | Status     |
| :---------------------------------------------------------- | :--------- |
| Stable key required; duplicates rejected case-insensitively | **M00 ✅** |
| A new feature starts `Unaddressed`, which blocks handoff    | **M00 ✅** |
| Import inventory from a list                                | M02        |

**UX note — the limit is stated to the user.** The product guarantees nothing
_catalogued_ is lost silently. It cannot know about a feature nobody recorded.
The inventory UI must say so plainly, because an unstated limit on a safety
mechanism produces false confidence (assumption A-04).

### Stage R2 — Import

Two paths with genuinely different fidelity, and the UI must not blur them:

| Path                    | Fidelity               | What you get                              | What you don't                      |
| :---------------------- | :--------------------- | :---------------------------------------- | :---------------------------------- |
| Upload screenshot       | `Visual`               | A visual reference                        | No element tree, no text, no tokens |
| Capture a running route | `StructuralWithStyles` | Semantic tree, text, token **candidates** | Not an approved design system       |

| Rule                                                           | Status     |
| :------------------------------------------------------------- | :--------- |
| A raster upload yields `Visual` and nothing more               | **M00 ✅** |
| The warning recommends DOM capture when structure is needed    | **M00 ✅** |
| `ImportedScreenshot` and `CapturedMarkup` are distinct origins | **M00 ✅** |
| Malformed input degrades and warns; never throws               | **M00 ✅** |
| >40 distinct colours flagged for consolidation                 | **M00 ✅** |

**UX note — never imply more than you have.** The UI must not show a screenshot
upload with affordances that suggest editable structure. This is the single most
damaging misrepresentation available to this product (ADR-0006): it would let a
redesign drop functionality that was visible in a screenshot but never
represented in the system.

**UX note — capture, don't generate, when importing.** Where a running
application is reachable, capture it. Google's own CLI guidance is explicit:
never generate during code import, because generation invents UI the application
does not have.

### Stage R3 — Coverage mapping

Each inventory item moves to exactly one terminal state:

```
                   ┌──► Covered   (≥1 real screen id)
  Unaddressed ─────┤
   (blocks)        └──► Waived    (reason + named owner)
```

| Rule                                             | Status     |
| :----------------------------------------------- | :--------- |
| `Covered` requires at least one real screen id   | **M00 ✅** |
| `Waived` requires a reason **and** a named owner | **M00 ✅** |
| A rejected waiver leaves coverage unchanged      | **M00 ✅** |
| Transitions clear the other state's data         | **M00 ✅** |
| Coverage mapping UI                              | M02        |

### Stage R4 — Handoff gate

| Rule                                       | Status     |
| :----------------------------------------- | :--------- |
| Blocked while any feature is `Unaddressed` | **M00 ✅** |
| The error names the blocking features      | **M00 ✅** |
| An empty inventory does not block          | **M00 ✅** |
| Readiness endpoint and UI                  | M02        |

**UX note — block, don't warn.** Warnings get ignored under deadline pressure,
which is precisely when functional loss happens. The gate is mechanical so it
cannot be skipped without someone recording a waiver and their name
(ADR-0007).

---

## 4. Cross-workflow UX principles

1. **Show provider limitations before they matter.** Projection warnings appear
   at the design system stage, not after a disappointing preview.
2. **Separate free from billable.** Planning and readiness are free and clearly
   marked as such; generation is clearly marked as spending.
3. **Never overstate fidelity.** Every import states what it actually recovered.
4. **Make omission visibly different from decision.** `Unaddressed` is not a
   neutral state; it is a blocker.
5. **Errors name the cause and the fix.** `screen.contract_drift` tells the user
   the contract moved and offers regeneration. `MISSING_CANVAS_CREDENTIALS`
   names the credential.
6. **Degrade readably.** When the API is unreachable the UI says so in words a
   developer can act on, rather than failing blank. Implemented and tested in
   M00.

---

## 5. Current UX state (M00)

One page exists: a read-only environment status panel reporting API health,
provider capabilities and missing credential **names**. It exists because "is
this environment working, and what can the provider actually do here?" is the
question every later milestone depends on.

No product workflow UI exists. Everything in §2 and §3 marked M01–M03 is
unbuilt. The domain rules behind them are implemented and verified, which is
what makes the UI work in M01–M02 presentation rather than invention.
