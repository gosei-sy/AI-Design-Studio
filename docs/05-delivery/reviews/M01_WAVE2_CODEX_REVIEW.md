# M01 Wave 2 Independent Review — M01-02 Authentication

**Review date:** 2026-10-26  
**Reviewer:** Codex — Independent Technical Architect, Senior Code Reviewer, QA Lead  
**Scope:** M01-02 and its decisions, implementation, migration, tests, and documentation  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

The implementation has a sound foundation: Identity storage is kept in
Infrastructure, HTTP/session concerns remain in the API host, the fallback
authorization policy protects mapped application endpoints by default, the
migration applies cleanly to an empty PostgreSQL database, and all reported 464
automated tests were independently reproduced with no skips.

M01-02 is nevertheless **not accepted**. Six high-severity security or runtime
defects remain:

1. security-stamp validation is configured but never attached to the application
   cookie, so password changes and resets do not revoke other sessions;
2. public authentication endpoints expose account existence through three
   independently reproduced response differences;
3. password recovery always fails for a known user outside Development;
4. an authenticated session can retrieve an already-enabled TOTP secret and use
   it to disable MFA;
5. the system accepts an unverified, self-asserted email as the human audit
   identity; and
6. the documented local/browser path is HTTP and cross-origin while the auth
   implementation requires Secure cookies and describes a same-origin BFF.

The passing tests are real, but they do not exercise these adversarial and
production-environment cases. M01-03 must remain not started until the P1
findings and the blocking P2 findings are corrected and independently
re-reviewed.

## 2. Acceptance and obligation matrix

| Requirement or D-7 obligation                                              | Result           | Evidence                                                                                                                                                |
| :------------------------------------------------------------------------- | :--------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Protected mapped API endpoints return `401` problem details when anonymous | **PASS**         | Runtime probe of `/api/v1/providers` returned `401 application/problem+json`                                                                            |
| All non-health endpoints require authentication                            | **FAIL**         | Anonymous `/swagger/v1/swagger.json` and `/swagger/index.html` both returned `200` in Development; see W2-009                                           |
| Identity is available through `ICurrentUser`                               | **PASS, narrow** | `/api/v1/auth/me` and integration coverage demonstrate propagation; proof that the email belongs to the human is absent, so S-21 is not met; see W2-005 |
| Authenticated and unauthenticated integration coverage                     | **PASS**         | 85 integration tests passed, including 28 auth tests                                                                                                    |
| Security headers                                                           | **PASS**         | Integration test and runtime responses confirm CSP, HSTS on HTTPS, `nosniff`, frame and referrer headers                                                |
| Password policy                                                            | **PASS**         | Identity options and integration test                                                                                                                   |
| Lockout                                                                    | **PARTIAL**      | Persistence and threshold work, but the distinct `423` is an account-existence oracle; see W2-002                                                       |
| Password change and session revocation                                     | **FAIL**         | The caller is refreshed, but the cookie has no security-stamp validation event; see W2-001                                                              |
| Password recovery                                                          | **FAIL**         | Known-account recovery throws in Production; see W2-003                                                                                                 |
| MFA policy                                                                 | **FAIL**         | Enabled secrets are re-disclosed and management failures are not counted; see W2-004                                                                    |
| Antiforgery                                                                | **PARTIAL**      | The explicit filter works over HTTPS; the documented HTTP path cannot issue a token and returns `500`; see W2-006                                       |
| Data Protection key persistence                                            | **PARTIAL**      | Persistence and scale-out sharing work, but the key ring is stored unencrypted; see W2-008                                                              |
| Clean migration and model sync                                             | **PASS**         | All three migrations applied to a new review database; pending-model check reported no changes                                                          |
| D-3 and D-7 decisions consistently documented                              | **FAIL**         | Current-state sections still say Wave 1 only, D-7 Open, and four roles; see W2-010                                                                      |

## 3. Findings

### W2-001 — P1 — Password changes and resets do not revoke other sessions

**Files:**

- `backend/src/AIDesignStudio.Api/Identity/SessionAuthentication.cs:35`
- `backend/src/AIDesignStudio.Api/Identity/SessionAuthentication.cs:38`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:184`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:364`

