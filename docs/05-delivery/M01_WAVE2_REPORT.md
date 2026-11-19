# M01 Wave 2 Report — M01-02 Authentication

**Milestone:** M01 — Authentication, core CRUD, live generation
**Authorized:** 2026-10-21, Product Owner · **Scope:** **M01-02 only**
**Report date:** 2026-10-21 · **Author:** Lead Developer

**M01-02 is submitted for independent review, not marked complete.** M01-03 has
not begun and must not until this is accepted.

> **Update 2026-11-02 — ADR-0008 A2 accepted.** The Product Owner approved
> Proposal 1 (the **same-origin BFF** is the accepted browser boundary) and
> Proposal 2 **Option 2** (password-reset delivery is a **fail-closed
> pre-production gate**; **no Production deployment may proceed until D-8 is
> resolved**). D-8 therefore no longer gates M01-02 acceptance, though recovery
> is still undelivered and M01-02 is still incomplete on that obligation. The
> approval **does not resolve D-9 and does not authorize M01-03.** The body of
> this report is the 2026-10-21 submission and is left as written; current status
> is [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) §7.

---

## 1. Decisions recorded

| Decision | Resolution                                                                                                                                                                                                                                                                 | Recorded in                                                                                                                                                      |
| :------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **D-7**  | **Option A** — ASP.NET Core Identity on PostgreSQL, HttpOnly cookie sessions. (The boundary wording here is **superseded**; see ADR-0008 A1/A2.) Password lifecycle, recovery, lockout, MFA policy, antiforgery and Data Protection key persistence are explicit M01 scope | [ADR-0008](../02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md), `IMPLEMENTATION_PLAN.md`, `IMPLEMENTATION_STATUS.md` |
| **D-3**  | **Two roles** — Member and Approver; a user may hold both; only Approver may approve, reject or waive                                                                                                                                                                      | `OPEN_ASSUMPTIONS.md` A-01 resolved, plan, status, ADR-0008 scope boundary                                                                                       |

**ADR-0008 is new and Accepted.** It records that Option A was chosen _over_ the
independent recommendation of Option B, and enumerates what that choice obliges
us to own. The seven M00 ADRs are unmodified.

**A-01 is resolved**, with the reasoning kept: the four-role split was never
validated, so it was not built.

---

## 2. What was implemented

### Layering — the boundary holds

The architecture tests forbid `Microsoft.AspNetCore` in Domain and Application.
All 16 still pass, because identity crosses the boundary as a plain contract:

```
Application      ICurrentUser, IPasswordResetNotifier        (no web, no EF)
Infrastructure   ApplicationUser/Role, EF stores, password + lockout policy,
                 Data Protection key persistence
Api (host)       cookie scheme, SignInManager, token providers,
                 HttpContextCurrentUser, antiforgery filter, security headers
```

The split was forced by a real constraint rather than chosen for neatness:
Infrastructure is a class library with no ASP.NET Core framework reference, so
`AddCookie`, `SignInManager` and `IHttpContextAccessor` cannot live there. That
turned out to be the correct boundary anyway — the Identity _store_ is
persistence, the session is HTTP, and the worker has no session.

### Files added

