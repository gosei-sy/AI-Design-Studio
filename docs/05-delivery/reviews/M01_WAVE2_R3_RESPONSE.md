# Response to the M01 Wave 2 Round-3 Review

**Decision under response:** CHANGES REQUIRED — 1 P1, 3 P2, 1 P3
**Response date:** 2026-10-21 · **Author:** Lead Developer
**Review:** [M01_WAVE2_R3_CODEX_REVIEW.md](M01_WAVE2_R3_CODEX_REVIEW.md)

**M01-02 is not accepted. M01-03 has not begun.** One finding here is not a
defect I can simply fix — it is a decision I took that was not mine, and it is
now back with you.

---

## R3-001 — P1 — I changed a gate I had no authority to change

**Accepted without qualification. This is the finding that matters, and it is
worse than a bug.**

ADR-0008 is **Accepted** and states that M01-02 is incomplete until recovery
delivery exists. In the previous response I wrote a rule into
`IMPLEMENTATION_STATUS.md` declaring the opposite — "D-8 does not block M01-02
acceptance; it is a pre-production gate" — with no superseding ADR and no
Product Owner or Solution Architect approval. I then reported against my own
rule as though it were settled.

Rule 2 of this repository exists for precisely this. I applied it to every other
decision in this milestone — I refused to close D-4 on inference, I refused to
pick the authentication option, I refused to seed a role because authorization
was someone else's task — and then granted myself the one answer I needed to
close a task I was reporting on. **That is the worst possible reason to assume
authority**, and the fact that my recommendation may well be the right call does
not make the process acceptable. A decision I take because it unblocks my own
report is not a decision, it is a conflict of interest.

### What I did about it

**Withdrawn.** `IMPLEMENTATION_STATUS.md` §7 now records the withdrawal, says
plainly why it was wrong, and states that **ADR-0008's position stands: M01-02
is incomplete on the recovery obligation.**

**Raised properly.** ADR-0008 now carries **amendment A2, status ⏸ Proposed —
awaiting Product Owner + Solution Architect approval.** It is explicitly _not in
force_, and it covers both items I had quietly changed:

| Proposal         | Question for you                                                                                                                                                                                                                 |
| :--------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **1 — boundary** | A1 described it as cross-origin/same-site; the implementation is now a **same-origin BFF proxy**, which is what the original decision text claimed. Approve the description, or tell me the implementation should change instead |
| **2 — D-8 gate** | **Option 1:** A1 stands, D-8 blocks M01-02 acceptance and therefore M01-03. **Option 2:** amend — M01-02 acceptable on authentication, recovery delivery becomes a pre-production gate enforced by Production refusing to start  |

My recommendation remains **option 2**, because option 1 blocks the milestone on
an external mail service M01 was never authorized to add. It is recorded as a
recommendation. **Until you decide, option 1 is in force**, and no document
claims otherwise.

---

## R3-002 — P2 — CI would have failed · **FIXED**

`playwright.config.ts` defaulted `API_ORIGIN` to `https://localhost:7080` while
`next.config.ts` and the CI job both use `http://localhost:5080`. The local run
passed because I was setting the variable by hand; CI sets nothing, so it would
have taken the broken default.

- Playwright's default is now `http://localhost:5080`, matching `next.config.ts`.
- The CI e2e job sets `API_ORIGIN` **explicitly** and `NEXT_PUBLIC_API_BASE_URL`
  to empty, with a comment tying it to `ASPNETCORE_URLS` so the three cannot
  drift apart silently again.

Worth naming: this is a configuration default that only the environment I was
_not_ running would have exercised. The same shape as the test-database defect in
Wave 2 — a default that is wrong but never taken locally.

## R3-003 — P2 — The "matrix" was not one · **FIXED**

Eight hand-picked cases were not a matrix, and calling them one was the same
error as asserting a flag and claiming a guard. The product is 3 environments × 3
transports × 2 log-flag values = **18**, and all 18 now execute, each with its
expected startup outcome and expected failure message.

**And the test the review asked for that I had not written:**
`PasswordResetNotifierTests` — 16 cases proving a reset token reaches a log
**only** in Development, with the development transport, with logging explicitly
opted in. Eleven combinations assert it is _not_ logged. Two more assert an
undeliverable reset is still reported _without_ the token, because silence would
hide an outage. Three assert the notifier never throws, which is what made it an
account-existence oracle originally.

The startup gate was my argument that tokens cannot leak. These are the evidence.

## R3-004 — P2 — Stale documentation · **FIXED**

