# Response to the M01 Wave 2 Re-Review

**Decision under response:** CHANGES REQUIRED — 3 P1, 2 P2
**Response date:** 2026-10-21 · **Author:** Lead Developer
**Review:** [M01_WAVE2_RE_REVIEW.md](M01_WAVE2_RE_REVIEW.md)

**M01-02 is resubmitted. It is not accepted, and M01-03 has not begun.**

---

## Summary

All five findings accepted. Two were my previous "fixes" that did not do what I
said they did, and the review proved it by execution rather than by reading.

The recurring failure is now unmistakable, because it has happened three times
in a row on the same task:

> **I keep testing the mechanism and asserting the property.**

| Round | I wrote                          | I tested                                 | What was missing                         |
| :---- | :------------------------------- | :--------------------------------------- | :--------------------------------------- |
| 1     | `ValidationInterval`             | old password rejected, own session alive | nothing ever revoked                     |
| 2     | `AccessFailedAsync` on bad codes | the store says locked                    | the endpoint never read the flag         |
| 2     | `Secure` cookies + HTTPS API URL | `TestServer` over https                  | a real browser could not keep the cookie |

In each case the code I added was correct in isolation and inert in place. The
antidote is not more care — it is asserting the _outcome a user or attacker would
experience_, which is what this round's tests now do: a correct TOTP code after
lockout must be **refused**, and a real Chromium must end up **with a cookie in
its jar**.

| Finding                                       | Severity | Status                           |
| :-------------------------------------------- | :------- | :------------------------------- |
| W2-RR-001 Production accepts `DevelopmentLog` | P1       | ✅ Fixed, + 8-case matrix        |
| W2-RR-002 MFA lockout recorded, not enforced  | P1       | ✅ Fixed, + 2 tests              |
| W2-RR-003 Browser path still fails            | P1       | ✅ Fixed, + 3 real-browser tests |
| W2-RR-004 E2E regressed with the API up       | P2       | ✅ Fixed, **7/7 passing**        |
| W2-RR-005 Documentation contradictions        | P2       | ✅ Reconciled, D-8 rule decided  |

---

## W2-RR-001 — Production accepted a development transport · **FIXED**

The gate rejected `Email` and rejected `None` in Production, then returned for
every other value — so `DevelopmentLog` started in Production, where it either
writes reset credentials to production logs or silently delivers nothing.

Two changes, because one was not enough:

- **Startup** rejects `DevelopmentLog` unless `IsDevelopment()`.
- **The notifier** now takes `IHostEnvironment` and includes it in the condition.
  It previously decided whether to log a credential purely from configuration
  values, so any path that skipped the startup check would still have logged the
  token. Defence in depth, as the review asked.

**Test:** `The_reset_transport_matrix_starts_only_where_it_is_safe` — eight cases
across environment × transport × log-flag, including both Production +
`DevelopmentLog` variants that the previous round never probed. Enumerating the
matrix is the only way to know a gate has no gaps; two hand-picked cases told me
it was fail-closed when it was not.

## W2-RR-002 — MFA lockout is now enforced · **FIXED**

The reviewer's Chromium probe is exactly right: five wrong codes, then the sixth
_correct_ code returned `204` and enabled MFA. `VerifyTwoFactorTokenAsync`
verifies a token; it does not consult lockout. **A flag is not a guard unless the
protected operation reads it** — and my test asserted the flag.

`RefuseIfLockedOutAsync` now runs first in **all three** factor-management
endpoints (enroll, enable, disable), returning `423 auth.locked_out`.

**What happens to the session, since the review asked it be defined:** the
session is left intact and only factor management is refused. Signing the caller
out would let anyone holding a session lock themselves out of the application by
guessing codes — converting a throttle into a self-inflicted denial of service.
A `423` is returned rather than a generic error because the caller is already
authenticated; the non-disclosure rule from W2-002 exists to protect _anonymous_
probes, and withholding the reason from the account's own owner buys nothing.

