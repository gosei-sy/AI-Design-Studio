# M01 Wave 1 Report

**Milestone:** M01 — Authentication, core CRUD, live generation
**Authorized:** 2026-10-21, explicitly by the Product Owner · **Scope:** Wave 1 only
**Report date:** 2026-10-21 · **Author:** Lead Developer

**No M01 task is marked complete in this report.** Two produced executed
evidence, one is blocked by the environment, and one is stopped at a decision
that is not mine to make.

---

## 1. Milestone state changes

| Document                                    | Change                                                                                                                                |
| :------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------------------ |
| `IMPLEMENTATION_PLAN.md`                    | M01 `Not started` → 🚧 **In progress**, authorized 2026-10-21; Wave 1 task-state table added; entry criteria record the authorization |
| `docs/05-delivery/IMPLEMENTATION_STATUS.md` | Current milestone → M01 In progress; M00 retained as ✅ Complete/APPROVED; §1 M01 row rewritten; rule-9 note replaced                 |
| `README.md`                                 | Status sentence → M00 complete, M01 in progress (Wave 1); E2E section rewritten from "unverified" to the real procedure               |

Recorded, as required:

- **M00 remains Complete and APPROVED** (2026-10-21). Nothing in the review
  history was rewritten.
- **M01 is In progress**, by Product Owner authorization — not by M00 approval
  alone.
- **M01-01 live execution remains blocked on D-1/D-2.**
- **M01-03 remains blocked on D-3** (and on M01-02 being accepted).
- **M01-07/08/09 and the complete M01-10 remain gated on A-08.**
- **M01-11 and M01-12 remain carried-over verification work** — M01-11's browser
  blocker is now cleared; M01-12 is still blocked.

### One thing deliberately not changed

`frontend/src/app/page.tsx` still renders "Milestone M00 — foundation", and
`status.spec.ts` asserts it. Updating that string is product behaviour, belongs
to M01-10, and M01-10 is gated. Changing it now would also mean editing a
passing E2E assertion to match — exactly the move the brief forbids. Left as
follow-up for M01-10.

---

## 2. M01-01 — offline preparation

No credential used, no provider call made, no spend. Everything below runs
locally.

### SPIKE-02's live test plan is reproducible

Re-read in full (240 lines, 9 steps). It states prerequisites, per-step expected
results, and which named code each step validates. It is executable as written by
someone holding credentials.

Cost classification, stated once and used consistently everywhere (corrected per
W1-RR-001 — earlier wording here said "steps 1–2 are free"):

| Operation                                                                   | Gate                    | Cost                                              |
| :-------------------------------------------------------------------------- | :---------------------- | :------------------------------------------------ |
| `--dry-run` (step 2)                                                        | **Neither D-1 nor D-2** | **No cost** — no credentials, no network          |
| Authenticated status (step 1) — readiness probe, **not** a design operation | **D-1**                 | **Cost unknown** — not in D-2's count             |
| Create / get / delete (steps 3,4,8) — design operations                     | D-1 + D-2               | **Cost unknown** — must be in D-2's approved list |
| Generation (step 3)                                                         | D-1 + D-2               | **Billable** (amount unobserved — that is A-09)   |
| Upload / apply / variants (5,6,7)                                           | D-1 + D-2               | **Cost unknown — STOP** unless itemised by D-2    |

"Cost unknown" means not observed. Only `--dry-run` is established as free, and
only because SPIKE-01 verified it makes no network call.

### CLI surface inspected without authentication

SPIKE-01 recorded that `--schema` and `--dry-run` work "with **no credentials and
no network**", which is what makes this safe to do under a no-provider-call
constraint. Obtained via `npm pack` into `/tmp`, outside the repository, so no
dependency was added to `package.json` or the lockfile.

