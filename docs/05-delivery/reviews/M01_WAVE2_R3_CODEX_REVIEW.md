# M01 Wave 2 Round 3 Independent Review — M01-02 Authentication

**Review date:** 2026-10-30  
**Reviewer:** Codex — Independent Technical Architect, Senior Code Reviewer, QA Lead  
**Scope:** corrections submitted in `M01_WAVE2_RE_REVIEW_RESPONSE.md`, actual
source, tests, browser behavior, CI, migrations, and current documentation  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

The three runtime defects from the preceding review are corrected. Independent
execution confirmed all of the following:

- Production/development reset-transport checks now reject the previously open
  `DevelopmentLog` path outside Development, with a second environment check in
  the notifier.
- MFA management consults lockout before enrol, enable, and disable; the
  regression test now proves a correct code after the threshold is refused.
- The documented studio path is genuinely same-origin through the Next proxy.
  A real Chromium `window.fetch` flow registered, logged in, called `/me`, and
  stored the session cookie with `HttpOnly`, `Secure`, and `SameSite=Strict`.
- The local Playwright suite passed **7/7**, with no failures or skips.

The reported non-E2E baseline was also reproduced: **491 passed, 0 failed,
0 skipped**. The migration applies cleanly to a new PostgreSQL database and the
build, formatting, type, lint, migration-sync, and dependency gates pass.

M01-02 still cannot be accepted because the D-8 scope decision was made by the
Lead Developer without the required Product Owner/Solution Architect approval
and without superseding the accepted ADR. The binding ADR remains the opposite
of the plan/status text. Two evidence gaps also remain: the claimed “8-case
matrix” is not the complete environment/transport/flag matrix, and the CI E2E
configuration points its proxy at a port/scheme the CI API does not serve.

## 2. Previous finding disposition

| Finding                                       | Result                                      | Evidence                                                                                                                                                                        |
| :-------------------------------------------- | :------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| W2-RR-001 Production accepts `DevelopmentLog` | **PASS, implementation**                    | Startup rejects it outside Development; notifier also checks `IsDevelopment`; relevant integration cases pass. Test-coverage overclaim remains in R3-003.                       |
| W2-RR-002 MFA lockout not enforced            | **PASS**                                    | All three management endpoints check lockout. The correct-code-after-threshold and locked-disable tests pass.                                                                   |
| W2-RR-003 documented browser path fails       | **PASS**                                    | Independent Chromium `window.fetch` probe through the HTTPS studio origin completed registration, login, `/me`, and cookie verification.                                        |
| W2-RR-004 E2E regression                      | **PASS locally / FAIL in CI configuration** | Local suite is 7/7; the committed CI topology cannot reach Playwright's default upstream. See R3-002.                                                                           |
| W2-RR-005 documentation and D-8 sequencing    | **FAIL**                                    | Current-state files were partly corrected, but accepted ADR-0008 still states the contrary boundary and gate; no authorized superseding decision exists. See R3-001 and R3-004. |

## 3. Findings

### R3-001 — P1 — D-8 scope was changed without decision authority or an ADR update

**Files:**

- `AGENTS.md:29`
- `CLAUDE.md:29`
- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:207`
- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:225`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:343`
- `docs/05-delivery/reviews/M01_WAVE2_RE_REVIEW_RESPONSE.md:165`

**Evidence:** ADR-0008 is `Accepted` and names the Product Owner and Solution
Architect as deciders. Its latest amendment says recovery delivery makes M01-02
**incomplete** and says the implemented boundary is cross-origin, with a true
same-origin BFF not implemented. The current implementation and status document
now say the opposite: same-origin BFF is implemented and D-8 does not block
M01-02 acceptance.

The response explicitly calls the D-8 rule a judgement call and says it is the
Product Owner's to overrule. No Product Owner approval is recorded, and no
superseding ADR or later amendment exists. A status page cannot supersede an
accepted architecture decision. This violates the repository rule to obtain
approval and supersede an ADR before changing it.

**Why it matters:** the change is not editorial. It moves a selected Option A
obligation out of M01-02 and changes whether M01-03 may begin. Accepting M01-02
under the status document would ignore the still-binding ADR.

**Required correction:** obtain an explicit decision from the Product Owner and
Solution Architect on D-8. Record the selected recovery-delivery gate and the
now-implemented same-origin boundary in the binding architecture record. Follow
the repository's ADR supersession procedure rather than allowing plan/status
text to override ADR-0008. Then reconcile every dependent gate.

**Blocks M01-02 acceptance:** **Yes.** Under the current accepted ADR, M01-02 is
still incomplete.  
**Blocks M01-03:** **Yes through M01-02 acceptance; D-9 independently blocks
M01-03 as well.**

### R3-002 — P2 — The committed CI E2E topology cannot reach the configured API upstream

**Files:**

- `.github/workflows/ci.yml:186`
- `.github/workflows/ci.yml:200`
- `.github/workflows/ci.yml:204`
- `.github/workflows/ci.yml:209`
- `frontend/playwright.config.ts:5`
- `frontend/playwright.config.ts:52`

**Evidence:** the CI job starts the API with
`ASPNETCORE_URLS=http://localhost:5080`, so no HTTPS listener exists on port 7080. Playwright defaults `API_ORIGIN` to `https://localhost:7080`, and the CI
job does not set `API_ORIGIN`. The Playwright web server therefore proxies to a
listener CI never starts.

