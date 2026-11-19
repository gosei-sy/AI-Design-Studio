# Response to the M01 Wave 2 Review

**Decision under response:** CHANGES REQUIRED — 6 P1, 4 P2, 1 P3
**Response date:** 2026-10-21 · **Author:** Lead Developer
**Review:** [M01_WAVE2_CODEX_REVIEW.md](M01_WAVE2_CODEX_REVIEW.md)

**M01-02 is resubmitted for review, not marked complete. M01-03 has not begun.**

---

## Summary

All eleven findings accepted. Every one was reproducible, and three were
vulnerabilities in anonymous public endpoints.

The pattern across the P1s is worth naming, because it is not carelessness about
security — it is **claiming a control without testing the control**:

| Finding | What was claimed                          | What was true                                                        |
| :------ | :---------------------------------------- | :------------------------------------------------------------------- |
| W2-001  | Sessions revoked on password change       | The validator was never attached; nothing was revoked                |
| W2-002  | "No account enumeration"                  | Three oracles, each in a path the tests did not probe                |
| W2-003  | Recovery implemented                      | Production had no recovery at all, and failed only for real accounts |
| W2-004  | Disable requires a current code           | Enrol handed out the secret needed to make one                       |
| W2-005  | Approvals attributable to a real identity | The subject was a self-asserted, unconfirmed email                   |
| W2-006  | Same-origin BFF session                   | Documented path was HTTP, so every auth call returned 500            |

In each case I wrote the mechanism, wrote a test that exercised the happy path,
and treated the happy path as proof of the property. W2-001 is the clearest: the
test proved the old password stopped working and the caller's own session
survived — neither of which requires revocation to exist. **A test that passes
on the broken implementation is not evidence.**

| Finding                 | Severity | Status                                    |
| :---------------------- | :------- | :---------------------------------------- |
| W2-001 revocation       | P1       | ✅ Fixed, + 2 tests                       |
| W2-002 enumeration      | P1       | ✅ Fixed, + 3 tests                       |
| W2-003 recovery         | P1       | ✅ Fail-closed + scope amended, + 2 tests |
| W2-004 MFA secret       | P1       | ✅ Fixed, + 3 tests                       |
| W2-005 audit identity   | P1       | ✅ Fixed, S-21 → partial, + 1 test        |
| W2-006 browser boundary | P1       | ✅ Fixed, ADR amended                     |
| W2-007 validation       | P2       | ✅ Fixed, + 4 tests                       |
| W2-008 key encryption   | P2       | ✅ Fail-closed, D-5 gate                  |
| W2-009 Swagger          | P2       | ✅ Fixed, + 3 tests                       |
| W2-010 documentation    | P2       | ✅ Reconciled                             |
| W2-011 line endings     | P3       | ✅ Restored, `.gitattributes` added       |

---

## W2-001 — Session revocation · **FIXED**

`SecurityStampValidatorOptions.ValidationInterval` was configured; the validator
was never invoked, because nothing set `OnValidatePrincipal`.

Registration now goes through **`AddIdentityCookies()`** with the validator
attached explicitly. The helper matters for a second reason I found while fixing
it: Identity uses **four** cookie schemes, and rejecting a principal signs out
across all of them. With only the application scheme registered, the validator
threw `InvalidOperationException` the first time it tried to revoke anything — so
the first fix produced a 500 instead of a 401, and the test caught that too.

**Tests:** `Changing_the_password_revokes_other_sessions` and
`Resetting_the_password_revokes_existing_sessions`. Both sign in **two** clients,
act through one, and require the other to receive 401 while the acting client
stays authenticated. The validation interval is 0 in those hosts so revocation is
observable without waiting fifteen minutes.

## W2-002 — Three enumeration oracles · **FIXED**

| Oracle                                          | Fix                                                                                                                              |
| :---------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------- |
| Weak password + existing → 202, + unknown → 400 | Password policy is evaluated **before** the account lookup, against a detached user, so the verdict depends only on the password |
| Fifth wrong attempt → 423 vs 401                | Lockout is **no longer reported distinctly**. Both are 401                                                                       |
| Forgot-password → 500 for real accounts         | Transport availability is a **startup** decision, so the handler cannot fail based on the address                                |

The lockout change has a real cost and I am not going to pretend otherwise: a
locked-out legitimate user now sees "invalid credentials" with no explanation of
why a password they know stopped working. The honest fix is to tell them by
email, which needs **D-8**. Until then, silence is the safer of two bad options,
because the alternative hands the oracle to everyone.
`Lockout_is_not_distinguishable_from_an_unknown_account` also asserts against the
store that the lockout **still happened** — suppressing the response must not
suppress the protection.

**Tests:** three, all against a non-Development host, each comparing full
response bodies with only the per-request `traceId` excluded.

## W2-003 — Password recovery · **FAIL-CLOSED, SCOPE AMENDED**

The notifier threw outside Development, which looked fail-closed and was worse
than useless: it threw **only for addresses that exist**.