| Check                        | Recorded in M00 | Observed now                         | Match |
| :--------------------------- | :-------------- | :----------------------------------- | :---- |
| CLI version                  | 0.11.1          | **0.11.1**                           | ✅    |
| `bodyFont` enum size         | 67              | **67**                               | ✅    |
| `bodyFont` enum **contents** | 67 names        | **identical set**                    | ✅    |
| `headlineFont` / `labelFont` | same 67         | **identical to `bodyFont`**          | ✅    |
| `colorMode` enum             | LIGHT, DARK     | **LIGHT, DARK**                      | ✅    |
| `colorVariant` enum size     | 9               | **9**                                | ✅    |
| `roundness` enum             | 5 steps         | **ROUND_TWO/FOUR/EIGHT/TWELVE/FULL** | ✅    |

**The provider's input surface has not moved since SPIKE-01.** This matters
beyond bookkeeping: ADR-0004's projection rules and
`StitchThemeProjectionTests.The_verified_font_enum_matches_the_provider_schema_size`
are both written against these enums, so a drift would have invalidated the
projection design before any credential arrived. It has not.

Worth stating precisely: this verifies the **request** contract only. The
**response** shapes for `generate screen` and `get screen` remain unverified.

Correcting an earlier mis-statement (M01-W1-002): those shapes are **SPIKE-02
technical evidence**, not an assumption in their own right, and they are
certainly not "the whole of A-09".

| Item     | What it actually asks                                                   |
| :------- | :---------------------------------------------------------------------- |
| **A-08** | Can `STITCH_API_KEY` be provisioned for unattended **server-side** use? |
| **A-09** | Is the **observed per-generation cost** acceptable at expected volume?  |
| SPIKE-02 | The response shapes, recorded as technical evidence                     |

A-09 cannot be assessed from a response shape at all — it needs an observed
price, which is why SPIKE-02 step 9 records cost. Both assumptions stay open.

### Fixture locations and harness boundaries prepared

Created `backend/tests/AIDesignStudio.UnitTests/Fixtures/Stitch/README.md` —
**containing no fixtures.** It fixes the filenames, mandates raw unedited stdout,
and states the rule: nothing goes in that directory that did not come out of a
real provider call.

No new seam was needed. `IStitchCommandRunner` is already the single boundary
between the adapter and the process, and `StitchAdapterTests` already drives the
adapter through `FakeStitchRunner`. A recorded response is wired in by reading
the file into that same fake:

```
File  ->  StitchResult.RawStdout  ->  FakeStitchRunner  ->  StitchDesignProvider  ->  assertions
```

### Commands, grouped by the gate each one needs

Split by gate rather than presented as one "after D-1/D-2" block (corrected per
W1-RR2-002): the dry-run needs neither decision and the status check needs only
D-1, so a single heading misstated what is blocked.

#### Needs neither D-1 nor D-2 — runnable today

```bash
npm pack @google/stitch && tar xzf google-stitch-0.11.1.tgz   # or install it

# No credentials, no network. Verified by SPIKE-01, re-executed this wave.
node package/dist/stitch.js generate screen --schema
node package/dist/stitch.js generate screen --new-project "AIDS Spike" \
  --prompt "Login screen with email and password" \
  --device MOBILE --dry-run --json > generate-screen.dry-run.json
```

#### Needs D-1 only — readiness probe, not a design operation

Readiness probes are **not** controlled commands and are **not** counted against
D-2's budget — they prove a credential works and touch no design. Cost is
unobserved, so **stop immediately if either returns unexpected billing
behaviour.**

```bash
stitch login                   # interactive: browser + loopback 8085
export STITCH_API_KEY=<key>    # separate credential; login alone is insufficient

stitch status --json > status.authenticated.json   # expect authenticated + canvas.present
curl -s localhost:5080/api/v1/providers/google-stitch/readiness   # expect isUsable: true
```

#### Needs D-1 **and** D-2 — the controlled commands

Every command in this group is billable **or cost unknown**. Each one is counted
against D-2's authorized total and **confirmed immediately before execution**.
None is ever retried automatically.

