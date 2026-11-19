# ADR-0008: Authentication is local ASP.NET Core Identity with a cookie/BFF session

- **Status:** Accepted (M01) — **as amended by A1 and A2**
- **Date:** 2026-10-21 · **A2 accepted:** 2026-11-02
- **Deciders:** Product Owner, Solution Architect
- **Resolves:** D-7
- **Supersedes:** nothing

## Context

M00 shipped with no authentication at all, recorded plainly in
SECURITY_REQUIREMENTS §5: S-19, S-20 and S-21 all read "NOT IMPLEMENTED — M01".
What those requirements never said was _how_. The M01 Wave 1 audit found no
identity provider, protocol, token format or session model recorded anywhere in
the repository, and raised **D-7** to decide it.

Three options were put to the Product Owner and Solution Architect (M01 Wave 1
report §5, as corrected by W1-003):

|       | Approach                                       | Session the browser holds              | Who owns login/MFA/password policy |
| :---- | :--------------------------------------------- | :------------------------------------- | :--------------------------------- |
| **A** | Local Identity + cookie/BFF                    | Server-issued HttpOnly cookie          | This application                   |
| **B** | External OIDC confidential client + cookie/BFF | Server-issued HttpOnly cookie          | External provider                  |
| **C** | Identity API endpoints                         | Cookie **or** proprietary bearer token | This application                   |

The independent architecture recommendation was **Option B**, on the grounds that
any real buyer expects SSO and that owning password reset, lockout, recovery and
MFA policy is a liability rather than an asset.

## Decision

**Option A.** ASP.NET Core Identity backed by the existing PostgreSQL database,
with a same-origin HttpOnly cookie session — a BFF boundary.

The Product Owner selected A over the recommendation. That is recorded here as a
decision, not as an oversight: Option B requires authorizing an external identity
dependency and pulls part of D-5 (deployment target and secret store) forward
into M01, and the Product Owner chose not to take on either in this milestone.

**The consequence is explicit, and it is the reason this ADR exists rather than a
one-line note:** choosing A means the password lifecycle, recovery, lockout, MFA
policy, antiforgery protection and Data Protection key persistence all become
M01 scope and stay this application's responsibility indefinitely. None of those
is optional, and none of them would have been ours under B.

### What the browser receives

An opaque cookie it cannot read. Nothing else — no access token, no refresh
token, no JWT.

```
__Host-aids.session   HttpOnly; Secure; SameSite=Strict; Path=/
```

The `__Host-` prefix is enforced by the browser: a cookie carrying it is rejected
unless it is Secure, has path `/` and has no `Domain` attribute, so it cannot be
scoped to a parent domain or planted by a sibling subdomain.

This is the point of preferring a cookie over a bearer token. An XSS cannot
exfiltrate a session it cannot see; a token in `localStorage` is readable by any
script that gets executed.

### What this costs us, enumerated

Each of these is implemented in M01-02, not deferred:

| Obligation           | How it is met                                                                                 |
| :------------------- | :-------------------------------------------------------------------------------------------- |
| Password policy      | 12 characters, mixed case, digit, non-alphanumeric                                            |
| Lockout              | 5 failures → 15 minutes, **enabled for new users**                                            |
| Password change      | Rotates the security stamp, re-issues the caller's session                                    |
| Password recovery    | Token through `IPasswordResetNotifier`, **never** in a response                               |
| MFA policy           | TOTP enrolment, enable-on-proof, disable-on-proof; failed second factors count toward lockout |
| Antiforgery          | Explicit endpoint filter on every state-changing endpoint                                     |
| Data Protection keys | Persisted to PostgreSQL via `IDataProtectionKeyContext`                                       |

**Antiforgery needs the explicit filter.** ASP.NET Core's antiforgery middleware
validates minimal-API endpoints that bind a _form_. A JSON endpoint binds from
the body and is not validated — so a cookie-session JSON API has no CSRF
protection from middleware alone. `SameSite=Strict` is the first layer and the
filter is the second; neither alone was considered sufficient.

**Data Protection key persistence is not a nicety.** Without it, keys live in the
container filesystem: every deployment signs all users out, and a second instance
cannot read the first's cookies at all. It works perfectly in development and
fails on the second production instance, which is the worst shape a defect can
have.

### Authenticated by default

The host sets a fallback authorization policy requiring an authenticated user.
Endpoints opt **out** with `AllowAnonymous`, and **eight** do: the three health
probes, plus five auth endpoints — antiforgery-token, register, login,
forgot-password and reset-password. (The original text said "only five" and then
listed eight; corrected per W2-010. Eight matches the `AllowAnonymous` call
sites: three in `HealthEndpoints`, five in `AuthEndpoints`.)