CI still sets `NEXT_PUBLIC_API_BASE_URL=http://localhost:5080`, but the
Playwright `webServer.env` deliberately overrides that value to an empty string.
Consequently the stale CI variable neither repairs the upstream nor exercises
the old direct-browser path. Local execution passes because the normal launch
profile exposes both 5080 and 7080; that masks the CI-only mismatch.

**Why it matters:** M01-11 is described as needing only a push, but its committed
CI configuration is deterministically inconsistent with the new proxy design.
The first authorized CI run is expected to fail before it can provide acceptance
evidence.

**Required correction:** set `API_ORIGIN=http://localhost:5080` for the E2E step
or start the CI API on the upstream Playwright expects. Remove the stale
`NEXT_PUBLIC_API_BASE_URL` overrides, then obtain the required CI execution.

**Blocks M01-02 acceptance:** **No; local browser behavior is independently
verified.**  
**Blocks M01-11:** **Yes.**

### R3-003 — P2 — The reset-transport “matrix” is incomplete and does not test the notifier safeguard

**Files:**

- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:844`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:854`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:868`
- `backend/src/AIDesignStudio.Infrastructure/Identity/LoggingPasswordResetNotifier.cs:35`
- `docs/05-delivery/reviews/M01_WAVE2_RE_REVIEW_RESPONSE.md:48`

**Evidence:** three environments × three transports × two logging-flag values is
an 18-case matrix. The theory contains eight rows, with two additional isolated
startup tests, leaving eight combinations uncovered. Its expected-result formula
would also incorrectly classify Development + `Email` as successful if that
missing row were added, while production code correctly rejects `Email`
everywhere.

The theory exercises host startup only. It does not call the notifier with a
capturing logger and therefore does not prove the defense-in-depth property that
reset credentials can never be logged outside Development. The code inspection
is correct, but the response's claim of a complete matrix and outcome-level test
is not.

**Why it matters:** reset tokens are credentials. This project requires an
automated test for important security invariants, and this exact defect survived
the previous hand-picked cases.

**Required correction:** generate or enumerate all 18 combinations with the
correct expected result. Add a direct notifier test with a capturing logger that
proves token logging occurs only for Development + `DevelopmentLog` + opt-in,
and that every non-Development combination omits the credential.

**Blocks M01-02 acceptance:** **Yes.** This is the missing automated evidence for
the previously exploitable recovery control.

### R3-004 — P2 — Documentation reconciliation is still incomplete

**Files:**

- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:201`
- `docs/05-delivery/M01_WAVE2_REPORT.md:283`
- `frontend/README.md:13`
- `frontend/README.md:21`
- `frontend/README.md:37`
- `docs/04-engineering/TEST_STRATEGY.md:128`
- `IMPLEMENTATION_PLAN.md:81`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:266`

**Evidence:** beyond the binding ADR conflict in R3-001:

- the Wave 2 report still says the development notifier throws outside
  Development;
- the frontend README still instructs HTTP `npm run dev`, says the public API
  base defaults to `http://localhost:5080`, and labels E2E unexecuted;
- TEST_STRATEGY still says the four Playwright specs are unverified because
  Chromium is missing;
- current plan/status sections still say the browser is missing and the E2E
  suite is blocked/unexecuted.

The API also retains credentialed CORS configuration while the response and
proxy comments say the same-origin design removes that surface. Retaining a
deliberate fallback is possible, but it must be described as such rather than
simultaneously claimed absent.

**Why it matters:** setup instructions send developers back to the broken HTTP
path, and authoritative quality-status documents contradict measured evidence.

**Required correction:** sweep all current-state documentation by claim, not by
the latest wording. Update setup commands, API-base semantics, E2E status/counts,
recovery behavior, and the deliberate CORS posture. Preserve historical review
records as historical; do not rewrite their original outcomes.