```bash
# 1 — create project          (cost unknown)
stitch create project --title "AIDS Spike" --json > create-project.success.json

# 2 — generate ONE screen     (⚠ BILLABLE)
stitch generate screen --project "$PROJECT_ID" \
  --prompt "Login screen with email and password" \
  --device MOBILE --title "Login" --json > generate-screen.success.json

# 3 — retrieve it             (cost unknown; needs the screen from command 2)
stitch get screen --project "$PROJECT_ID" --screen "$SCREEN_ID" --json > get-screen.success.json

# 4 — clean up                (cost unknown)
stitch delete project --project "$PROJECT_ID" --json
```

#### STOP — not in the default authorization

Steps 5 (upload), 6 (apply-design-system) and 7 (variants) are **cost unknown**
and must not be run unless D-2 itemises them separately.

Then step 9: record the observed cost of every command above, which is what makes
**A-09** assessable; and record the credential findings, which is what makes
**A-08** assessable.

### The guard does **not** protect this, and must stay `false`

This correction is M01-W1-001, and the earlier version of this section was
wrong. It said `BillableOperationsEnabled` "must be set true only for steps 3+",
which was wrong twice over.

| Control                     | Enforced in                                                 | Protects a direct `stitch` command? |
| :-------------------------- | :---------------------------------------------------------- | :---------------------------------- |
| `BillableOperationsEnabled` | `StitchDesignProvider.EnsureBillableAllowed` (3 call sites) | **No**                              |
| Job idempotency keys        | `PostgresJobQueue.EnqueueAsync`                             | **No**                              |

Verified in the source: `StitchProcessCommandRunner` declares the option and
never reads it — the check sits one layer above, in the adapter. A CLI command
typed in a terminal starts at the process and therefore passes through neither
the adapter nor the job queue. **Recording fixtures bypasses the application
entirely.**

So enabling the flag would have protected nothing, while removing the only thing
stopping the _running application_ from spending. And "idempotency prevents
repeat charges" does not hold on this path either: re-running a generate command
charges again.

**The guard stays `false` for the whole exercise.**

### What D-2 must actually authorize

Not a general approval to spend. Specifically:

1. **An exact operation-count limit** — a number of **controlled commands**,
   meaning billable _or_ cost unknown.
2. **A monetary cap** only where provider pricing exposes something measurable.
   Where it does not, that must be said rather than implied.
3. **Initially one project and one screen generation.** Nothing beyond.
4. **An explicit stop before upload, apply-design-system and variants**, unless
   those calls are separately itemised in the cap.

> **Every billable _or cost-unknown_ live design operation must be
> pre-authorized, counted, confirmed immediately before execution, and never
> automatically retried.**

**What is a live design operation.** Create, generate, get and delete — plus the
optional upload, apply-design-system and variants — are live design operations.
They are the **controlled commands**, and they are what D-2 authorizes and counts.

**`stitch status` is not one.** It is a **D-1-only readiness probe**: it proves a
credential works and does not touch a design. It needs D-1, its cost is unobserved,
and it is **not** counted against D-2's controlled-command budget. The earlier
wording said "live provider _command_", which swept the readiness probe into a
category requiring D-2 — contradicting the same documents' D-1-only mapping for
`status.authenticated.json` (W1-RR3-001).

That distinction is not a loophole: **if readiness returns any unexpected billing
behaviour, stop immediately** and treat it as an anomaly, exactly as for a
controlled command. What changes is the authorization and counting, not the
obligation to stop.

These are the **controlled commands**. The term is load-bearing: keying the
controls to "paid" or "billable" while the cost table said create, get and delete
are _cost unknown_ left those three outside counting, confirmation and the
no-retry rule (W1-RR2-001). **Making the labels more honest moved commands out of
the protected set** — a category is only as safe as the controls that name it.

Operator rules, non-negotiable: **confirm immediately before each controlled
command**; **stop on any anomaly** — unexpected output, a CLI retry suggestion, a
timeout, or unknown billing behaviour; and **never automatically retry a
controlled command.**
The CLI exits 0 on failure (SPIKE-01), so a command that looks failed may still
have been charged — a human decides what happens next.

No wrapper script is offered, and none should be written. The provider exposes no
per-call price to the CLI, so a script cannot know what it has spent; naming one
`safe-stitch.sh` would imply a bound that does not exist. A counted checklist is
at least honest about being a checklist.