A fallback policy rather than per-endpoint attributes, because the default has to
be closed. An endpoint added in M01-04 is protected because nobody did anything —
the inverse of the usual failure, where protection is forgotten.

### Where the layers sit

The architecture tests forbid `Microsoft.AspNetCore` in Domain and Application,
and that boundary is preserved:

```
Application      ICurrentUser, IPasswordResetNotifier      (plain contracts)
Infrastructure   ApplicationUser, EF stores, password/lockout policy,
                 Data Protection key persistence
Api (host)       cookie scheme, SignInManager, token providers,
                 HttpContextCurrentUser, antiforgery, security headers
```

The split is not cosmetic. Infrastructure is a class library with no ASP.NET
Core framework reference; cookie authentication and `HttpContext` reading need
one, and a worker process has no session to manage. Identity's _store_ is
persistence; the session is HTTP.

### Identity tables live in the `auth` schema

One `AppDbContext`, one migration history, one transaction boundary — the same
reasoning ADR-0005 used for the job queue. Schema namespacing keeps the boundary
legible in the database, and means a future read-only grant on `app` does not
quietly include the user table.

## Consequences

**Accepted:**

- The password lifecycle is ours to maintain and get right, forever.
- No SSO. An enterprise customer wanting it triggers a migration to Option B,
  which needs its own ADR.
- No email transport exists in M01, so `RequireConfirmedEmail` is **false** and
  the development notifier logs reset tokens instead of sending them. It throws
  outside Development rather than degrading to a silent no-op.

**Gained:**

- No external identity dependency, and D-5 stays in M02 where it was.
- The browser never holds a credential it can read.
- Organization membership and authorization remain entirely local, which is what
  M01-03 will build on — and is also what Option B would have needed anyway.

**Migration path to B, if it is ever chosen:** both A and B terminate in the same
server-side cookie session. Replacing _who authenticates the user_ does not
require rebuilding downstream consumers, provided `ICurrentUser` stays the only
way identity reaches the application. That constraint is what keeps the option
open, and it is why `ICurrentUser` carries no roles and no organization.

## Not chosen

- **Option B now.** The better long-term answer, and the independent
  recommendation. Declined for this milestone because it requires authorizing an
  external identity dependency and advancing part of D-5.
- **Option C with bearer tokens.** Bearer tokens exist for clients that cannot
  use cookies. The studio is a same-origin SPA, so it can — and a token in
  browser-reachable storage would give up the main advantage of the cookie.
- **Email confirmation before sign-in.** Would be correct, and with no mail
  transport it would mean nobody could ever log in.

## Scope boundary — what this ADR does _not_ decide

**Authorization.** Decision **D-3** collapses the four-role model to two for the
MVP — **Member** and **Approver**, a user may hold both, and only Approver may
perform approval, rejection or waiver transitions. Enforcing that is **M01-03**,
which must not begin until M01-02 is independently accepted.

So in M01-02: the roles table exists because Identity's store requires it, **no
role is seeded, and no role is enforced.** Asserted by
`AuthenticationTests.No_role_is_seeded_because_authorization_is_m01_03`.

Organization scoping (S-20) is likewise M01-03. `ApplicationUser` deliberately
carries no `OrganizationId` yet: adding it here would ship an unreviewed
authorization decision inside an authentication task.

---

## Amendments

| #      | Subject                                              | Status                                                     |
| :----- | :--------------------------------------------------- | :--------------------------------------------------------- |
| **A1** | Corrections from the M01 Wave 2 review               | Accepted 2026-10-21 — boundary + D-8 gate superseded by A2 |
| **A2** | Same-origin BFF boundary; D-8 gate is pre-production | ✅ Accepted 2026-11-02 (Proposal 1; Proposal 2 Option 2)   |

### A1 — Corrections from the M01 Wave 2 review (2026-10-21)

The first implementation of this decision was independently reviewed and returned
**CHANGES REQUIRED** (6 P1, 4 P2, 1 P3). Four of those findings change what this
ADR can claim, so they are recorded here rather than left to the response
document. The decision itself — Option A — is unchanged.

#### Session revocation was claimed and did not work (W2-001)

The "Accepted/Gained" text above described sessions being revoked on password
change. `SecurityStampValidatorOptions.ValidationInterval` was configured, but
the validator is only invoked if the cookie's `OnValidatePrincipal` event calls
it, and nothing attached it. **No session was ever revoked.**