Availability is now settled at startup. **Production refuses to start** without a
transport; a transport that is configured but unimplemented also refuses to
start. Development warns. The request path is a deliberate sink, so every address
gets the same 202.

**ADR-0008 amended (A1):** recovery _token generation_ is implemented; recovery
_delivery_ is **blocked on D-8**, and **M01-02 is incomplete on that
obligation**. The ADR no longer claims recovery is done. Token logging in
Development is opt-in and off by default, because a reset token is a credential
and shared log storage must not receive one.

**Tests:** `Production_refuses_to_start_without_a_password_reset_transport`,
`A_configured_but_unimplemented_transport_refuses_to_start`.

## W2-004 — MFA secret re-disclosure · **FIXED**

The attack the review described worked: session → enrol → read live secret →
compute code → disable MFA. My own comment claimed the disable endpoint prevented
exactly that.

- **Enrol refuses with 409 when MFA is already enabled.** The secret is never
  re-disclosed.
- **Enrol always mints a fresh secret**, so a key handed out earlier cannot be
  enabled later by someone who kept it.
- **Disable requires the current password _and_ a current code.** Removing a
  factor should be at least as hard as using it.
- **Invalid factor-management codes call `AccessFailedAsync`**, so the management
  endpoints are bounded by lockout. Only the login branch did before — which
  contradicted the ADR's own claim.

**Tests:** `An_enabled_authenticator_secret_is_never_re_disclosed`,
`Disabling_two_factor_requires_the_current_password`,
`Repeated_invalid_factor_codes_are_bounded_by_lockout`.

## W2-005 — Audit identity · **FIXED; S-21 → PARTIAL**

`RequireAuditIdentity()` returned the email address. Registration is public and
confirmation is disabled, so an address is self-asserted — anyone could register
someone else's and be recorded as that person in every future approval.

It now returns the **immutable user id**, and throws if no identifier claim
exists rather than falling back to email. Email remains available as display
metadata.

That makes attribution **stable**, not **proved**. Account proofing is raised as
**D-9** and must be resolved before M01-03 attaches roles and organizations.
**S-21 is now recorded as partial, not met.**

**Test:** `The_audit_identity_is_the_immutable_user_id_not_the_email`.

## W2-006 — Browser boundary · **FIXED, ADR AMENDED**

The cookies are `Secure` and every documented URL was HTTP, so the whole auth
surface returned 500 on the setup path in the README. The tests hid it by forcing
`https://localhost`.

- The **`https` profile is now the default** (`https://localhost:7080`).
- `.env.example`, README, API_CONTRACTS and the frontend default all use HTTPS.
- The fetch helper sends **`credentials: "include"`**.
- CORS allows the HTTPS studio origin with `AllowCredentials()`.

**ADR amended (A1):** the boundary is **cross-origin and same-site**, not
same-origin BFF. Different port (and in deployment, different subdomain) is a
different _origin_ but the same _site_, so `SameSite=Strict` still permits the
cookie while a genuinely cross-site caller is refused. A true same-origin reverse
proxy remains the better end-state and is **not** implemented — stated rather
than implied.

**Not done:** a Playwright browser test of the auth flow. The review asked for
one. It needs both servers over HTTPS with a self-signed certificate, and
M01-11's E2E acceptance is itself still pending its CI run. I did not want to
claim browser verification I had not performed, so this is **an open gap**, listed
in §"Not done" below rather than buried.

## W2-007 — Request validation · **FIXED**

`{}` returned 500 from four endpoints. Two causes, and the second is the
interesting one:

1. `AddValidation()` was never called. It is now.
2. **`[Required]` on a positional record parameter targets the _parameter_, not
   the property.** `Validator` reads property attributes, so the annotations were
   invisible — which is why `AddValidation()` alone still left three endpoints
   returning 500 after step 1. All thirteen attributes are now
   `[property: Required]`.

A `RequestValidationEndpointFilter` on the auth group makes the boundary explicit
and uniform, which the review named as an acceptable equivalent.

**Tests:** `An_empty_request_body_is_a_client_error_not_a_server_error` across
all four endpoints.

## W2-008 — Data Protection keys · **FAIL-CLOSED**

`PersistKeysToDbContext` disables the automatic encryption at rest that the
default location provides, and no protector was configured — the key ring
protecting every session cookie sat in PostgreSQL as readable XML.

No protector is hard-coded, because the right one depends on the deployment
target (**D-5**). Instead the host **refuses to start outside Development**
until one exists, and warns in Development. Non-Development test hosts supply a
generated certificate, so they exercise the production shape rather than
bypassing the check.

Production key management is **not** claimed complete; it is a pre-deployment
gate tied to D-5.

## W2-009 — Swagger · **FIXED**

Swagger is middleware registered before authentication, so the endpoint fallback
policy never applied. Anonymous callers got the full contract.

It now runs inside a `UseWhen` branch that authenticates and requires an
authenticated user, Development-only. **Tests:**
`Swagger_is_not_available_anonymously` (two paths, on a **fresh anonymous
client** — the previous test used a pre-authenticated fixture and could not have
detected this) and `Swagger_is_available_to_an_authenticated_caller_in_development`.