### Enabling the guard — a separate, narrower authorization

It may be turned on **only** for a test that runs _through the application
adapter_, under its own authorization distinct from this CLI spike, and then as
environment-scoped configuration
(`Providers__Stitch__BillableOperationsEnabled=true` for that one run) — **never**
by editing the `appsettings.json` defaults, which are the fail-closed baseline
for every environment including CI.

### Billable guard confirmed false by default

```csharp
// StitchCommandRunner.cs:28 — no initializer, so the CLR default is false
public bool BillableOperationsEnabled { get; set; }
```

```
backend/src/AIDesignStudio.Api/appsettings.json:26      "BillableOperationsEnabled": false
backend/src/AIDesignStudio.Worker/appsettings.json:22   "BillableOperationsEnabled": false
```

Fail-closed in all three places: the type default, and both hosts explicitly.
`EnsureBillableAllowed` throws `BILLABLE_OPERATIONS_DISABLED` while it is off.

### A-08 and A-09 remain open

Neither was touched. Nothing in this report claims A-08 is resolved.

### What remains blocked by D-1 and D-2

Corrected per M01-W1-002. The earlier version put step 2 behind D-1 and claimed
D-2 blocked "every fixture" — both overstated the blocker and hid work that is
already possible.

| Gate          | What it gates                                                                                                                                                                                                                                                                                          |
| :------------ | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Neither**   | SPIKE-02 **step 2**, the `--dry-run` plan. Runs with no credentials and no network, so `generate-screen.dry-run.json` can be recorded **today**. It was in fact re-executed during this wave                                                                                                           |
| **D-1 only**  | Authenticated readiness and live canvas access — `stitch status --json`, and our readiness endpoint returning `isUsable: true`. `status.authenticated.json` needs D-1 and **not** D-2                                                                                                                  |
| **D-2**       | Every approved live **design operation** that is **billable or cost unknown** — `generate screen` (billable); `create project`, `get screen`, `delete project` (cost unknown); plus `upload`/`apply-design-system`/`variants` only if separately itemised. And **A-09**, which needs an observed price |
| **D-1 + D-2** | `generate-screen.success.json`. `get-screen.success.json` too — not because retrieval is necessarily billable, but because there is nothing to retrieve until the authorized generation has created a screen                                                                                           |
| **Both**      | M01-01's acceptance criteria in full, and therefore **A-08**; and consequently M01-07, M01-08, M01-09 and the complete M01-10                                                                                                                                                                          |

**A-09** needs D-1 and D-2 as well, but for a different reason from the others:
it is a question about _observed cost_, so it cannot be assessed until a billable
call has actually been made and its price recorded.

---

## 3. M01-11 — E2E executed

### The browser blocker, and the version trap behind it

The failure recorded at M00 closure was:

```
browserType.launch: Executable doesn't exist at
  .../ms-playwright/chromium_headless_shell-1248/chrome-headless-shell-linux64/chrome-headless-shell
```

Measured versions:

| Resolution path                         | Version    |
| :-------------------------------------- | :--------- |
| `package-lock.json`                     | **1.64.1** |
| `frontend/node_modules/.bin/playwright` | **1.64.1** |
| `npx playwright` from the repo root     | **1.62.0** |

Each Playwright release pins its own browser build. Installing with 1.62.0 would
have downloaded a build 1.64.1 then cannot find — the same error, with the
install appearing to succeed. Installed with the workspace binary:

```bash
cd frontend && ./node_modules/.bin/playwright install chromium
# -> Chrome Headless Shell 156.0.8078.4 (playwright chromium-headless-shell v1248)
```

`v1248` — the exact build the error named.

### An environment fault found on the way

`npm run build --workspace frontend`, green earlier the same day, began failing
with `sh: 1: next: not found`. Cause: **every `bin/` directory was missing across
the installed tree** — 21 dangling symlinks including `next`, `eslint`,
`typescript` and `vite`. `/workspace` is a Windows drvfs mount (its `df -i`
reports a negative inode count), which is the symlink-reliability hazard the
environment notes warn about.