Corrected by registering through `AddIdentityCookies()` and attaching
`SecurityStampValidator.ValidatePrincipalAsync` explicitly. The helper matters
for a second reason: Identity uses four cookie schemes, and rejecting a principal
signs out across all of them — with only the application scheme registered, the
validator threw the first time it tried to revoke anything.

#### Password recovery is unavailable, and that is now explicit (W2-003)

This ADR listed recovery as implemented. It was not: the only notifier threw
outside Development, so a production user had no recovery path, and the throw
happened _only for addresses that exist_ — an account-existence oracle.

**Amended position:** recovery _token generation_ is implemented; recovery
_delivery_ is **blocked on a mail transport**, raised as decision **D-8**.
Availability is decided at startup, not per request — **Production refuses to
start** without a transport. M01-02 is therefore **incomplete** on the recovery
obligation, and this ADR no longer claims otherwise.

> **Amended by A2 on 2026-11-02.** Recovery is still undelivered and M01-02 is
> still incomplete on that obligation — but D-8 no longer gates M01-02
> _acceptance_. It is a fail-closed **pre-production** gate: see A2, Proposal 2.

#### Data Protection keys need an explicit protector (W2-008)

`PersistKeysToDbContext` **disables** the automatic encryption at rest that the
default key location provides. The first implementation persisted keys and
configured no protector, so the key ring protecting every session cookie was
written to PostgreSQL as readable XML.

No protector is hard-coded, because the right one depends on the deployment
target — a certificate, Key Vault or an approved equivalent, to be selected with
**D-5**. Instead the host **refuses to start outside Development** until one is
configured, and warns loudly in Development.

#### The browser boundary is cross-origin, not same-origin (W2-006) · **superseded by A2**

This ADR described a "same-origin BFF". The implementation was not: the frontend
ran on `http://localhost:3000` and called the API on another port, over HTTP —
and because the cookies are `Secure`, every auth request on the documented setup
path returned 500.

**Amended position:** the boundary is **cross-origin and same-site**, with
credentialed requests. The studio origin and the API origin differ by port (and
in deployment, by subdomain), which is a different _origin_ but the same _site_ —
so `SameSite=Strict` still permits the session cookie, while a genuinely
cross-site caller is refused. This requires, and now has: HTTPS everywhere in the
documented path, `AllowCredentials()` with an explicit origin allow-list, and
`credentials: "include"` on the client.

A true same-origin BFF (a reverse proxy in front of both) remains the better
end-state and would remove the CORS surface entirely. It is not required by this
decision and is not implemented.

> **Superseded by A2 on 2026-11-02.** The same-origin BFF was subsequently
> implemented and is now the accepted boundary. The paragraphs above are retained
> as the record of what was decided in A1, not as the current position.

#### What this amendment does not change

Option A stands. The obligations table stands, with recovery _delivery_ moved to
D-8. The layering, the `auth` schema, the authenticated-by-default policy and the
cookie's attributes are unchanged.

**Two decisions this amendment raises:**

| #       | Decision                                                                                               | Blocks                                       |
| :------ | :----------------------------------------------------------------------------------------------------- | :------------------------------------------- |
| **D-8** | A password-reset delivery transport (an authorized mail service)                                       | Recovery; **Production startup** (A2)        |
| **D-9** | Account proofing — confirmed email, or invitation/admin provisioning instead of open self-registration | **M01-03** attaching roles and organizations |

D-9 exists because registration is public and unconfirmed, so an email address is
self-asserted. The audit subject is now the **immutable user id** rather than the
address (W2-005), which makes attribution stable — but not _proved_. **S-21 is
partial, not met**, until D-9 is resolved.

---

### A2 — Boundary and recovery gate · **ACCEPTED**

- **Status:** ✅ **Accepted** — Proposal 1 approved; Proposal 2 approved as **Option 2**
- **Date raised:** 2026-10-21 · **Raised by:** Lead Developer
- **Date accepted:** 2026-11-02 · **Deciders:** Product Owner
- **Trigger:** M01-W2-R3 review
- **Supersedes:** A1's browser-boundary description, and A1's position that D-8 gates M01-02 acceptance

**This amendment is in force.** Both proposals below were approved on
2026-11-02. Where it conflicts with A1, A2 governs; the rest of A1 and the
accepted decision stand unchanged.

The approval was explicit on what it does **not** do: **D-9 is not resolved and
M01-03 is not authorized.**

