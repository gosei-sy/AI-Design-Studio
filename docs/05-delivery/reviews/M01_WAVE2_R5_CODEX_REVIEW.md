# M01 Wave 2 — Independent Re-review, Round 5

**Review date:** 2026-10-30  
**Scope:** R4-001 documentation correction  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

Most of R4-001 is corrected. The D-8 entries now consistently preserve the
accepted ADR-0008 gate, the main E2E documentation reports 7/7 local success
with CI outstanding, the obsolete notifier statement is marked superseded, and
the historical M00 evidence is generally distinguished from current state.

R4-001 cannot yet be closed because one adjacent current-state paragraph in
`IMPLEMENTATION_STATUS.md` still asserts the exact obsolete E2E state. The table
immediately above it says Chromium is resolved and 7/7 pass locally; the prose
then says E2E remains unverified and blocked because Chromium is absent.

No runtime suite was rerun for this documentation-only review. The last
independently measured baseline remains **517 passing, 0 failed, 0 skipped**,
plus **7/7 E2E**.

## 2. R4-001 disposition

**PARTIALLY FIXED.** Verified corrections include:

- `IMPLEMENTATION_STATUS.md:84` now says accepted ADR-0008 blocks M01-02 and
  M01-03, and A2 is proposed rather than in force.
- `IMPLEMENTATION_PLAN.md:473` now makes D-8 necessary before M01-02 acceptance
  under the accepted ADR.
- `TEST_STRATEGY.md:142-156`, `frontend/README.md:37`,
  `IMPLEMENTATION_PLAN.md:28`, and `docs/06-spikes/README.md:95` now report 7/7
  local E2E success with CI outstanding.
- `M01_WAVE2_REPORT.md:283` now labels the obsolete notifier statement
  superseded.
- The M00 four-spec result is retained as an explicitly historical snapshot in
  the corrected plan and strategy sections.

## 3. Finding

### R5-001 — P2 — The status document still reactivates the obsolete Chromium blocker

**File:** `docs/05-delivery/IMPLEMENTATION_STATUS.md:312-325`

**Evidence:** line 312 correctly records:

> Resolved in M01-11 … 7 specs, 7 passing locally

But lines 316-325 then say:

> Both remain written but unverified … Browser-level E2E remains blocked … the
> Chromium binary is absent.

“Both” refers to E2E and Docker Compose. Only Compose remains unverified. The E2E
claim is current-tense, not a preserved historical measurement, and directly
contradicts the corrected row four lines earlier as well as the current M01 row
at line 84.

**Why it matters:** this is an authoritative current-status document. A reader
still cannot use it to determine whether M01-11 is blocked on the browser binary
or only on CI.

**Required correction:** rewrite lines 316-325 so they distinguish the M00
snapshot from current state:

- at M00 closure, both artifacts were unverified and neither counted toward the
  436;
- now E2E is 7/7 locally and only the CI run is outstanding;
- Docker Compose remains unverified because Docker is unavailable.

Preserve the historical four-test failure in the M00 execution-results section;
do not rewrite it as a new measurement.

**Blocks M01-02 acceptance:** **Yes**, as the last unresolved part of R4-001.
After this documentation correction, acceptance is still independently blocked
on ADR-0008 A2/D-8.

## 4. ADR and readiness status

ADR-0008 A2 remains correctly marked **Proposed, not accepted**. The independent
architecture recommendation remains:

1. approve the same-origin BFF boundary description; and
2. approve option 2, moving reset delivery to a fail-closed pre-production gate.

Those recommendations are not approval. The Product Owner and Solution
Architect must record the decision through ADR governance. Until then, the
accepted A1 position remains in force and M01-02 is incomplete. D-9 continues to
block M01-03 independently.

## 5. Verification

This was a documentation-only review. Runtime suites were deliberately not
rerun or presented as newly measured. Verification was limited to the corrected
documents, a claim-based repository search, formatting, and whitespace checks.

## 6. Decision

**CHANGES REQUIRED.** Correct R5-001 as documentation-only work. Once that
single paragraph is reconciled, no developer-resolvable finding from this round
remains and the status becomes **BLOCKED — OWNER DECISION** on ADR-0008 A2/D-8.
M01-03 must not start and remains independently blocked on D-9.