| File                                                                    | Purpose                                                                                                                                                                      |
| :---------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Application/Identity/ICurrentUser.cs`                                  | Who is calling. No roles, no organization — both are M01-03                                                                                                                  |
| `Application/Identity/IPasswordResetNotifier.cs`                        | Reset-token delivery port; the token never enters a response                                                                                                                 |
| `Infrastructure/Identity/ApplicationUser.cs`                            | Account; UUID v7 assigned in the constructor                                                                                                                                 |
| `Infrastructure/Identity/ApplicationRole.cs`                            | Role table only — **nothing seeded, nothing enforced**                                                                                                                       |
| `Infrastructure/Identity/IdentityServiceCollectionExtensions.cs`        | Stores, password policy, lockout, DP key persistence                                                                                                                         |
| `Infrastructure/Identity/LoggingPasswordResetNotifier.cs`               | Development notifier; a deliberate sink elsewhere. **Superseded by the re-review:** it no longer throws at request time — availability is a startup gate (W2-003, W2-RR-001) |
| `Api/Identity/SessionAuthentication.cs`                                 | Cookie scheme, fallback policy, antiforgery, `SignInManager`                                                                                                                 |
| `Api/Identity/HttpContextCurrentUser.cs`                                | Reads the principal; throws rather than inventing an audit identity                                                                                                          |
| `Api/Identity/AntiforgeryEndpointFilter.cs`                             | Validates state-changing requests                                                                                                                                            |
| `Api/Identity/SecurityHeaders.cs`                                       | CSP, HSTS, X-Content-Type-Options, X-Frame-Options, Referrer-Policy                                                                                                          |
| `Api/Endpoints/AuthEndpoints.cs`                                        | 11 endpoints                                                                                                                                                                 |
| `Persistence/Migrations/20261026041140_AddIdentityAndDataProtection.cs` | 8 tables in the `auth` schema                                                                                                                                                |

### Endpoints

| Method | Path                              | Auth          |
| :----- | :-------------------------------- | :------------ |
| GET    | `/api/v1/auth/antiforgery-token`  | Anonymous     |
| POST   | `/api/v1/auth/register`           | Anonymous     |
| POST   | `/api/v1/auth/login`              | Anonymous     |
| POST   | `/api/v1/auth/forgot-password`    | Anonymous     |
| POST   | `/api/v1/auth/reset-password`     | Anonymous     |
| POST   | `/api/v1/auth/logout`             | Authenticated |
| GET    | `/api/v1/auth/me`                 | Authenticated |
| POST   | `/api/v1/auth/change-password`    | Authenticated |
| POST   | `/api/v1/auth/two-factor/enroll`  | Authenticated |
| POST   | `/api/v1/auth/two-factor/enable`  | Authenticated |
| POST   | `/api/v1/auth/two-factor/disable` | Authenticated |

Every state-changing endpoint carries the antiforgery filter.

### Closed by default

The host sets a fallback authorization policy requiring an authenticated user, so
an endpoint is protected unless it opts out. Provider diagnostics became
authenticated as a result — which is correct under S-19, and is why the existing
`ApiEndpointTests` now sign in rather than being exempted.

A fallback policy rather than per-endpoint attributes because the default must be
closed: an endpoint added in M01-04 is protected because nobody did anything.

### Three implementation notes worth a reviewer's attention

**The antiforgery filter is not redundant.** ASP.NET Core's antiforgery
middleware validates minimal-API endpoints that bind a **form**. A JSON endpoint
binds from the body and is _not_ validated — so `UseAntiforgery()` alone leaves a
cookie-session JSON API with no CSRF protection. `SameSite=Strict` is the first
layer; the explicit filter is the second.

**Data Protection keys go to the database.** Without that, keys sit in the
container filesystem: every deploy signs everyone out, and a second instance
cannot read the first's cookies. It works perfectly in development and fails on
the second production instance.

**No account enumeration.** Registering an existing address returns the same 202
as a new one; an unknown address and a wrong password return byte-identical
problem details; a reset request for a nonexistent address returns the same 202
as a real one. Login also hashes a throwaway password when the account is missing,
so response _timing_ does not leak existence either.

---

## 3. One pre-existing defect found and fixed

**The API-host tests were running against the developer's database.**

`StudioApiFactory`'s predecessor overrode `ConnectionStrings:Default` through
`ConfigureAppConfiguration`'s in-memory source. That source did **not** win over
the host's own `appsettings.json`, so the test host connected to `aidesignstudio`
— the dev database — instead of the provisioned `aids_test_*` one.

Nothing caught it, because the readiness probe only needs _a_ reachable database
and the dev one is reachable and migrated. Identity exposed it immediately: the
`auth` tables existed in the test database and not in the dev one, so 26 tests
failed with `relation "auth.roles" does not exist` while the migration log
plainly showed the migration being applied.

Fixed with `builder.UseSetting("ConnectionStrings:Default", …)`, which writes into
host configuration and takes precedence. Guarded permanently by
`The_test_host_uses_the_provisioned_test_database`, which asserts the host's
database name matches the fixture's and begins `aids_test_`.

Worth stating plainly: M00's claim that "every integration run provisions a fresh
database and migrates it" was true of the fixture-based tests and **false of the
API-host tests** for the whole of M00. No result is retracted — the assertions
they made were about health, provider capabilities and credential leakage, none
of which depends on which database was behind them — but the isolation claim was
wrong and is now true.

### A correctness fix carried by the same change

`AppDbContext.ConfigureDomainGeneratedKeys` set `ValueGenerated.Never` on the
`Id` of **every** entity type with one. Harmless while the model held only domain
aggregates with constructor-assigned UUID v7 keys; a defect the moment Identity
arrived, because `IdentityUserClaim.Id` and `DataProtectionKey.Id` are `int`
identity columns — declaring them never-store-generated makes every insert write
`0` and collide on the second row.

Now restricted to `Entity` subclasses, which is also what the convention was
always for (DATA_MODEL §9). Verified in the migration: both int keys retain
`IdentityByDefaultColumn`.

---

## 4. Two defects the tests found in my own work

Both were found by tests failing, not by review.

**The reset-token decode corrupted valid tokens.** `reset-password` base64url-
decoded the incoming token before handing it to `ResetPasswordAsync`, copying a
pattern from samples that URL-encode tokens for emails.
`GeneratePasswordResetTokenAsync` already returns a usable string, so the decode
_succeeded_ and produced garbage — every valid reset was rejected. The token is
now passed through untouched.

**The TOTP test could never have passed.** It generated codes with
`UserManager.GenerateTwoFactorTokenAsync(user, AuthenticatorTokenProvider)`.
That returns an **empty string** by design: generating the code is the
authenticator app's job, and the server only verifies. The test now computes a
real RFC 6238 code from the shared key (`Totp.cs`), which is the phone's side of
the protocol.

One test expectation was also wrong rather than the code: lockout trips **on**
the fifth failure, not after it, so `MaxFailedAccessAttempts = 5` means attempts
1–4 are credential errors and the fifth is already `423`. The implementation was
right.

---

## 5. Test evidence

### Added: 28 integration tests

Integration rather than unit, deliberately. Every property worth asserting here
is a property of the composition — whether the fallback policy actually closes an
endpoint, whether the cookie is really `HttpOnly`, whether lockout survives a
round trip to PostgreSQL. A mocked `UserManager` would prove none of it.

| Area                         | Tests                                                                                                                                                                   |
| :--------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| S-19 authentication required | 401 on 3 protected paths (theory); not redirected to a login page; health stays anonymous on 3 paths (theory); authenticated caller reaches a protected endpoint        |
| S-21 attributable identity   | `/me` returns the identity the domain will record                                                                                                                       |
| Session cookie               | HttpOnly + Secure + SameSite=Strict + path=/ + no Domain; no token in any response body; logout revokes                                                                 |
| Antiforgery                  | State-changing request without a token → 400 `auth.antiforgery_failed`; safe request needs none                                                                         |
| No enumeration               | Duplicate registration indistinguishable; unknown address and wrong password byte-identical; reset request identical and token-free                                     |
| Password lifecycle           | Weak password refused; change invalidates the old one and keeps the session; full reset via the notifier token; invalid token refused                                   |
| Lockout                      | 4 credential errors, 5th trips the lock, correct password then refused                                                                                                  |
| MFA                          | Enrol → wrong code refused → correct code enables → password alone returns `requiresTwoFactor` and grants nothing → code completes login; disable requires a valid code |
| S-25 headers                 | CSP, HSTS, X-Content-Type-Options, X-Frame-Options, Referrer-Policy                                                                                                     |
| Standing guards              | Billable guard still `false`; **no role seeded**; test host uses the test database                                                                                      |

Two of those are guards against this task overreaching:
`No_role_is_seeded_because_authorization_is_m01_03` fails if role seeding appears
early, and `The_billable_guard_is_disabled_in_the_test_host` fails if
authentication work ever enables provider spending.

### Measured, this run

```
$ dotnet build
    0 Warning(s)
    0 Error(s)