**Tests:** `Repeated_invalid_factor_codes_are_bounded_by_lockout` now continues
past the store assertion and submits a **correct** code, requiring `423` and
`TwoFactorEnabled == false`. `Disabling_two_factor_is_refused_while_locked_out`
covers the other direction.

## W2-RR-003 — The browser path works, and is tested in a browser · **FIXED**

I changed the API URL to HTTPS and left both READMEs opening the frontend on
HTTP. Schemeful `SameSite` means those are different sites, so a `Strict` cookie
is never sent — which is precisely what the reviewer reproduced.

**The boundary is now genuinely same-origin**, which is what ADR-0008 claimed
from the beginning:

- `next.config.ts` proxies `/api/*` and `/health*` to the API server-side.
- The browser calls **only** the studio origin; `API_BASE_URL` is empty.
- The studio runs over HTTPS (`npm run dev:https`), so the `Secure` cookie is
  storable.
- CORS is no longer involved in the browser path at all.

Two things this took that are worth recording, because both were measured rather
than guessed:

1. **Next's rewrite does not forward `X-Forwarded-Proto`.** The same request
   sent directly to the API with that header returned `200`; through the rewrite
   without it, `500`. Fixed with `src/middleware.ts`, and the API now calls
   `UseForwardedHeaders` — trusting loopback only, so a remote client cannot
   forge the scheme.
2. **A relative base URL cannot be used server-side.** A server component has no
   document origin to resolve against, so `api.ts` uses the API origin on the
   server and the proxied path in the browser. Conflating them made the status
   page report "no provider adapters" instead of "sign in".

Proxying to the API's **HTTP** port is deliberate: TLS terminates at the BFF,
which is how a deployed reverse proxy works, and it avoids Next's undici-based
proxy rejecting the self-signed development certificate (`NODE_TLS_REJECT_UNAUTHORIZED`
has no effect there).

**Tests — in real Chromium**, `frontend/e2e/auth.spec.ts`:

- token issuance → registration → login → **cookie present in the browser jar**,
  asserted `httpOnly`, `secure`, `sameSite=Strict` → authenticated `/me` and
  `/api/v1/providers` → logout → `401`
- a state-changing request with no token → `400 auth.antiforgery_failed`
- anonymous provider access → `401`

The previous round listed this test as an open gap. It is now executed, and it is
the only kind of test that could have caught this class of bug.

## W2-RR-004 — The E2E gate passes with the API up · **FIXED**

`loadStatus` put the anonymous readiness call and the now-protected provider call
in one `Promise.all`, so the 401 took the health result down with it and the page
rendered raw problem JSON.

- The two requests are independent. An unauthenticated visitor still sees whether
  the API and database are up.
- A 401 renders a deliberate **"Sign in to view provider status"** state, not an
  error — calling it an error would be untrue and would hide a real outage.
- Playwright targets HTTPS and the proxied origin, matching the chosen boundary.
- The provider spec no longer skips itself when an alert appears, which is how it
  silently stopped asserting anything.

**Measured: 7 passed, 0 failed, 0 skipped** against both real services. No
assertion was weakened; three were added.

## W2-RR-005 — Documentation reconciled, and the D-8 rule decided · **FIXED**

| Was                                                                               | Now                                                                            |
| :-------------------------------------------------------------------------------- | :----------------------------------------------------------------------------- |
| Status table "Wave 1 only", M01-02 stopped at open D-7                            | Wave 2, M01-02 only; D-7 resolved; two review rounds returned CHANGES REQUIRED |
| Plan/status/security said "same-origin" while the ADR amendment said cross-origin | **Same-origin via the studio's BFF proxy** — and now true, so both can say it  |
| Wave 2 report: "the notifier throws outside Development"                          | Marked superseded; availability is a startup gate                              |
| D-8's gate semantics undefined                                                    | **Decided and stated once** — see below                                        |

### The D-8 rule

> **D-8 does not block M01-02 acceptance. It is a pre-production gate.**

