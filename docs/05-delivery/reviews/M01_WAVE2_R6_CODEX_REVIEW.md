# M01 Wave 2 — Independent Re-review, Round 6

**Review date:** 2026-10-31  
**Scope:** R5-001 documentation correction  
**Decision:** **BLOCKED — OWNER DECISION**

## 1. Executive summary

R5-001 is **closed**. The current-state documentation now distinguishes the M00
closure snapshot from the present M01 state:

- Playwright E2E was blocked at M00 closure;
- it now passes **7/7 locally** and only the CI run remains outstanding for
  M01-11; and
- Docker Compose remains unverified because Docker is unavailable.

No developer-resolvable finding remains from the M01-02 review. The runtime
implementation remains supported by the last independently measured baseline of
**517 passing, 0 failed, 0 skipped**, plus **7/7 Playwright E2E**. Those suites
were not rerun for this documentation-only correction and are not presented as a
new measurement.

M01-02 is not accepted yet. ADR-0008 A2 remains **Proposed, not accepted**, so
the accepted A1 position remains in force: D-8 blocks M01-02 and therefore
M01-03. The next action belongs to the Product Owner and Solution Architect.

## 2. R5-001 disposition

**PASS.** Independent inspection verified:

- `IMPLEMENTATION_STATUS.md:213-278` explicitly labels the test results as an
  M00 closure snapshot, retains the original four-test failure as historical
  evidence, and separately states the current 7/7 result.
- `IMPLEMENTATION_STATUS.md:309-350` says the carried-over table combines status
  at closure with current disposition, separates E2E from Docker Compose, and
  identifies CI—not Chromium—as the remaining M01-11 gate.
- `IMPLEMENTATION_STATUS.md:128` marks Browser E2E as blocked at M00 closure and
  resolved locally since then.
- The remaining references to four specs, an absent Chromium binary, or
  unexecuted E2E are explicitly historical snapshot or changelog entries.
- `ADR-0008:264-320` remains unchanged in status: A2 is proposed, not in force,
  and option 1 remains binding until a decision is recorded.

## 3. Findings

**None.** No new code, security, architecture, test, or documentation finding
was identified in the authorized review scope.

## 4. Architecture decision required

The independently recommended decision remains:

1. **Approve proposal 1:** record the implemented and browser-verified boundary
   as same-origin through the BFF proxy.
2. **Approve proposal 2, option 2:** allow M01-02 acceptance on the verified
   authentication implementation and make password-reset delivery a fail-closed
   pre-production gate. Production already refuses to start without an
   authorized transport.

This is an architecture recommendation, not approval. ADR-0008 A2 requires an
explicit Product Owner and Solution Architect decision. If either proposal is
rejected, the implementation and acceptance state must follow the accepted
alternative rather than treating the current code as implicit approval.

## 5. Verification performed

| Check                                            | Actual result                                                                                                                      |
| :----------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------- |
| Claim-based search of `IMPLEMENTATION_STATUS.md` | **PASS** — every obsolete E2E-state hit is explicitly historical; current sections consistently say 7/7 local with CI outstanding. |
| `npm run format:check`                           | **PASS** — all matched files use Prettier style.                                                                                   |
| `git diff --check`                               | **PASS** — exit 0.                                                                                                                 |

Runtime test suites were deliberately not rerun because the submitted correction
changed documentation only. No provider call, spend, infrastructure
installation, commit, push, or deployment was performed by this review.

## 6. Remaining blockers

- **ADR-0008 A2 / D-8:** Product Owner + Solution Architect decision; currently
  blocks M01-02 acceptance and therefore M01-03.
- **D-9:** account proofing; independently blocks M01-03.
- **M01-11:** local E2E evidence is complete; CI evidence requires an authorized
  push.
- **M01-12:** Docker remains unavailable, so Compose remains unverified.
- **MFA recovery codes:** still absent and remain a recorded product/security
  gap.

## 7. Readiness decision

**BLOCKED — OWNER DECISION.** All developer-side findings for M01-02 are closed.
Do not ask Claude Code for another implementation or documentation correction at
this point. Record the Product Owner and Solution Architect decision on
ADR-0008 A2/D-8 first. M01-03 must not start and remains independently blocked
on D-9.