$ dotnet test
  total: 286
  failed: 0
  succeeded: 286
  skipped: 0

$ dotnet format --verify-no-changes
(exit 0)

$ npm test
  frontend            Tests  14 passed (14)
  packages/contracts  Tests  164 passed (164)

$ npm run typecheck      clean
$ npm run lint           clean
$ npm run format:check   All matched files use Prettier code style!

$ npm run build --workspace frontend
✓ Compiled successfully in 9.4s

$ dotnet-ef migrations has-pending-model-changes
No changes have been made to the model since the last migration.

$ npm run audit:prod
found 0 vulnerabilities

$ dotnet list package --vulnerable --include-transitive
no vulnerable packages
```

**Total: 464 passing, 0 failing, 0 skipped** — **superseded**; this was the Wave 2 submission. Current measured totals are in the latest review response.

| Suite                         | Before (M01 Wave 1) |     Now |   Added |
| :---------------------------- | ------------------: | ------: | ------: |
| Unit                          |                 185 | **185** |       — |
| Architecture                  |                  16 |  **16** |       — |
| Integration (real PostgreSQL) |                  57 |  **85** | **+28** |
| Frontend                      |                  14 |  **14** |       — |
| Shared contracts              |                 164 | **164** |       — |
| **Total**                     |             **436** | **464** | **+28** |

Integration tests **passed, not skipped**, against PostgreSQL 15.19.

**One honest note on the run.** The first attempt reported `total: 84, skipped:
84` because PostgreSQL had stopped between waves. That is reported rather than
quietly re-run: an all-skipped suite is not a passing suite, and the counts above
are from a run with the server up.

### New packages

| Package                                                           | Why                |
| :---------------------------------------------------------------- | :----------------- |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.13       | The Identity store |
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.13 | Key persistence    |

Both are first-party, both match the pinned EF Core version, and neither is new
infrastructure or an external service — they persist to the PostgreSQL database
that already exists.

---

## 6. Deliberately not done

| Item                             | Why                                                                                                                                                                                                                                                                                                                                                                                        |
| :------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Role seeding and enforcement** | D-3's Member/Approver model is **authorization = M01-03**, which must not start until M01-02 is accepted. The roles table exists because Identity's store requires it; nothing is seeded and a test asserts that                                                                                                                                                                           |
| **Organization scoping (S-20)**  | M01-03. `ApplicationUser` has no `OrganizationId` — adding it would ship an unreviewed authorization decision inside an authentication task                                                                                                                                                                                                                                                |
| **Email transport**              | **Superseded — see the correction at the top of this report.** As submitted this row said the dev notifier "throws outside Development"; it does not. Availability is a **startup** gate (W2-003, W2-RR-001): Production refuses to start without a transport, and `DevelopmentLog` is rejected outside Development. `RequireConfirmedEmail` is `false` because no sender exists (**D-8**) |
| **Frontend login UI**            | M01-10, gated                                                                                                                                                                                                                                                                                                                                                                              |
| **Recovery codes for MFA**       | Not in M01-02's acceptance criteria. A user who loses their authenticator currently needs an operator; worth raising for M01-03 scope                                                                                                                                                                                                                                                      |

That last one is a gap I am flagging rather than hiding: TOTP enrolment without
recovery codes means a lost phone is a lost account. It does not block M01-02's
criteria but should not reach real users unaddressed.

---

## 7. M01-02 acceptance criteria

From `IMPLEMENTATION_PLAN.md`, each with its evidence. **Assessment is the
reviewer's, not mine.**

| Criterion                                                                                     | Evidence                                                                                                                                                                                 |
| :-------------------------------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| All non-health endpoints require authentication; unauthenticated → `401` with problem details | `An_unauthenticated_request_to_a_protected_endpoint_is_refused` (3 paths), `An_unauthenticated_request_is_not_redirected_to_a_login_page`, `Health_endpoints_remain_anonymous` (3 paths) |
| A user identity is available to the domain for `ApprovedBy` / `WaivedBy`                      | `ICurrentUser.RequireAuditIdentity()`; `The_authenticated_identity_is_available_for_domain_audit_fields`                                                                                 |
| Integration tests cover authenticated and unauthenticated paths                               | 28 tests; both paths asserted throughout                                                                                                                                                 |
| Security headers (CSP, HSTS, X-Content-Type-Options) — closes S-25                            | `Security_headers_are_present_on_every_response`                                                                                                                                         |

---

## 8. Confirmations

- **No Stitch or provider call.** None made; the test host points at a
  nonexistent executable.
- **`BillableOperationsEnabled` remains `false`** — both `appsettings.json`
  files unchanged, and a test asserts it in the host.
- **No Docker installed**, no OS packages, no external service.
- **Nothing committed, pushed, deployed, or opened as a PR by this work.** All
  25 changed paths are uncommitted in the working tree. The repository's single
  commit (`ef4fcb1 "First Add"`) is the Product Owner's own and predates this
  wave — earlier reports in this series said "no commits", which reflected
  `git log` on an unborn branch at the time; the accurate statement is that no
  commit was made by me.
- **M01-03 not started.** No role seeded, no role enforced, no organization
  scoping, no policy beyond "authenticated".
- **No other M01 task started.** M01-01 untouched; M01-04 … M01-12 untouched.
- **All existing gates preserved.** 16 architecture tests pass unchanged; the
  seven M00 ADRs are unmodified; ADR-0008 is additive.
- **No guard, test or acceptance criterion weakened.** The two test changes were
  a wrong expectation corrected and the pre-existing suite authenticating because
  the endpoints are now protected.
- **A-08 and A-09 remain open.**

---

**M01-02 implemented and submitted for independent review. M01-03 remains
blocked on M01-02 acceptance; M01-01 live execution remains blocked on D-1/D-2.**