## W2-010 — Documentation · **RECONCILED**

| Was                                               | Now                                                       |
| :------------------------------------------------ | :-------------------------------------------------------- |
| Plan and status: "Wave 1 is the authorized scope" | Wave 2, M01-02 only                                       |
| M01-02 "Depends on: A-01 decision"                | Depends on D-7 (resolved); role model is M01-03's         |
| M01-02 "Open decision: the four-role model"       | D-3 resolved; D-8 and D-9 listed as the open ones         |
| M01-03 "The four roles from A-01 are enforced"    | **Member and Approver** per D-3                           |
| A duplicate "D-7 … Status: Open" section          | Removed; D-7 appears once, resolved                       |
| Risk row "Role model (A-01) wrong"                | Closed by D-3                                             |
| ADR-0008 "only five" then listing eight           | **Eight**, matching the eight `AllowAnonymous` call sites |

## W2-011 — Line endings · **FIXED**

Both `.csproj` files restored to their original line endings; `git diff
--ignore-space-at-eol` had already confirmed zero semantic change. Only the two
real package references remain in the diff. Added **`.gitattributes`** with
`* text=auto eol=lf` so a cross-platform agent cannot reintroduce it.
`git diff --check` exits 0.

---

## New decisions raised

| #       | Decision                                                                                               | Owner                              | Blocks                       |
| :------ | :----------------------------------------------------------------------------------------------------- | :--------------------------------- | :--------------------------- |
| **D-8** | A password-reset delivery transport (authorized mail service)                                          | Product Owner                      | Recovery; Production startup |
| **D-9** | Account proofing — confirmed email, or invitation/admin provisioning instead of open self-registration | Product Owner + Solution Architect | **M01-03** attaching roles   |

---

## Not done — stated, not hidden

| Item                                                  | Why                                                                                                                                                                             |
| :---------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Playwright browser test of the auth flow** (W2-006) | Needs both servers over HTTPS with a self-signed cert; M01-11's own CI acceptance is still pending. I did not perform browser verification, so I do not claim it. **Open gap.** |
| Production mail transport                             | **D-8**. Fail-closed meanwhile                                                                                                                                                  |
| Production key protector                              | **D-5**. Fail-closed meanwhile                                                                                                                                                  |
| Account proofing                                      | **D-9**. S-21 recorded partial                                                                                                                                                  |
| MFA recovery codes                                    | Still absent; a lost authenticator still needs an operator. Raised in the Wave 2 report and unresolved                                                                          |
| Role enforcement, organization scoping                | **M01-03**, not started                                                                                                                                                         |

---

## Verification — measured, this run

```
dotnet build                        0 Warning(s), 0 Error(s)
dotnet test                         total: 304, failed: 0, succeeded: 304, skipped: 0
dotnet format --verify-no-changes   exit 0
npm test                            frontend 14 passed · contracts 164 passed
npm run typecheck                   clean
npm run lint                         clean
npm run format:check                All matched files use Prettier code style!
npm run build --workspace frontend  ✓ Compiled successfully in 9.7s
migrations has-pending-model-changes  No changes have been made to the model
git diff --check                    exit 0
npm run audit:prod                  found 0 vulnerabilities
```

**Total: 482 passing, 0 failed, 0 skipped.**

| Suite                         | Wave 2 submission |     Now |   Added |
| :---------------------------- | ----------------: | ------: | ------: |
| Unit                          |               185 | **185** |       — |
| Architecture                  |                16 |  **16** |       — |
| Integration (real PostgreSQL) |                85 | **103** | **+18** |
| Frontend                      |                14 |  **14** |       — |
| Shared contracts              |               164 | **164** |       — |
| **Total**                     |           **464** | **482** | **+18** |

Integration tests **passed, not skipped**, against PostgreSQL 15.19.

---

## Confirmations

- **No Stitch or provider call.** `BillableOperationsEnabled` still `false` in
  both hosts, still asserted by a test.
- **No spend.** No Docker, no OS packages, no external service added.
- **Nothing committed, pushed, deployed or opened as a PR.** The repository's one
  commit is the Product Owner's and predates this work.
- **M01-03 not started.** No role seeded, none enforced, no organization scoping.
  The guard test still asserts it.
- **No other M01 task started.**
- **All existing gates preserved.** 16 architecture tests pass unchanged; the
  seven M00 ADRs are unmodified; ADR-0008's decision is unchanged and its
  corrections are an appended amendment.
- **No guard, test or acceptance criterion weakened.** Two test expectations
  changed because the behaviour they asserted was itself the defect: lockout no
  longer returns 423, and the audit identity is no longer the email.
- **A-08 and A-09 remain open.**

---

**M01 Wave 2 corrections applied. M01-02 resubmitted for independent review and
not accepted; M01-03 remains blocked on that acceptance and on D-9; recovery
remains blocked on D-8; production key management remains blocked on D-5.**
