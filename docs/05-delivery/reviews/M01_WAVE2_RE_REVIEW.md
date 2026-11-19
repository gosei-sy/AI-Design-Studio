# M01 Wave 2 Independent Re-review — M01-02 Authentication

**Review date:** 2026-10-26  
**Reviewer:** Codex — Independent Technical Architect, Senior Code Reviewer, QA Lead  
**Scope:** corrections submitted in `M01_WAVE2_REVIEW_RESPONSE.md`, actual source,
migrations, tests, browser behavior, and current documentation  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

The correction set materially improves the authentication implementation. Session
stamp validation is now attached and demonstrated with two clients; the three
anonymous account-enumeration differences were removed; enabled TOTP secrets are
no longer re-disclosed; immutable user IDs are used for audit attribution; request
validation rejects malformed bodies; non-Development hosts require a Data
Protection key protector; Swagger is protected; and line-ending noise is fixed.

The reported non-browser baseline was independently reproduced: **482 tests
passed, 0 failed, 0 skipped**. The migration applied to a new PostgreSQL database,
the model is in sync, the frontend production build succeeds, and the formatting,
type, lint, package-vulnerability, and diff checks pass.

M01-02 is nevertheless **not accepted**. Three P1 defects remain:

1. Production accepts `DevelopmentLog` as a password-reset transport, allowing
   either silent non-delivery or reset-token logging in Production.
2. MFA management records lockout but never enforces it; a valid sixth code was
   accepted after five failures and returned `204`.
3. The documented browser path remains HTTP frontend to HTTPS API. A real
   Chromium run obtained an antiforgery token but registration returned
   `400 auth.antiforgery_failed`, with no API cookie stored.

The existing E2E suite also regressed when run with the API available:
**2 passed, 1 failed, 1 skipped**. Documentation still contains conflicting
current-state and dependency claims. D-8 and D-9 remain unresolved; the latter
independently blocks M01-03.

## 2. Round-1 finding disposition

| Finding                           | Re-review result              | Evidence                                                                                                                                  |
| :-------------------------------- | :---------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------- |
| W2-001 session revocation         | **PASS**                      | Identity cookie validation is attached; two-client change/reset tests passed.                                                             |
| W2-002 account enumeration        | **PASS**                      | Password policy precedes lookup; lockout has a uniform external response; recovery availability moved to startup.                         |
| W2-003 password recovery          | **FAIL**                      | The request-time oracle is removed, but the Production startup gate is bypassable with `DevelopmentLog`; see W2-RR-001. D-8 remains open. |
| W2-004 MFA policy                 | **FAIL**                      | Secret re-disclosure and passwordless disablement are fixed, but lockout is not enforced; see W2-RR-002.                                  |
| W2-005 audit identity             | **BLOCKED**                   | The immutable user ID correction passes. S-21 remains partial and M01-03 remains blocked on account-proofing decision D-9.                |
| W2-006 browser boundary           | **FAIL**                      | The actual documented HTTP-to-HTTPS browser path fails antiforgery; see W2-RR-003.                                                        |
| W2-007 request validation         | **PASS**                      | Property-targeted validation and the group filter are present; malformed-body integration tests passed.                                   |
| W2-008 Data Protection encryption | **PASS, pre-deployment gate** | Non-Development startup requires an XML protector. Selecting the production protector remains D-5.                                        |
| W2-009 Swagger exposure           | **PASS**                      | Anonymous Swagger is rejected and authenticated Development access is covered.                                                            |
| W2-010 documentation              | **FAIL**                      | Current-state text and D-8/M01-02 sequencing still conflict; see W2-RR-005.                                                               |
| W2-011 line endings               | **PASS**                      | `.gitattributes` is present and `git diff --check` exits 0.                                                                               |

## 3. Findings

### W2-RR-001 — P1 — Production accepts a development reset-token transport

**Files:**