**Evidence:** `SecurityStampValidatorOptions.ValidationInterval` is configured,
but the application cookie is registered with a plain `AddCookie`. Its events set
only the redirect handlers; there is no
`OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync` and no
equivalent Identity cookie registration. Configuring the interval alone does not
invoke the validator. [Microsoft's Identity documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0)
states that security stamp validation is attached through the cookie's
`OnValidatePrincipal` event.

The existing password-change test proves only that the old password cannot create
a new session and that the caller's refreshed session remains usable. It never
creates a second pre-existing session and therefore cannot detect this defect.

**Why it matters:** a stolen or forgotten cookie remains valid for its full
sliding lifetime after password change or password reset. The comments and ADR
claim the opposite.

**Required correction:** register the application cookie through the Identity
cookie helpers or explicitly attach the security-stamp validator. Add a
two-client integration test that signs both clients in, changes or resets the
password through one, advances/controls the validation interval, and proves the
other receives `401` while the refreshed caller remains authenticated.

**Blocks M01-03:** **Yes.** Downstream authorization cannot rely on sessions that
security-sensitive account changes cannot revoke.

### W2-002 — P1 — Public auth endpoints disclose whether an account exists

**Files:**

- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:49`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:95`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:201`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthenticationTests.cs:264`

**Evidence from a running Production host against a freshly migrated review
database:**

| Probe                                | Existing account      | Unknown account                |
| :----------------------------------- | :-------------------- | :----------------------------- |
| Register with the same weak password | `202`                 | `400 auth.registration_failed` |
| Fifth wrong login attempt            | `423 auth.locked_out` | `401 auth.invalid_credentials` |
| Forgot password                      | `500`                 | `202`                          |

Registration checks for an existing user before evaluating password policy.
Login intentionally emits a distinct lockout response. Forgot-password invokes a
notifier only for an existing user, and that notifier throws in Production. The
current tests compare only duplicate registration with a strong password, the
first wrong password with an unknown address, and recovery in Development; they
do not cover the distinguishing cases above.

**Why it matters:** attackers can enumerate accounts for credential stuffing,
targeted phishing, and deliberate lockout. This directly contradicts the Wave 2
report's statement "No account enumeration."

**Required correction:** make observable responses uniform across the full state
space, not just the tested happy pair. Validate password policy independently of
the account lookup, do not expose account lockout to anonymous callers through a
distinct response, and make recovery transport configuration independent of
whether the submitted address exists. Add paired Production-environment tests
for weak duplicate registration, the lockout threshold, and unavailable
recovery transport.

**Blocks M01-03:** **Yes.** This is an actual vulnerability in anonymous public
endpoints.

### W2-003 — P1 — Password recovery is not implemented outside Development

**Files:**

- `backend/src/AIDesignStudio.Infrastructure/Identity/LoggingPasswordResetNotifier.cs:19`
- `backend/src/AIDesignStudio.Infrastructure/Identity/LoggingPasswordResetNotifier.cs:30`
- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:40`
- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:65`
- `docs/05-delivery/M01_WAVE2_REPORT.md:277`

**Evidence:** the only `IPasswordResetNotifier` throws outside Development.
Against a Production host, `/forgot-password` returned `500` for a known account.
The ADR says recovery became non-optional M01 scope and lists it as implemented,
while the report defers the actual email transport to M02.

**Why it matters:** a production user who forgets a password has no recovery
path. The behavior is data-dependent, also creating the enumeration channel in
W2-002.

**Required correction:** either provide an authorized, secure recovery-delivery
mechanism in M01, or formally amend the decision/scope and keep M01-02 incomplete
until that dependency is available. A missing production notifier should be a
uniform startup/configuration failure, not an exception triggered only after a
known address is submitted. Do not log real reset credentials in shared CI or
production logs.

**Blocks M01-03:** **Yes.** ADR-0008 explicitly makes recovery part of this task.

### W2-004 — P1 — An enabled TOTP secret is re-disclosed and can be used to remove MFA

**File:** `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:250`

**Evidence:** `/two-factor/enroll` fetches the existing authenticator key and
returns it even when `TwoFactorEnabled` is already true. `/two-factor/disable`
accepts a current code generated from that key. Consequently, a caller holding a
session can call enroll, read the enabled secret, generate a valid code, and
disable MFA. The comment at lines 327-329 says the endpoint protects against
exactly that stolen-session case, but the enroll endpoint defeats the protection.

Invalid codes at enable and disable also do not call `AccessFailedAsync` or any
other throttle, contradicting the ADR and security requirement that failed second
factors count toward lockout. Only the login-time MFA branch does so.

**Why it matters:** TOTP is not an independent factor once the active shared
secret can be recovered from a session. Management endpoints also permit
unbounded online guessing.

**Required correction:** never return an existing key after MFA is enabled.
Require recent reauthentication/current-password proof plus an existing factor or
recovery code for rotation or disablement. Count or rate-limit invalid factor
management attempts. Add regression tests proving an enabled secret is not
recoverable and repeated invalid management codes are bounded.

**Blocks M01-03:** **Yes.** The selected Option A makes MFA policy explicit M01
scope.

### W2-005 — P1 — The recorded audit identity is self-asserted, not a verified human identity

**Files:**

- `backend/src/AIDesignStudio.Infrastructure/Identity/IdentityServiceCollectionExtensions.cs:53`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:45`
- `backend/src/AIDesignStudio.Api/Identity/HttpContextCurrentUser.cs:26`
- `docs/04-engineering/SECURITY_REQUIREMENTS.md:154`

**Evidence:** public registration accepts an arbitrary email, confirmed email is
explicitly disabled, and `RequireAuditIdentity` records that unverified email
instead of the immutable user ID. A caller can therefore register another
person's address and be recorded in future approval/audit fields as that person.
The current integration test proves only propagation of the supplied string.

**Why it matters:** S-21 requires approval actions to be attributable to a real
identity. Account squatting becomes more dangerous as soon as M01-03 assigns
organizations and Approver roles.

**Required correction:** decide and implement account proofing before role and
organization assignment. Acceptable directions include confirmed-email delivery
or disabling public self-registration in favor of trusted invitation/admin
provisioning. Persist the immutable user ID as the audit subject and treat email
as display metadata. Until proofing exists, mark S-21 partial rather than met.

**Blocks M01-03:** **Yes.** Roles must not be attached to an unproved identity.

### W2-006 — P1 — The documented browser path cannot use the implemented session

**Files:**

- `backend/src/AIDesignStudio.Api/Identity/SessionAuthentication.cs:42`
- `backend/src/AIDesignStudio.Api/Identity/SessionAuthentication.cs:83`
- `backend/src/AIDesignStudio.Api/Properties/launchSettings.json:4`
- `frontend/src/lib/api.ts:10`
- `.env.example:14`
- `README.md:93`

**Evidence:** both session and antiforgery cookies use
`CookieSecurePolicy.Always`, but the default launch profile, frontend API base
URL, environment example, API contracts, and README all use
`http://localhost:5080`. On that documented path,
`GET /api/v1/auth/antiforgery-token` and every state-changing auth request
returned `500` with:

```text
AntiforgeryOptions.Cookie.SecurePolicy = Always, but current request is not SSL.
```

The tests hide this mismatch by forcing `https://localhost` in
`StudioApiFactory`. Separately, the ADR calls the design same-origin/BFF, while
the current browser configuration directly calls a different origin and the
frontend `fetch` helper does not set `credentials: "include"`.

**Why it matters:** the auth API passes in-process tests but cannot be used by a
browser following the repository's setup instructions.

**Required correction:** establish one coherent browser boundary. Prefer an
actual same-origin/BFF route and HTTPS local profile. If a cross-origin API is
intentional, amend the ADR, document HTTPS origins, send credentials explicitly,
and add a real browser integration test covering token issuance, login, session
use, and antiforgery. Update every local URL consistently.

**Blocks M01-03:** **Yes.** The chosen session architecture must be executable
before authorization is layered on it.

### W2-007 — P2 — Missing request validation turns client errors into `500`

**Files:**

- `backend/src/AIDesignStudio.Api/Program.cs:10`
- `backend/src/AIDesignStudio.Api/Endpoints/AuthEndpoints.cs:400`

**Evidence:** request records carry `[Required]`, but the host never calls
`AddValidation`. Runtime probes sent `{}` to register, login, forgot-password,
and reset-password; all four returned `500` because null values reached
`UserManager.FindByEmailAsync`. An invalid email does reach Identity and returns
`400`, demonstrating the inconsistent boundary.

[ASP.NET Core's Minimal API validation documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/validation?view=aspnetcore-10.0)
states that without `AddValidation`, invalid input reaches the handler rather
than being rejected before execution.

**Why it matters:** anonymous callers can generate server errors and noisy logs,
and clients receive no stable validation contract.

**Required correction:** enable .NET 10 Minimal API validation or add an
equivalent explicit endpoint filter, map failures to stable problem details, and
test missing, null, empty, malformed-email, and oversized inputs for every auth
request.

**Blocks M01-03:** **Yes.** Fix the shared API boundary before adding more
endpoints.

### W2-008 — P2 — Data Protection keys are persisted unencrypted

**Files:**

- `backend/src/AIDesignStudio.Infrastructure/Identity/IdentityServiceCollectionExtensions.cs:27`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261026041140_AddIdentityAndDataProtection.cs:18`

**Evidence:** configuration calls `PersistKeysToDbContext` but no
`ProtectKeysWith*` mechanism. The integration run emitted:

```text
No XML encryptor configured. Key ... may be persisted to storage in unencrypted form.
```

The migration stores the key-ring XML as plain text. [Microsoft documents](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings)
that choosing an explicit persistence location disables automatic encryption at
rest and recommends explicitly configuring a production key-encryption
mechanism.

**Why it matters:** a read of this table exposes the master material protecting
authentication cookies and reset tokens. Persistence solves scale-out but not
key confidentiality.

**Required correction:** define the production protector together with D-5
(certificate, KMS/Key Vault, or an approved equivalent), restrict database access,
and make Production fail closed if protection is absent. Document local-only
behavior separately.

**Blocks M01-03:** **No**, provided it becomes an explicit pre-deployment gate;
it does block any claim that production key management is complete.

### W2-009 — P2 — Swagger bypasses the authenticated-by-default policy

**Files:**

- `backend/src/AIDesignStudio.Api/Program.cs:80`
- `backend/tests/AIDesignStudio.IntegrationTests/ApiEndpointTests.cs:208`
- `IMPLEMENTATION_PLAN.md:174`

**Evidence:** Swagger is middleware registered before authentication and
authorization, so the fallback endpoint policy does not protect it. An anonymous
Development-host probe returned:

```text
/swagger/v1/swagger.json -> 200 application/json
/swagger/index.html      -> 200 text/html
/api/v1/providers        -> 401 application/problem+json
```

The existing Swagger test uses a client that signs in during test initialization,
so it cannot detect anonymous access.

**Why it matters:** the literal acceptance criterion says all non-health
endpoints require authentication, and the API contract can expose future routes
and schemas to an unauthenticated caller.

**Required correction:** protect Swagger in Development or explicitly approve and
document a narrow development-only exception. Add an anonymous test either way;
do not rely on a globally pre-authenticated fixture for authorization assertions.

**Blocks M01-03:** **Yes**, until the acceptance criterion and implementation
agree.

### W2-010 — P2 — Current-state documentation still contains superseded decisions

**Files:**

- `IMPLEMENTATION_PLAN.md:14`
- `IMPLEMENTATION_PLAN.md:172`
- `IMPLEMENTATION_PLAN.md:182`
- `IMPLEMENTATION_PLAN.md:193`
- `IMPLEMENTATION_PLAN.md:473`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:17`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:83`
- `docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md:91`

**Evidence:** the plan and status still say only Wave 1 is authorized; the plan
reopens D-7 after recording it resolved; M01-02 still depends on A-01 and calls
the role decision open; M01-03 still requires four roles after D-3 selected two;
and the milestone table says M01-02 is stopped at open D-7. ADR-0008 says "only
five" endpoints opt out but then enumerates three health endpoints plus five auth
endpoints, which is eight and matches the eight `AllowAnonymous` call sites.

**Why it matters:** M01-03 would be implemented against contradictory role and
dependency instructions.

**Required correction:** perform a claim-based repository sweep, not a search for
one old phrase. Keep one authoritative current-state task table, remove the
duplicate open D-7 section, change M01-03 to Member/Approver, and correct the
anonymous-endpoint count and wording.

**Blocks M01-03:** **Yes.** Its role model is currently contradictory in the
implementation plan.

### W2-011 — P3 — Line-ending-only project-file churn fails `git diff --check`

**Files:**

- `backend/src/AIDesignStudio.Api/AIDesignStudio.Api.csproj:1`
- `backend/src/AIDesignStudio.Worker/AIDesignStudio.Worker.csproj:1`

**Evidence:** each file is shown as every line removed and re-added, but
`git diff --ignore-space-at-eol` produces no semantic change. `git diff --check`
reports trailing whitespace for both because of the CRLF conversion.

**Why it matters:** review noise hides real project-file changes and leaves a
standard Git hygiene check failing.

**Required correction:** restore the original line endings for both files and
keep only semantic project changes. Add or honor a repository `.gitattributes`
policy if cross-platform agents will continue editing these files.

**Blocks M01-03:** **No.**

## 4. Architecture assessment

The domain/application/infrastructure/presentation split is generally good.
`ICurrentUser` and `IPasswordResetNotifier` are plain application contracts;
Identity/EF storage remains in Infrastructure; cookie, antiforgery, and
`HttpContext` remain in the host. All 16 architecture tests pass.

The main architectural mismatch is operational: ADR-0008 records a same-origin
BFF, but the configured browser path is a direct cross-origin SPA-to-API request.
That is not a naming issue; it changes cookie transport, CORS, CSRF, and local TLS
behavior. W2-006 must settle the boundary before later UI and authorization work
depend on it.

One `AppDbContext` with an `auth` schema is consistent with the modular-monolith
decision. The migration and the narrowed UUID convention are correct.

## 5. Security assessment

Positive controls verified:

- Secure, HttpOnly, `SameSite=Strict`, host-only session-cookie attributes;
- explicit antiforgery filtering on each current state-changing auth endpoint;
- authenticated-by-default mapped endpoints;
- generic reset failure responses;
- password hashing through ASP.NET Core Identity;
- persisted lockout state; and
- no provider spend or Stitch call.

Actual vulnerabilities are W2-001, W2-002, W2-004, and W2-005. W2-003 and
W2-006 are production/runtime blockers. W2-008 is a deployment security risk,
not evidence that a deployed system has already been compromised. MFA recovery
codes remain a declared future risk rather than a finding against the current
acceptance criteria.

## 6. Test and command results

| Command or probe                                                                     | Outcome                                                                                                                              |
| :----------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet build`                                                                       | **PASS** — 0 warnings, 0 errors                                                                                                      |
| `dotnet test --no-build`                                                             | **PASS**, but the solution runner did not print the integration summary in this terminal; each executable was then measured directly |
| Unit test executable with TRX                                                        | **PASS** — 185 passed, 0 failed                                                                                                      |
| Architecture test executable with TRX                                                | **PASS** — 16 passed, 0 failed                                                                                                       |
| Integration test executable, verbose + TRX                                           | **PASS** — 85 passed, 0 failed, 0 skipped                                                                                            |
| `dotnet format --verify-no-changes`                                                  | **PASS** — exit 0                                                                                                                    |
| `dotnet-ef migrations has-pending-model-changes --project ... --startup-project ...` | **PASS** — no model changes; warning that CLI 10.0.12 is older than runtime 10.0.13                                                  |
| Empty-database `dotnet-ef database update` using dedicated review DB                 | **PASS** — all three migrations applied; review DB removed afterward                                                                 |
| `npm test` plus explicit verbose workspace runs                                      | **PASS** — 14 frontend + 164 contracts                                                                                               |
| `npm run typecheck` and explicit contracts typecheck                                 | **PASS**                                                                                                                             |
| `npm run lint`                                                                       | **PASS**                                                                                                                             |
| `npm run format:check`                                                               | **PASS**                                                                                                                             |
| `npm run build --workspace frontend`                                                 | **PASS** — exit 0                                                                                                                    |
| `npm run audit:prod`                                                                 | **PASS** — 0 vulnerabilities                                                                                                         |
| `dotnet list package --vulnerable --include-transitive`                              | **PASS** — exit 0, no vulnerable package entries                                                                                     |
| `git diff --check`                                                                   | **FAIL** — CRLF/trailing-whitespace churn in two project files; see W2-011                                                           |

**Reproduced automated baseline:** **464 passed, 0 failed, 0 skipped** — 185
unit, 16 architecture, 85 integration, 14 frontend, 164 contracts.

Additional runtime probes were executed against an HTTPS Production host and a
fresh database, then against the documented HTTP Development path. They produced
the account-enumeration, recovery, missing-validation, HTTP-antiforgery, and
anonymous-Swagger evidence recorded above. No runtime probe called Stitch or
enabled billable operations.

## 7. Required corrections before re-review

1. Wire security-stamp validation and prove cross-session revocation.
2. Remove all three account-enumeration channels and add Production-mode paired
   tests.
3. Resolve production password recovery consistently with ADR-0008.
4. Redesign MFA enroll/disable/rotation so an enabled secret is never disclosed
   and invalid management attempts are bounded.
5. Decide and implement identity proofing before assigning organizations or
   Approver roles.
6. Make the documented browser boundary executable over HTTPS and align the BFF
   claim, CORS, cookie credentials, docs, and browser tests.
7. Add request validation and negative tests for all auth DTOs.
8. Record and gate Data Protection key encryption before deployment.
9. Protect or explicitly exempt Swagger, with an anonymous test.
10. Reconcile the role/dependency/current-wave documentation and remove
    line-ending-only churn.

## 8. Decision and next milestone gate

**CHANGES REQUIRED. M01-02 is not accepted. M01-03 must remain not started.**

The 464-test baseline is accepted as measured evidence, but it is not sufficient
evidence of a production-valid authentication boundary. Re-review should include
new regression tests for every P1 finding, the same full quality gates, a clean
migration, and browser/runtime probes using the repository's documented local
configuration.