M01-02 is accepted on authentication: sign-in, session, revocation, lockout, MFA,
antiforgery, headers, and recovery **token generation**. Recovery **delivery** is
carried as an explicit fail-closed gap — Production refuses to start without a
transport, so it cannot reach a user silently.

Consequences, both deliberate: **M01-03 is not blocked by D-8** (it is blocked by
M01-02 acceptance and by **D-9**), and **no Production deployment is possible
until D-8 resolves**, because the host will not start. The gate is real; it is
just not placed at M01-02.

This is a judgement call on a question the documents left open, and it is the
Product Owner's to overrule. If you prefer D-8 to block M01-02 acceptance, say so
and the rule changes. What was not acceptable was leaving both readings in place.

---

## Verification — measured, this run

```
dotnet build                        0 Warning(s), 0 Error(s)
dotnet test                         total: 313, failed: 0, succeeded: 313, skipped: 0
dotnet format --verify-no-changes   exit 0
npm test                            frontend 14 passed · contracts 164 passed
npm run typecheck / lint             clean
npm run format:check                All matched files use Prettier code style!
npm run build --workspace frontend  ✓ Compiled successfully in 16.3s
migrations has-pending-model-changes  No changes have been made to the model
git diff --check                    exit 0
npm run audit:prod                  found 0 vulnerabilities

playwright test (both services up)  7 passed, 0 failed, 0 skipped
```

**Total: 491 passing, 0 failed, 0 skipped** — plus 7 E2E, counted separately.

| Suite                         | Previous round |                Now |  Added |
| :---------------------------- | -------------: | -----------------: | -----: |
| Unit                          |            185 |            **185** |      — |
| Architecture                  |             16 |             **16** |      — |
| Integration (real PostgreSQL) |            103 |            **112** | **+9** |
| Frontend                      |             14 |             **14** |      — |
| Shared contracts              |            164 |            **164** |      — |
| **Total**                     |        **482** |            **491** | **+9** |
| E2E (real Chromium)           |   4, 1 failing | **7, all passing** | **+3** |

Integration tests **passed, not skipped**, against PostgreSQL 15.19.

**One honest note:** PostgreSQL stopped twice between runs in this environment,
and the first attempt of a suite reported `112 skipped`. That is reported rather
than quietly re-run — an all-skipped suite is not a passing suite. The counts
above are from runs with the server up.

---

## Still open — stated, not hidden

| Item                                    | Status                                                                     |
| :-------------------------------------- | :------------------------------------------------------------------------- |
| **D-8** — reset delivery transport      | Open. Pre-production gate; Production refuses to start                     |
| **D-9** — account proofing              | Open. **Blocks M01-03** independently of everything else                   |
| **D-5** — Data Protection key protector | Open. Production refuses to start                                          |
| MFA recovery codes                      | Still absent. A lost authenticator still needs an operator                 |
| M01-11 CI acceptance                    | E2E 7/7 locally; the CI run still requires a push, which is not authorized |
| M01-12 Docker                           | **BLOCKED** — not installed                                                |
| S-20, role enforcement                  | **M01-03**, not started                                                    |

---

## Confirmations

- **No Stitch or provider call.** `BillableOperationsEnabled` still `false` in
  both hosts and still asserted by a test.
- **No spend.** No Docker, no OS packages, no external service added.
- **Nothing committed, pushed, deployed or opened as a PR.**
- **M01-03 not started.** No role seeded, none enforced, no organization
  scoping; the guard test still asserts it.
- **Seven M00 ADRs unmodified.** ADR-0008's decision is unchanged; its
  corrections remain an appended amendment.
- **No guard, test or acceptance criterion weakened.** Changed expectations were
  ones asserting the defective behaviour itself.
- **A-08 and A-09 remain open.**

---

**M01 Wave 2 re-review corrections applied. M01-02 resubmitted and not accepted;
M01-03 remains blocked on M01-02 acceptance and on D-9.**