- `backend/src/AIDesignStudio.Infrastructure/Identity/IdentityStartupValidation.cs:43`
- `backend/src/AIDesignStudio.Infrastructure/Identity/IdentityStartupValidation.cs:55`
- `backend/src/AIDesignStudio.Infrastructure/Identity/LoggingPasswordResetNotifier.cs:33`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:827`

**Evidence:** startup rejects `Email` because it is unimplemented, and rejects
`None` only when `environment.IsProduction()`. Every other enum value returns at
lines 55-57. Therefore `DevelopmentLog` is accepted in Production. The notifier
does not receive or check `IHostEnvironment`; when
`LogTokensInDevelopment=true`, it logs the reset credential solely from the
configuration values. With the flag false, the same Production configuration
starts but silently delivers no recovery message.

The new matrix covers Production + `None` and Staging + `Email`, but contains no
Production + `DevelopmentLog` case. This contradicts the comments and response
claim that Production refuses to start without a real transport and that token
logging is Development-only.

**Why it matters:** a configuration intended only for local development can
either leak reset credentials to production logs or leave users without recovery
while startup reports success.

**Required correction:** reject `DevelopmentLog` unless
`environment.IsDevelopment()` and add a defense-in-depth environment check in
the notifier. Test the complete environment/transport/logging matrix, including
Production + `DevelopmentLog` with both flag values. A real Production transport
still requires D-8.

**Blocks M01-03:** **Yes.** M01-02 cannot be accepted while its fail-closed
recovery control is bypassable.

### W2-RR-002 — P1 — MFA lockout is recorded but not enforced

**Files:**

- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:331`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:355`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:445`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:944`

**Evidence:** invalid codes call `AccessFailedAsync`, but neither the enable nor
disable endpoint checks `IsLockedOutAsync`. `VerifyTwoFactorTokenAsync` verifies
the token; it does not enforce sign-in lockout. The regression test stops after
asserting that the stored user is locked out, so it passes even though the
endpoint remains usable.

A runtime Chromium probe against the real API registered and authenticated a
user, enrolled TOTP, submitted five invalid codes (five `400` responses), then
submitted the correct current code. The sixth request returned **`204`** and
enabled MFA despite the lockout.

**Why it matters:** an attacker holding a session still has an effectively
unbounded online TOTP-management oracle. The persistence flag is not a guard
unless the protected operation consults it.

**Required correction:** enforce lockout before factor-management password/code
verification and define what happens to the active session when the threshold is
crossed. Add a regression test proving a correct code after the threshold is
rejected. A separate factor-management rate limiter may be preferable to sharing
login lockout, but it must be an enforced, tested control.

**Blocks M01-03:** **Yes.** MFA policy is an explicit Option A obligation.

### W2-RR-003 — P1 — The documented browser authentication path still fails

**Files:**

- `README.md:101`
- `README.md:126`
- `frontend/README.md:13`
- `frontend/src/lib/api.ts:10`
- `backend/src/AIDesignStudio.Api/Identity/SessionAuthentication.cs:66`
- `backend/src/AIDesignStudio.Api/Identity/SessionAuthentication.cs:72`
- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:225`

**Evidence:** the API URL was changed to `https://localhost:7080`, but both
READMEs still start and open the frontend at `http://localhost:3000`. Modern
SameSite calculation is schemeful: an HTTP top-level site and HTTPS API are not
the same site for a `SameSite=Strict` cookie. The ADR amendment says the
documented path is HTTPS everywhere, but no HTTPS frontend launch path is
implemented or documented.

This was reproduced in Chromium with both real services running. From
`http://localhost:3000`, the browser received `200` and a request token from
`https://localhost:7080/api/v1/auth/antiforgery-token`; the subsequent
credentialed registration returned **`400 auth.antiforgery_failed`**, and the
browser cookie store contained **zero API cookies**.

The response itself labels the browser test an open gap while its summary labels
W2-006 fixed. Execution resolves that contradiction: it is not fixed.

**Why it matters:** the authentication API works in `TestServer` and in a page
whose top-level origin is the API, but fails for users following the project's
actual frontend setup.

**Required correction:** establish one executable boundary and test it in a real
browser. Prefer a true same-origin reverse proxy/BFF. If direct cross-origin
access remains the decision, serve the frontend over HTTPS on the same schemeful
site, align CORS/cookie settings and all setup/configuration documents, and add an
E2E flow covering token issuance, registration/login, authenticated use, logout,
and antiforgery rejection.

**Blocks M01-03:** **Yes.** Authorization must not be layered onto a browser
session path that cannot establish the session.

### W2-RR-004 — P2 — The existing E2E quality gate regressed with the API running

**Files:**

- `frontend/e2e/status.spec.ts:34`
- `frontend/src/app/page.tsx:12`
- `frontend/playwright.config.ts:8`

**Evidence:** `npm run test:e2e --workspace frontend` was executed while the real
API and production frontend were available. Result: **2 passed, 1 failed,
1 skipped**. The status-page test expected a health result or a clear API
connection error, but received raw `401` problem JSON because `loadStatus` groups
the anonymous readiness request and now-protected provider request in one
`Promise.all`. The provider test then skipped. The Playwright configuration also
still documents and defaults its API to `http://localhost:5080`, contrary to the
new HTTPS boundary.

**Why it matters:** protecting the provider endpoint is correct, but it regressed
the existing developer status surface and its executable gate. The response's
preserved Wave 1 E2E result is no longer the result of the current code.

**Required correction:** keep the anonymous health result independent from the
authenticated provider request, render an intentional authentication-required
state rather than raw problem JSON, align Playwright with the selected browser
boundary, and make the API-up E2E suite pass without weakening assertions.

**Blocks M01-03:** **Yes for acceptance of the current wave.** A known failing
local E2E gate must be corrected before adding authorization behavior.

### W2-RR-005 — P2 — Current documentation and M01 sequencing remain contradictory

**Files:**

- `docs/05-delivery/IMPLEMENTATION_STATUS.md:84`
- `IMPLEMENTATION_PLAN.md:466`
- `IMPLEMENTATION_PLAN.md:470`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:339`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:375`
- `docs/04-engineering/SECURITY_REQUIREMENTS.md:159`
- `docs/05-delivery/M01_WAVE2_REPORT.md:283`