| Document                   | Was                                                                     | Now                                                                                                                |
| :------------------------- | :---------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------- |
| `TEST_STRATEGY.md`         | 185/57, total 436, `dotnet test # 258`                                  | **201/122, total 517**, `# 339`; new section on the E2E boundary tests and why they are counted separately         |
| `frontend/README.md`       | `npm run dev` → `http://localhost:3000`, API at `http://localhost:5080` | `dev:https` → `https://localhost:3000`, API reached through the studio's proxy                                     |
| `M01_WAVE2_REPORT.md`      | "same-origin HttpOnly cookie/BFF", 464 total                            | Boundary wording marked **superseded** (ADR-0008 A1/A2); 464 marked **superseded** as the Wave 2 submission figure |
| `IMPLEMENTATION_STATUS.md` | self-authored D-8 rule                                                  | Withdrawal notice; D-8 row points at the open decision                                                             |
| `ADR-0008`                 | A1 only                                                                 | A2 added, **Proposed**, not in force                                                                               |

## R3-005 — P3 — `middleware.ts` deprecation · **FIXED**

Renamed to `frontend/src/proxy.ts` with the export renamed to `proxy`. The
deprecation warning is gone from a clean `next dev` start, and the E2E suite
still passes 7/7 through it.

---

## Verification — measured, this run

```
dotnet build                        0 Warning(s), 0 Error(s)
dotnet test                         total: 339, failed: 0, succeeded: 339, skipped: 0
  unit 201 · architecture 16 · integration 122      (all skipped: 0)
dotnet format --verify-no-changes   exit 0
npm test                            frontend 14 passed · contracts 164 passed
npm run typecheck / lint             clean
npm run format:check                All matched files use Prettier code style!
npm run build --workspace frontend  ✓ Compiled successfully in 18.8s
migrations has-pending-model-changes  No changes have been made to the model
git diff --check                    exit 0
npm run audit:prod                  found 0 vulnerabilities

playwright test (both services up)  7 passed, 0 failed, 0 skipped
```

**Total: 517 passing, 0 failed, 0 skipped**, plus **7 E2E** counted separately.

| Suite                         | Previous round |     Now |   Added |
| :---------------------------- | -------------: | ------: | ------: |
| Unit                          |            185 | **201** | **+16** |
| Architecture                  |             16 |  **16** |       — |
| Integration (real PostgreSQL) |            112 | **122** | **+10** |
| Frontend                      |             14 |  **14** |       — |
| Shared contracts              |            164 | **164** |       — |
| **Total**                     |        **491** | **517** | **+26** |
| E2E (real Chromium)           |              7 |   **7** |       — |

Integration tests **passed, not skipped**, against PostgreSQL 15.19.

---

## What blocks acceptance now

**Nothing I can resolve.** Every finding in this review is addressed except the
one that was never mine:

| Blocker                                                 | Owner                                  | Note                                                                                        |
| :------------------------------------------------------ | :------------------------------------- | :------------------------------------------------------------------------------------------ |
| **ADR-0008 A2** — boundary description and the D-8 gate | **Product Owner + Solution Architect** | Proposed, not in force. Until decided, ADR-0008 stands and M01-02 is incomplete on recovery |
| **D-8** — reset delivery transport                      | Product Owner                          | Needs an authorized mail service                                                            |
| **D-9** — account proofing                              | Product Owner + Solution Architect     | **Blocks M01-03 independently** of everything above                                         |
| **D-5** — Data Protection key protector                 | Solution Architect                     | Production refuses to start without it                                                      |
| M01-11 CI acceptance                                    | —                                      | E2E 7/7 locally; the CI run needs a push, which is not authorized                           |
| M01-12 Docker                                           | Environment                            | Not installed                                                                               |

MFA recovery codes remain absent — a lost authenticator still needs an operator.
Unchanged and still worth deciding before real users exist.

---

## Confirmations

- **No Stitch or provider call.** `BillableOperationsEnabled` still `false` in
  both hosts and still asserted by a test.
- **No spend**, no Docker, no OS packages, no external service.
- **Nothing committed, pushed, deployed or opened as a PR.**
- **M01-03 not started.** No role seeded, none enforced, no organization scoping.
- **Seven M00 ADRs unmodified.** ADR-0008's accepted decision and A1 are
  unmodified; A2 is appended as **Proposed**.
- **No self-granted decisions remain.** The D-8 rule is withdrawn; the boundary
  description awaits approval rather than asserting itself.
- **A-08 and A-09 remain open.**

---

**M01 Wave 2 round-3 corrections applied. M01-02 remains unaccepted and now
depends on a decision rather than on code: ADR-0008 A2 needs Product Owner and
Solution Architect approval. M01-03 remains blocked on that and, independently,
on D-9.**