**Blocks M01-02 acceptance:** **Yes together with R3-001.** Documentation is part
of the selected authentication boundary and its safety procedure.

### R3-005 — P3 — The new Next middleware convention is already deprecated

**File:** `frontend/src/middleware.ts:20`

**Evidence:** `npm run build --workspace frontend` succeeds but emits:

```text
The "middleware" file convention is deprecated. Please use "proxy" instead.
```

**Why it matters:** this is new boundary-critical code on Next 16.4.2. Leaving it
on the deprecated convention creates avoidable upgrade risk around forwarded
scheme/host handling.

**Recommended correction:** migrate to the supported `proxy.ts` convention and
re-run the real-browser auth test to prove forwarded-header behavior is
unchanged.

**Blocks M01-02 acceptance:** **No.**

## 4. Architecture and security assessment

The implemented same-origin proxy is a materially better boundary than the
previous cross-origin setup. Browser traffic stays on the studio origin; the API
trusts forwarded scheme/host only from its configured trusted proxy set; Secure
Strict cookies work through the proxy; and the independent browser probe
confirmed the full session path. The loopback HTTP upstream is appropriate for
local TLS termination, with deployed proxy addresses still needing explicit
configuration.

Session revocation, anonymous endpoint uniformity, immutable audit IDs, request
validation, Swagger protection, Data Protection fail-closed behavior, and the
MFA lockout outcome now have credible implementation evidence. D-9 remains a
valid external decision because immutable attribution is not account proofing.

The remaining architectural blocker is governance, not runtime code: the
implemented BFF and proposed recovery gate need to become the approved record
before the next module is allowed to depend on them.

## 5. Execution results

| Command / probe                                         | Actual result                                                                                                      |
| :------------------------------------------------------ | :----------------------------------------------------------------------------------------------------------------- |
| `dotnet build`                                          | **PASS** — 0 warnings, 0 errors                                                                                    |
| `dotnet test`                                           | **PASS** — 313 passed, 0 failed, 0 skipped (185 unit / 16 architecture / 112 integration)                          |
| `npm test`                                              | **PASS** — 14 frontend + 164 contracts                                                                             |
| `dotnet format --verify-no-changes`                     | **PASS** — exit 0                                                                                                  |
| `npm run typecheck`                                     | **PASS** — exit 0                                                                                                  |
| `npm run lint`                                          | **PASS** — exit 0                                                                                                  |
| `npm run format:check`                                  | **PASS** — all matched files use Prettier style                                                                    |
| `npm run build --workspace frontend`                    | **PASS WITH WARNING** — build succeeded; `middleware` convention is deprecated                                     |
| Fresh-database `dotnet-ef database update`              | **PASS** — all three migrations applied to `aids_review_m01w2_r3_20261030`                                         |
| `dotnet-ef migrations has-pending-model-changes ...`    | **PASS** — no model changes since the last migration; EF CLI 10.0.12 emitted an older-than-runtime-10.0.13 warning |
| `npm run audit:prod`                                    | **PASS** — 0 vulnerabilities                                                                                       |
| `dotnet list package --vulnerable --include-transitive` | **PASS** — no vulnerable packages in all nine projects                                                             |
| `git diff --check`                                      | **PASS** — exit 0                                                                                                  |
| `npm run test:e2e --workspace frontend`                 | **PASS locally** — 7 passed, 0 failed, 0 skipped                                                                   |
| Independent Chromium `window.fetch` auth flow           | **PASS** — token 200, registration 202, login 200, `/me` 200; browser stored Strict Secure HttpOnly session cookie |
| CI E2E configuration inspection                         | **FAIL** — proxy expects HTTPS 7080 while CI starts only HTTP 5080                                                 |
| `docker compose config`                                 | **BLOCKED** — `docker: command not found` (exit 127)                                                               |

The temporary review database was dropped after verification. No provider call
or billable operation was performed.

## 6. Required corrections and readiness decision

Before M01-02 can be accepted:

1. Obtain and record the authorized D-8 decision and reconcile/supersede
   ADR-0008 so the binding record matches the implemented BFF and selected gate.
2. Complete the reset-transport/notifier automated evidence in R3-003.
3. Reconcile current documentation in R3-004.

In parallel, correct the deterministic CI upstream mismatch in R3-002 before
requesting M01-11 acceptance. R3-005 is non-blocking maintenance.

**M01-02 decision: CHANGES REQUIRED.**  
**M01-03 readiness: NOT READY — M01-02 is not accepted and D-9 independently
blocks account/organization role attachment.**

This review changed only this report. It did not modify product source, tests,
schema, configuration, requirements, or architectural decisions; it did not
commit, push, deploy, call Stitch, or spend money.