**Evidence:** the current milestone table still says “Wave 1 only” and that
M01-02 stopped at open D-7. The plan, status, and security requirements still call
the active session boundary “same-origin,” while the appended ADR amendment calls
it cross-origin. The Wave 2 report still says the development notifier throws
outside Development; the implementation does not.

The gate semantics also conflict. ADR-0008 states that M01-02 is incomplete until
D-8 resolves recovery delivery. The decisions table says D-8 is needed only
before Production, while M01-03 depends on accepted M01-02. Those statements do
not define whether D-8 blocks M01-02 acceptance and therefore M01-03.

**Why it matters:** engineers cannot safely sequence the next wave when the
authoritative documents disagree about both the deployed boundary and the entry
gate.

**Required correction:** sweep all current-state claims, leaving historical text
clearly marked as superseded. Decide and record one D-8 rule: either D-8 blocks
M01-02 acceptance, or recovery delivery is formally moved to a later
pre-production gate through an approved decision amendment. Do not mark M01-02
complete under the current ADR wording.

**Blocks M01-03:** **Yes until sequencing is unambiguous; D-9 independently
blocks it regardless.**

## 4. Architecture and security assessment

The layer placement remains sound: Identity persistence is in Infrastructure,
request/session concerns are in the API host, and `ICurrentUser` stays in the
Application layer. The fallback authorization policy, immutable audit subject,
security-stamp validation, request validation, Data Protection persistence, and
non-Development key-protector gate are appropriate foundations.

The remaining failures are control-boundary failures, not reasons to replace the
architecture. In each P1 case, the mechanism exists but the real execution path
does not pass through an enforced guard: Production can select a development
transport, MFA endpoints do not consult lockout, and the real browser is on a
different schemeful site from the API.

No evidence was found that M01-03 role or organization work had started. D-9 is
correctly kept open because immutable attribution does not prove the account
belongs to the asserted person.

## 5. Execution results

| Command / probe                                          | Actual result                                                                                                      |
| :------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------- |
| `dotnet build`                                           | **PASS** — 0 warnings, 0 errors                                                                                    |
| `dotnet test`                                            | **PASS** — 304 passed, 0 failed, 0 skipped (185 unit / 16 architecture / 103 integration)                          |
| `npm test`                                               | **PASS** — 14 frontend + 164 contracts                                                                             |
| `dotnet format --verify-no-changes`                      | **PASS** — exit 0                                                                                                  |
| `npm run typecheck`                                      | **PASS** — exit 0                                                                                                  |
| `npm run lint`                                           | **PASS** — exit 0                                                                                                  |
| `npm run format:check`                                   | **PASS** — all matched files use Prettier style                                                                    |
| `npm run build --workspace frontend`                     | **PASS** — production build compiled successfully                                                                  |
| Fresh-database `dotnet-ef database update`               | **PASS** — all three migrations applied to `aids_review_m01w2_rr_20261026`                                         |
| `dotnet-ef migrations has-pending-model-changes ...`     | **PASS** — no model changes since the last migration; EF CLI 10.0.12 emitted an older-than-runtime-10.0.13 warning |
| `npm run audit:prod`                                     | **PASS** — 0 vulnerabilities                                                                                       |
| `dotnet list package --vulnerable --include-transitive`  | **PASS** — no vulnerable packages in all nine projects                                                             |
| `git diff --check`                                       | **PASS** — exit 0                                                                                                  |
| Browser HTTP-frontend → HTTPS-API auth probe             | **FAIL** — token GET 200; registration 400 `auth.antiforgery_failed`; zero API cookies stored                      |
| MFA lockout adversarial probe                            | **FAIL** — five invalid codes returned 400; correct sixth code returned 204                                        |
| `npm run test:e2e --workspace frontend` with API running | **FAIL** — 2 passed, 1 failed, 1 skipped                                                                           |
| `docker compose config`                                  | **BLOCKED** — `docker: command not found` (exit 127)                                                               |

The temporary review database was dropped after verification. No provider call or
billable operation was performed.

## 6. Required corrections and readiness decision

Before M01-02 can be accepted:

1. Close W2-RR-001 with an environment-enforced reset-transport matrix.
2. Close W2-RR-002 with actual MFA lockout/rate-limit enforcement and a test that
   attempts a valid code after the threshold.
3. Close W2-RR-003 with an executable browser boundary and auth E2E coverage.
4. Restore the API-up Playwright suite in W2-RR-004.
5. Reconcile current documentation and decide the D-8 acceptance gate in
   W2-RR-005.
6. Resolve D-9 before any M01-03 implementation. If ADR-0008 continues to make
   recovery delivery an M01-02 obligation, resolve D-8 before M01-02 acceptance
   as well.

**M01-02 decision: CHANGES REQUIRED.**  
**M01-03 readiness: NOT READY — blocked by the P1 findings, the failing E2E
gate, M01-02 acceptance, and D-9.**

This review changed only this report. It did not modify product source, tests,
schema, configuration, requirements, or architectural decisions; it did not
commit, push, deploy, call Stitch, or spend money.
