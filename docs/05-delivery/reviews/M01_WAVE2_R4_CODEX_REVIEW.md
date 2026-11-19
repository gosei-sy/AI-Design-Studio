# M01 Wave 2 — Independent Re-review, Round 4

**Review date:** 2026-10-30  
**Scope:** R3-001 through R3-005 and the claims in
`M01_WAVE2_R3_RESPONSE.md`  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

The runtime and test corrections are credible. I independently verified the
complete reset-transport matrix and notifier tests, the CI Playwright origin,
the supported Next.js proxy convention, the real browser suite, the build, the
full test suites, formatting, migration synchronization, and dependency
vulnerability checks.

Measured evidence is **517 passing, 0 failed, 0 skipped** across the normal
suites (201 unit, 16 architecture, 122 integration, 14 frontend, 164 contract),
plus **7 Playwright E2E passing, 0 failed, 0 skipped**. The M01-02 implementation
has no remaining code defect identified in this round.

R3-001 was handled correctly as a governance issue: the unauthorized D-8 rule
was withdrawn, ADR-0008 A2 is explicitly proposed rather than applied, and the
accepted position remains in force. Therefore M01-02 still cannot be accepted
until the Product Owner and Solution Architect decide the boundary and D-8
gate.

One developer-resolvable issue remains. R3-004 was reported fixed, but current
documentation still contains mutually exclusive D-8 and E2E states. This is a
P2 documentation-integrity finding. After it is corrected, the review should
move directly to **BLOCKED — OWNER DECISION**, not another implementation loop.

## 2. Previous-finding disposition

| Finding                                                    | Result                           | Independent evidence                                                                                                                                                                                                                               |
| :--------------------------------------------------------- | :------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **R3-001** — unauthorized D-8 decision                     | **BLOCKED, correctly escalated** | `IMPLEMENTATION_STATUS.md:343-374` withdraws the rule; `ADR-0008:264-320` marks A2 proposed and says option 1 remains in force. Product Owner + Solution Architect approval is still absent.                                                       |
| **R3-002** — CI E2E origin mismatch                        | **PASS**                         | `.github/workflows/ci.yml:212-219` sets `API_ORIGIN=http://localhost:5080`, matching `ASPNETCORE_URLS`; Playwright has the same default.                                                                                                           |
| **R3-003** — incomplete reset matrix and notifier evidence | **PASS**                         | `AuthenticationTests.cs:862-881` enumerates all 18 environment × transport × flag combinations. `PasswordResetNotifierTests.cs` executes 16 cases, including 11 negative token-disclosure cases and undeliverable-reset logging without the token. |
| **R3-004** — documentation reconciliation                  | **FAIL**                         | Finding R4-001 below. Correct new sections were added, but contradictory old current-state claims remain.                                                                                                                                          |
| **R3-005** — deprecated Next middleware convention         | **PASS**                         | `frontend/src/proxy.ts` replaces `middleware.ts`; the frontend build has no middleware deprecation warning; Playwright remains 7/7.                                                                                                                |

## 3. Finding

### R4-001 — P2 — Current documentation still asserts incompatible D-8 and E2E states

**Files and evidence:**

- `docs/05-delivery/IMPLEMENTATION_STATUS.md:84` calls D-8 a
  **pre-production gate**, while the same file at lines 350-374 correctly says
  the accepted ADR makes M01-02 incomplete and that no document may claim the
  gate is resolved.
- `IMPLEMENTATION_PLAN.md:466` lists D-8 as needed only **Before Production**,
  contrary to the accepted ADR and the still-pending A2 decision.
- `docs/04-engineering/TEST_STRATEGY.md:101-113` correctly describes seven
  executed Playwright specs, but lines 142-151 still call four specs unverified
  and say Chromium is not installed.
- `frontend/README.md:37` still labels the Playwright specs “not yet executed.”
- `IMPLEMENTATION_PLAN.md:28` says 4/4 specs pass even though the current suite
  and measured evidence are 7/7. Its M00-closure table may retain the historical
  four-spec state, but lines 84-85 incorrectly say the E2E suite **remains**
  unverified.
- `docs/06-spikes/README.md:95` still presents missing Chromium and unexecuted
  E2E as a current environment limit.
- `docs/05-delivery/M01_WAVE2_REPORT.md:58` correctly marks the notifier behavior
  superseded, but line 283 repeats the old claim that it throws outside
  Development without marking that row superseded.

The R3 response claims R3-004 is fixed, but these are live contradictions in
setup, strategy, milestone, and status documents—not merely immutable historical
review results.

**Why it matters:** engineers cannot determine the current acceptance gate or
the current browser evidence from the authoritative documents. The D-8 mismatch
is especially material because one version permits acceptance and the binding
version forbids it.