Repaired with `npm ci`, which installs from the lockfile and cannot rewrite it —
confirmed byte-identical afterwards. Dangling symlinks: 21 → **0**.

This was not caused by any change in this wave, and it is worth recording
because it briefly made an already-verified gate fail. Nothing was "fixed" in
product code to accommodate it.

### A real defect in the spec, found only because the specs could finally run

First executed run: **2 passed, 1 failed, 1 skipped.**

```
Error: locator.isVisible: Error: strict mode violation:
  getByRole('alert') resolved to 2 elements:
    1) <p role="alert" ...>Could not reach the API at http://localhost:5080/…</p>
    2) <div role="alert" aria-live="assertive" id="__next-route-announcer__"></div>
```

Next.js renders its own route announcer with `role="alert"` as a sibling of
`<main>`, so a bare `getByRole("alert")` matched two elements and every call on
it failed strict mode. The specs had never reached this line before, because they
died at `browserType.launch`.

Fixed by **scoping the locator**, not by weakening anything:

```ts
const statusAlert = (page: Page) => page.getByRole("main").getByRole("alert");
```

Every assertion is unchanged — same matchers, same regexes, same timeout. Only
the locator now identifies the element it was always meant to identify. No
`.first()` was added to paper over the ambiguity, and no product code was
touched.

### Actual results

With the API running (the mode CI uses):

```
Running 4 tests using 4 workers
  ✓ 2 … never renders a credential value (1.1s)
  ✓ 1 … lists provider capabilities when the API is reachable (1.2s)
  ✓ 3 … renders the application shell (1.1s)
  ✓ 4 … reports either a health result or a clear connection error (1.1s)

  4 passed (19.0s)
```

**All four specs execute. 4 passed, 0 failed, 0 skipped.**

With the API deliberately down:

```
  -  2 … lists provider capabilities when the API is reachable
  ✓ 1 … reports either a health result or a clear connection error (1.1s)
  ✓ 3 … renders the application shell (1.1s)
  ✓ 4 … never renders a credential value (1.0s)

  1 skipped
  3 passed (17.5s)
```

The skip is by design and pre-existing: the spec calls `test.skip` when the API
is unreachable, because it cannot assert a provider listing it cannot fetch. Both
modes are reported so the skip is not hidden.

### One transient failure, recorded

On a later re-run of the same suite, Playwright's managed web server failed to
start once:

```
[WebServer] npm error command sh -c next start --port 3100
Error: Process from config.webServer was not able to start. Exit code: 1
```

The immediate retry passed 4/4, and `next start` on a spare port serves `200`
with `Ready in 5.4s`, so the server itself is sound. Port 3100 was free at the
time. Most likely a startup race shortly after `.next` was rewritten by a build,
on the same drvfs mount that produced the dangling-symlink fault above — **but
that is a hypothesis, not a diagnosis.** Recorded because an intermittently
failing web-server start would make the CI `e2e` job flaky, which is relevant to
M01-11's acceptance and should be watched on the first CI runs rather than
discovered later.

### CI reviewed

The job already resolved the right version — `working-directory: frontend` makes
`npx` prefer the local install. But `npx` silently downloads a different version
when it cannot resolve one, which is precisely how this failed locally. Made
explicit so it cannot:

```yaml
run: ./node_modules/.bin/playwright install --with-deps chromium
working-directory: frontend
```

`--with-deps` is retained, so CI still installs system libraries. The job starts
the API before the run, so CI exercises all four specs.

### Why this is not marked complete

