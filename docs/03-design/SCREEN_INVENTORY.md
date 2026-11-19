# Screen Inventory — AI Design Studio itself

The screens of this application (not of a user's project).

| Status              | Meaning                 |
| :------------------ | :---------------------- |
| **Built**           | Exists and is tested    |
| **M01 / M02 / M03** | Planned, with milestone |

Each planned screen lists the states it must cover, because the contract schema
requires them of a user's screens and this product should hold itself to the
same standard. Generative tools and hand-written UIs both default to the happy
path.

---

## 1. Built (M00)

### S-01 — Environment status

**Route:** `/` · **Status:** Built · **Devices:** desktop, tablet

Read-only diagnostics: API health with per-check detail, registered provider
adapters with their declared capabilities, and missing credential **names**.

| State                 | Behaviour                                                                             | Tested                                    |
| :-------------------- | :------------------------------------------------------------------------------------ | :---------------------------------------- |
| default               | Overall status, per-check rows, provider list with capabilities                       | ✅                                        |
| loading               | `Checking environment…` inside a Suspense boundary while the shell is already painted | ✅ (build output shows partial prerender) |
| error                 | `role="alert"` with an actionable message naming the API URL                          | ✅                                        |
| provider-unconfigured | Provider listed, `isUsable: false`, missing credentials named                         | ✅                                        |
| signed-out            | `Sign in to view provider status.`; health still shown                                | ✅ E2E                                    |
| signed-in             | Provider status shown — the server render forwards the visitor's session (2026-11-19) | ✅ E2E                                    |
| no-adapters           | `No provider adapters reported.`                                                      | ✅                                        |

Why it exists: every later milestone depends on knowing whether the environment
works and what the provider can do here, and reading that from a page is faster
than reading logs.

Security constraint, asserted in an E2E test: no credential value ever appears
in the rendered output.

---

## 2. M01 — Authoring and generation

### S-02 — Sign in

**Route:** `/sign-in` · **M01**

States: default · submitting · invalid-credentials · locked-out · provider-error

No longer blocked. A-01 was closed by D-3 (two roles, Member and Approver), the
mechanism is ADR-0008 (local Identity, same-origin cookie/BFF session), and the
API it calls exists since M01-02 (`/api/v1/auth/login`, lockout included). The
screen itself is M01-10 and is not built.

### S-03 — Project list

**Route:** `/projects` · **M01**

States: default · **empty (first-run)** · loading · error · unauthorized

The empty state is the most important one here — it is where a new user chooses
between the two workflows, and the two must be visibly distinct (UX_WORKFLOW §3).

### S-04 — Create project

**Route:** `/projects/new` · **M01**

States: default · validating · submitting · error

Must make the `NewProduct` / `Redesign` choice and its permanence clear:
`kind` cannot be changed afterwards.

### S-05 — Project overview

**Route:** `/projects/{id}` · **M01**

Stage progress: contract → design system → screens → review → handoff.

States: default · loading · error · **blocked** (shows what is gating progress —
no approved contract, or undecided inventory items)

### S-06 — Contract editor

**Route:** `/projects/{id}/contract` · **M01**

States: no-contract · **draft (editable)** · **approved (read-only)** ·
validation-errors · saving · conflict (`contract.draft_already_open`)

Critical UX requirement: the approved view must be visibly read-only. The
immutability rule (`contract.version_immutable`) is a guarantee, and a UI that
offers an edit affordance and then refuses is worse than one that does not offer
it.

### S-07 — Contract version history

**Route:** `/projects/{id}/contract/versions` · **M01**

States: default · single-version · loading · error

Shows status, approver, timestamp and payload hash per version. Superseded
payloads stay readable.

### S-08 — Design system editor

**Route:** `/projects/{id}/design-system` · **M01**

States: no-system · draft · approved (read-only) · validation-errors · saving

Same immutability treatment as S-06.

### S-09 — Provider projection preview

**Route:** `/projects/{id}/design-system/projection` · **M01**

States: default (**warnings present — the normal case**) ·
no-warnings (rare) · provider-unconfigured · loading · error

The screen that carries ADR-0004 to the user. Shows, per token: what the design
system specifies, what the provider will apply, and why they differ. A non-empty
warning list is normal output and must not be styled as failure.

### S-10 — Screen list

**Route:** `/projects/{id}/screens` · **M01**

States: default · empty · loading · error · **contract-required** (no approved
contract yet, so generation would have nothing to be judged against)

### S-11 — Generate screen

**Route:** `/projects/{id}/screens/{key}/generate` · **M01**

States: default · **plan-preview (free)** · **confirming-spend** ·
enqueued · generating · succeeded · failed-retryable · failed-terminal ·
**billable-disabled**

Two requirements that follow directly from M00 findings:

- The free plan step is a visible stage, not a hidden confirm. Generation costs
  money; the user should see the operations first.
- `BILLABLE_OPERATIONS_DISABLED` must read as a deliberate environment setting,
  not a fault.

### S-12 — Job status

**Route:** `/jobs/{id}` · **M01**

States: pending · running · succeeded · **dead (retries exhausted)** ·
**dead (non-retryable)** · cancelled

The two `Dead` states need different copy: "we tried three times and it kept
failing" versus "this cannot succeed — fix the credential". Conflating them
sends users to the wrong fix.

---

## 3. M02 — Review, import, redesign

### S-13 — Screen review

**Route:** `/projects/{id}/screens/{key}/review` · **M02**

States: draft · **in-review (obligations shown beside the artifact)** ·
approved · rejected · **contract-drift (approval blocked)** · loading · error

The `contract-drift` state must name which contract change caused it and offer
regeneration. A bare `screen.contract_drift` error on work the user considers
finished is how a correct mechanism gets worked around (assumption A-12).

### S-14 — Version comparison

**Route:** `/projects/{id}/screens/{key}/versions` · **M02**

States: default · single-version · loading · error

Shows origin, provider provenance, contract version and review state per
version. Note: because generation is non-deterministic, this is a comparison,
not a diff.

### S-15 — Import artifact

**Route:** `/projects/{id}/imports/new` · **M02**

States: choose-method · **uploading-screenshot** · **capturing-route** ·
parsing · **result (Visual fidelity)** · **result (StructuralWithStyles)** ·
degraded-with-warnings · error

The hard UX constraint from ADR-0006: the `Visual` result must not carry any
affordance implying editable structure, and must recommend DOM capture where
structure is needed. Two separate result states, not one with a badge.

### S-16 — Import result detail

**Route:** `/projects/{id}/imports/{id}` · **M02**

States: visual-only · structural · warnings-present · error

Observed styles are labelled **token candidates**, with an explicit promotion
step to the design system. One page's computed styles are evidence, not a design
system.

### S-17 — Feature inventory

**Route:** `/projects/{id}/inventory` · **M02**

States: empty · **has-blocking-items** · all-decided · loading · error

Must state the guarantee's limit plainly: nothing _catalogued_ is lost silently;
a feature nobody recorded is invisible (assumption A-04). An unstated limit on a
safety mechanism produces false confidence.

### S-18 — Coverage mapping

**Route:** `/projects/{id}/inventory/{itemId}` · **M02**

States: unaddressed · covered · waived · **waiver-form (reason + owner
required)** · validation-error

The waiver form cannot submit without both fields. Rejecting a partial waiver
must leave coverage unchanged — no half-states.

### S-19 — Handoff readiness

**Route:** `/projects/{id}/handoff` · **M02**

States: **blocked (features undecided)** · blocked (screens unapproved) ·
ready · loading · error

Blocked states name the specific blockers. "Not ready" without a list is
unactionable.

---

## 4. M03 — Handoff

### S-20 — Handoff package

**Route:** `/projects/{id}/handoff/package` · **M03**

States: generating · ready · **ready-with-caveats** · error

The `ready-with-caveats` state is mandatory, not optional polish. The package
must state every projection warning and every screen never verified against a
live provider. A handoff that hides known gaps is worse than no handoff, because
it is trusted.

### S-21 — Variant exploration

**Route:** `/projects/{id}/screens/{key}/variants` · **M03**

States: unavailable (**capability not declared**) · default · generating ·
results · error

Begins in the `unavailable` state: `IScreenVariantCapability` is deliberately
not declared until the response mapping is verified against a live account
(M01-07). The screen must read the capability list rather than assume support.

---

## 5. Cross-screen requirements

| Requirement                                           | Applies to | Status                 |
| :---------------------------------------------------- | :--------- | :--------------------- |
| Readable degradation when the API is unreachable      | All        | ✅ on S-01             |
| No credential value in rendered output                | All        | ✅ asserted in E2E     |
| Loading states inside Suspense, shell painted first   | All        | ✅ pattern set by S-01 |
| Domain errors shown by `code`, never by parsing prose | All        | Convention set         |
| WCAG 2.1 AA                                           | All        | Not started            |
| Keyboard-navigable                                    | All        | Not started            |
| Responsive to tablet width                            | All        | Not started            |

---

## 6. Count

| Milestone | Screens | Built |
| :-------- | :------ | :---- |
| M00       | 1       | 1     |
| M01       | 11      | 0     |
| M02       | 7       | 0     |
| M03       | 2       | 0     |
| **Total** | **21**  | **1** |