**Required correction:** perform a claim-based documentation sweep:

1. Until A2 is approved, make every current-state D-8 entry say that the
   accepted ADR blocks M01-02 and therefore M01-03.
2. Make current E2E sections consistently state 7/7 local success, with only
   the CI run outstanding for M01-11.
3. Preserve M00 and earlier-wave measurements as explicitly historical
   snapshots; remove or qualify present-tense words such as “remains” when they
   turn those snapshots into current claims.
4. Mark the stale notifier row in the Wave 2 submission as superseded, matching
   the correction already recorded at the top of that report.

**Blocks M01-02 acceptance:** **Yes**, together with the unresolved ADR/D-8
decision. It does not block correcting the documents or obtaining the owner
decision.

## 4. Architecture and security assessment

The same-origin BFF implementation is the preferred technical boundary. The
browser talks to one HTTPS origin, API forwarding is server-side, the trusted
forwarded-header scope is loopback-only in the verified local topology, and the
real Chromium tests prove that the Strict, Secure, HttpOnly session cookie lands
in the browser and works.

The implementation is nevertheless ahead of the accepted record: ADR-0008 A1
still describes cross-origin/same-site behavior. A2 correctly acknowledges that
gap and is not represented as approved. I recommend approving A2 proposal 1
(same-origin BFF) and proposal 2 option 2 (reset delivery as a fail-closed
pre-production gate), but that recommendation is not a Product Owner or Solution
Architect decision.

The reset-notifier controls now have outcome-level evidence. Production startup
rejects the development transport; token logging is limited to Development plus
the explicit development transport and opt-in; the full configuration matrix is
covered. D-9 account proofing, D-5 Data Protection key protection, MFA recovery
codes, and the external reset-delivery transport remain genuine open risks or
decisions and are not represented as implemented.

## 5. Execution results

| Command / check                                         | Actual result                                                                                                                              |
| :------------------------------------------------------ | :----------------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet build`                                          | **PASS** — 0 warnings, 0 errors. An initial parallel run encountered transient output-file locks; the required sequential rerun was clean. |
| `dotnet test`                                           | **PASS** — 339 passed, 0 failed, 0 skipped: 201 unit, 16 architecture, 122 integration.                                                    |
| `npm test`                                              | **PASS** — 14 frontend + 164 contracts.                                                                                                    |
| `npm run test:e2e --workspace frontend`                 | **PASS locally** — 7 passed, 0 failed, 0 skipped against both services.                                                                    |
| `npm run build --workspace frontend`                    | **PASS** — production build completed; no middleware deprecation warning.                                                                  |
| `dotnet format --verify-no-changes`                     | **PASS** — exit 0.                                                                                                                         |
| `npm run typecheck`                                     | **PASS** — exit 0.                                                                                                                         |
| `npm run lint`                                          | **PASS** — exit 0.                                                                                                                         |
| `npm run format:check`                                  | **PASS** — all matched files use Prettier style.                                                                                           |
| Fresh-database migration update                         | **PASS** — all three migrations applied to a new database.                                                                                 |
| `dotnet-ef migrations has-pending-model-changes ...`    | **PASS** — no pending model changes; CLI 10.0.12 emitted an older-than-runtime-10.0.13 warning.                                            |
| `npm run audit:prod`                                    | **PASS** — 0 vulnerabilities.                                                                                                              |
| `dotnet list package --vulnerable --include-transitive` | **PASS** — no vulnerable packages across all nine projects.                                                                                |
| `git diff --check`                                      | **PASS** — exit 0.                                                                                                                         |
| `docker compose config`                                 | **BLOCKED** — Docker remains unavailable; M01-12 is unchanged.                                                                             |

The temporary review database was dropped. No Stitch call, billable operation,
commit, push, deployment, or infrastructure installation was performed.

## 6. Required next actions

1. Claude Code corrects R4-001 as documentation-only work and runs
   `npm run format:check`. It must not rewrite historical measurement records as
   if they were current runs.
2. The Product Owner and Solution Architect explicitly decide both ADR-0008 A2
   proposals. If approved, record that approval using the repository's ADR
   governance and reconcile all current-state documents.
3. D-8 is then handled according to that decision. D-9 remains independently
   required before M01-03.

## 7. Readiness decision

**CHANGES REQUIRED.** R4-001 remains within the developer's control. The M01-02
runtime implementation is technically ready on the evidence reviewed, but the
task is **not accepted**. After the documentation correction, acceptance remains
**BLOCKED — OWNER DECISION** on ADR-0008 A2/D-8. M01-03 must not start and is
also independently blocked on D-9.