#### Why it was raised separately rather than applied directly

At the time of writing, A1 described the browser boundary as **cross-origin and
same-site**. The
M01-W2-RR-003 correction replaced that with a **same-origin BFF proxy**: the
studio serves the browser and forwards `/api/*` server-side, so the browser talks
to one origin. That is what this ADR's original decision text claimed, and it is
now what the code does — verified in real Chromium.

Separately, A1 stated that **M01-02 is incomplete until recovery delivery
exists** (D-8). A later revision of `IMPLEMENTATION_STATUS.md` asserted the
opposite — that D-8 is only a pre-production gate — **without approval and
without a superseding amendment.** That assertion has been withdrawn. Rule 2 of
this repository's working rules exists to prevent exactly that, and the
Lead Developer broke it; the withdrawal is recorded in `IMPLEMENTATION_STATUS.md`
§7.

#### Proposal 1 — the boundary is same-origin via a BFF proxy · ✅ **APPROVED**

**Approved 2026-11-02. The same-origin BFF is the accepted boundary.** This
supersedes A1's "cross-origin and same-site" description, which is now
historical. CORS is not part of the browser authentication path.

|                          | A1 (accepted)                     | Proposed                                                        |
| :----------------------- | :-------------------------------- | :-------------------------------------------------------------- |
| Browser talks to         | the API's own origin              | **the studio's origin only**                                    |
| Session cookie           | cross-origin, same-site           | **same-origin**                                                 |
| CORS in the browser path | required, with `AllowCredentials` | **not used**                                                    |
| `X-Forwarded-Proto`      | n/a                               | trusted from loopback, so the API still issues `Secure` cookies |

The reason to prefer it is not tidiness. `SameSite` is compared **schemefully**,
so a `SameSite=Strict` cookie is not sent from a page on a different origin or
scheme — which is why the documented browser path failed in two consecutive
review rounds. Removing the cross-origin hop removes the failure mode rather than
configuring around it.

#### Proposal 2 — resolve D-8's gate semantics · ✅ **APPROVED AS OPTION 2**

Recovery _token generation_ is implemented. Recovery _delivery_ needs an external
mail service that M01 was never authorized to add. Two coherent positions were
put forward:

| Option | Meaning                                                                                                                                                                |
| :----- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **1**  | **A1 stands.** D-8 blocks M01-02 acceptance, and therefore M01-03                                                                                                      |
| **2**  | **Amend.** M01-02 may be accepted on authentication; recovery delivery becomes a **pre-production gate**, enforced by Production refusing to start without a transport |

**Decision: Option 2, approved by the Product Owner on 2026-11-02.**

In force from that date:

- **M01-02 may be accepted on authentication.** D-8 no longer blocks its
  acceptance.
- **Password-reset delivery is a fail-closed pre-production gate.** **No
  Production deployment may proceed until D-8 is resolved**, and this is
  enforced in code, not by convention: `IdentityStartupValidation` refuses to
  start the host when the configured transport is `None` in Production,
  `DevelopmentLog` outside Development, or `Email` (which is unimplemented).
  Today **no transport value starts in Production** — which is the intended
  meaning of the gate, not a defect.
- The obligation itself is unchanged. Recovery is **not** delivered, and nothing
  may describe M01-02 as complete on recovery.

A caution worth recording with the decision: a gate that refuses every value is
indistinguishable at the console from a misconfiguration. The error messages name
D-8 for that reason, and whoever resolves D-8 must remove the `Email` throw in
the same change that implements the transport, or Production will still refuse to
start.

#### Independent review input (round 4, 2026-10-21)

The round-4 independent review verified the code and tests as passing — 517
tests, 0 failures, 0 skips, plus 7/7 E2E — and **recommends approving both
proposals**: adopt the same-origin BFF description, and move reset delivery to a
fail-closed pre-production gate.

That was recorded as **input to the decision, not as the decision** — the
reviewer recommends, the Lead Developer recommends, and neither is an approver.
The Product Owner's decision on 2026-11-02 is what accepted this amendment, and
it matched both recommendations.

#### Applied on approval

Done in one pass on 2026-11-02: this status line, `IMPLEMENTATION_PLAN.md`,
`IMPLEMENTATION_STATUS.md`, `docs/04-engineering/SECURITY_REQUIREMENTS.md` and
the Wave 2 report. No runtime change was needed — the startup gate Option 2
requires was already implemented as part of W2-003/W2-RR-001, and is covered by
the 18-case transport matrix in `AuthenticationTests` plus the 16-case notifier
suite.