M01-11's acceptance says "runs green locally **and in CI**" and "the CI `e2e` job
passes with real output". Locally: done, evidenced above. **In CI: not observed**
— running it needs a push, and pushing is not authorized. The third criterion
(removing TEST_STRATEGY's unverified note) is deliberately left until CI has
passed.

No system dependency was missing: the browser launched once the correct build was
present, so there is nothing to report under that heading.

---

## 4. M01-12 — BLOCKED

```
$ docker --version
/bin/bash: line 12: docker: command not found      (exit 127)

$ docker compose version
/bin/bash: line 14: docker: command not found      (exit 127)

$ docker compose config
/bin/bash: line 16: docker: command not found      (exit 127)

$ command -v docker
(no output)

$ ls -la /var/run/docker.sock
ls: cannot access '/var/run/docker.sock': No such file or directory
```

**Exact limitation:** the Docker CLI is not installed and there is no daemon
socket. Not a daemon that is merely stopped — there is no client binary and no
socket to connect to.

Per the brief, **no OS packages or daemon components were installed.** That needs
separate authorization. Unblocking requires either a Docker Engine install in
this container (privileged, needs authorization) or running M01-12 on a host that
has one.

**M01-12 stays BLOCKED. `docker-compose.yml` remains unverified, PostgreSQL 17
was not exercised, and accepted risk R-4 stands.** Integration tests continue to
run against the natively installed PostgreSQL 15.19.

---

## 5. M01-02 — stopped at a decision

### What the audit found

| Question                                   | Finding                                                                                                                                                           |
| :----------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Any authentication today?                  | **None.** No `AddAuthentication`, `UseAuthentication`, `ClaimsPrincipal` or `HttpContext.User` anywhere in `backend/src`                                          |
| Endpoints exposed?                         | Health and read-only provider diagnostics only — `HealthEndpoints.cs`, `ProviderEndpoints.cs`                                                                     |
| Does anything trust a client-supplied org? | **No.** `OrganizationId` does not appear in the API project at all. There is no header-trusting code to unwind                                                    |
| Is the data model ready?                   | Yes. `OrganizationId` is indexed on projects, assets and jobs; job idempotency is org-scoped; storage keys are org-prefixed; `ApprovedBy`/`WaivedBy` are recorded |
| Is the **mechanism** decided anywhere?     | **No.** This is the gap                                                                                                                                           |

S-19, S-20 and S-21 say authentication is "NOT IMPLEMENTED — M01". They say
nothing about _how_. There is no recorded identity provider, protocol, token
format or session model anywhere in the repository.

### This is a decision, and it is distinct from D-3

- **D-3 / A-01** decides the **role model** — whether Product Owner, UX Lead,
  Developer and Reviewer are four real roles. That is _authorization_, and it
  gates **M01-03**.
- The **authentication mechanism** decides how a request proves _who_ it is.
  That is _authentication_, and it gates **M01-02**.

They are independent: any of the options below works with one role or four.
Conflating them would let a role-model answer be mistaken for a mechanism
answer.

It also touches **D-5** (deployment target and secret store, currently M02): an
external identity provider cannot be chosen without knowing the hosting and
tenancy story, and option B below would pull part of D-5 forward.

### Options

Rewritten per M01-W1-003. The earlier table was imprecise in three ways that
mattered, corrected below.

#### Option A — Local Identity + cookie/BFF

- ASP.NET Core Identity backed by PostgreSQL.
- Server-issued **HttpOnly** cookie.
- Same-origin / BFF boundary.
- **Requires antiforgery protection on every state-changing request.**
- The application owns: password storage, password reset, lockout, account
  recovery, MFA policy, and **Data Protection key persistence** (without which
  cookies stop validating when the host restarts or scales out).

#### Option B — External OIDC confidential client + cookie/BFF

- Authorization Code flow handled **server-side**, as a confidential client.
- The browser receives an **HttpOnly application session cookie — not an access
  token.**
- The external provider owns login, MFA and password policy.
- Organization membership and authorization stay **local** to this application.
- Requires an **ADR approving an external identity dependency**, and pulls the
  identity-provider and secret-storage portion of **D-5 into M01**.

#### Option C — ASP.NET Core Identity API endpoints

- Supports **cookie sessions as well as** proprietary bearer tokens.
- Proprietary bearer tokens are intended for clients that **cannot use cookies**.
  They are **not** an OIDC/SSO replacement.
- If cookies are used, **the same antiforgery requirements as Option A apply.**

### Three overstatements corrected

| Earlier claim                                     | Why it was wrong                                                                                                                                                                                                                                                                                               |
| :------------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| B is "JWT bearer validation"                      | B is a **confidential client** doing Authorization Code server-side. The browser holds a session cookie; there is no bearer token in the browser, and no JWT validated there                                                                                                                                   |
| C "puts a token in browser-readable storage"      | C does **not** inherently do that. It offers cookie sessions too; the bearer-token mode is for non-cookie clients. The storage risk was presented as inherent when it is a deployment choice                                                                                                                   |
| Moving A → B "means rebuilding the session layer" | Not necessarily. If the session boundary and organization-membership abstraction are designed cleanly, swapping the _authentication_ front end does not require rebuilding every downstream consumer. Both A and B terminate in a server-side cookie session, which is precisely what makes the swap tractable |

Note what the first two corrections have in common: both A and B — and C in
cookie mode — end at the **same** place, a server-issued HttpOnly cookie. The
options differ in _who authenticates the user_, not in what the browser holds.
Framing B as "bearer tokens" obscured that, and made the A→B move look more
expensive than it is.

### Independent architecture recommendation

> **Recommend Option B: external OIDC confidential client with server-side
> cookie/BFF session and local organization membership. If an external identity
> provider cannot be authorized for M01, Option A is the fallback, but its
> password lifecycle, recovery, lockout, MFA policy, antiforgery and Data
> Protection requirements become explicit M01 scope.**

This supersedes the Option A recommendation in the first version of this report.
The earlier reasoning leaned on the no-external-service constraint and
under-counted what Option A actually obliges the team to build and keep correct —
password reset, lockout, recovery and MFA policy are each a security surface, and
Data Protection key persistence is the kind of omission that works in
development and fails on the second instance in production.

**The choice is not mine to make.** It is recorded as **D-7** and belongs to the
Product Owner together with the Solution Architect.

### Non-negotiables for whichever is chosen

- **`OrganizationId` comes from the authenticated principal**, resolved through a
  server-side membership lookup. Never from a header, query parameter or body
  field. S-20 is meaningless otherwise, and the storage-path and job-scoping
  guards already in place assume a trustworthy org id.
- **No mock or dev-only authentication will be presented as satisfying M01-02.**
  A development bypass that writes a fixed user is not authentication; if one is
  needed for local work it ships disabled, environment-gated, and M01-02 stays
  open until the real mechanism is in place.
- The cross-tenant isolation test in M01's acceptance criteria must fail if
  scoping is removed.

### Status

**M01-02 is stopped at this decision boundary, with no authentication code
written.** The choice is material: it determines the API's composition, the
session model, the frontend's auth flow, part of the data model, and — if B is
chosen — whether part of D-5 moves into M01.

Formalised as **D-7** (owner: Product Owner + Solution Architect; blocks M01-02;
status **Open**), recorded identically in `IMPLEMENTATION_PLAN.md` and
`IMPLEMENTATION_STATUS.md`. Resuming M01-02 needs D-7 answered.

---

## 6. Decisions still required from the Product Owner

| #       | Decision                                                                                                               | Blocks                                                                                      | Status            |
| :------ | :--------------------------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------ | :---------------- |
| **D-1** | A Stitch account with canvas access, and an API key                                                                    | Authenticated readiness, live canvas access, and **every live provider operation**          | ⛔ **Unresolved** |
| **D-2** | The approved live **design-operation** list: exact count, cap where measurable, one project + one generation initially | Every approved live **design operation** that is billable or **cost unknown**, and **A-09** | ⛔ **Unresolved** |
| **D-3** | Confirm or collapse the four-role model (A-01)                                                                         | **M01-03**                                                                                  | ⛔ **Unresolved** |
| **D-7** | **Authentication mechanism and identity provider** — see § 5. Owner: **Product Owner + Solution Architect**            | **M01-02**, and therefore M01-03 … M01-06                                                   | ⛔ **Open**       |

D-4 was resolved on 2026-10-21. **D-7 is new**, raised by this audit, and is
**not** a restatement of D-3: D-3 selects the authorization role model, D-7
selects the authentication mechanism. D-1, D-2, D-3, D-5 and D-6 are unchanged by
this correction.

---

## 7. Commands and outcomes

Every figure below is from this run.

```
$ dotnet build
    0 Warning(s)
    0 Error(s)

$ dotnet test
  total: 258
  failed: 0
  succeeded: 258
  skipped: 0

$ dotnet format --verify-no-changes
(no output; exit 0)

$ npm test
  frontend            Test Files  2 passed (2)    Tests  14 passed (14)
  packages/contracts  Test Files  3 passed (3)    Tests  164 passed (164)

$ npm run typecheck      exit 0, clean
$ npm run lint           exit 0, clean
$ npm run format:check   All matched files use Prettier code style!

$ npm run build --workspace frontend
✓ Compiled successfully in 6.3s

$ dotnet-ef migrations has-pending-model-changes \
    --project backend/src/AIDesignStudio.Infrastructure \
    --startup-project backend/src/AIDesignStudio.Api
No changes have been made to the model since the last migration.
```

**Totals: 436 passed, 0 failed, 0 skipped** — 185 unit, 16 architecture, 57
integration, 14 frontend, 164 contracts. Integration tests **passed, not
skipped**, against PostgreSQL 15.19.

Unchanged from M00 closure, which is the expected result: Wave 1 added no
production code. The E2E suite is counted separately and is not part of these 436.

```
$ npm run test:e2e --workspace frontend     # API running
  4 passed (19.0s)                          # 4 passed, 0 failed, 0 skipped

$ npm run test:e2e --workspace frontend     # API down
  1 skipped, 3 passed (17.5s)

$ docker --version / docker compose version / docker compose config
  command not found (exit 127) — BLOCKED, not counted as passed
```

---

## 8. Files changed

| File                                                               | Change                                                         |
| :----------------------------------------------------------------- | :------------------------------------------------------------- |
| `IMPLEMENTATION_PLAN.md`                                           | M01 In progress; Wave 1 task-state table; entry criteria       |
| `docs/05-delivery/IMPLEMENTATION_STATUS.md`                        | Current milestone M01; §1 row; rule-9 note                     |
| `README.md`                                                        | Status sentence; E2E section rewritten with the version caveat |
| `docs/05-delivery/M01_WAVE1_REPORT.md`                             | **New** — this report                                          |
| `backend/tests/AIDesignStudio.UnitTests/Fixtures/Stitch/README.md` | **New** — fixture locations and recording rules; no fixtures   |
| `frontend/e2e/status.spec.ts`                                      | Alert locator scoped to `main`; assertions unchanged           |
| `.github/workflows/ci.yml`                                         | Browser install uses the workspace binary, not `npx`           |

**No production source file was modified.** No `.cs` file, no React component,
no schema, no migration. `package-lock.json` is byte-identical.

---

## 9. Confirmations

- **No Stitch or provider call was made.** The only CLI invocations were
  `--version` and `--schema`, which SPIKE-01 verified run with no credentials and
  no network, from a tarball in `/tmp` outside the repository.
- **No spend.** No billable operation was attempted.
- **The billable guard is still `false`** — type default plus explicit `false` in
  both hosts, quoted in §2.
- **Nothing committed, pushed, deployed, or opened as a PR.** The repository
  still has no commits.
- **No work beyond Wave 1.** M01-03 … M01-10 untouched; no authentication code
  written; every dependency gate respected.
- **Nothing weakened.** No guard, test, acceptance criterion or tenant-isolation
  rule was relaxed. The one E2E change scopes a locator and leaves every
  assertion intact.
- **All seven ADRs and their amendments are preserved**, unmodified.
- **No new infrastructure or external service** was added.
- **No M01 task is self-approved.** M01-11 awaits its CI run; M01-12 is blocked;
  M01-01 is partially prepared with its live portion blocked; M01-02 is stopped
  at a decision.

---

**M01 Wave 1 complete as authorized. Four decisions await the Product Owner:
D-1, D-2, D-3, and the authentication mechanism.**
