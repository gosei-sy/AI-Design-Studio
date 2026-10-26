# Response to the M01 Wave 1 Review

**Decision under response:** CHANGES REQUIRED (1 P1, 2 P2) → (2 P2) → (1 P1, 1 P2) → (1 P1) → (1 P1 residual)
**Final decision:** ✅ **APPROVED** 2026-10-21 — independent verification found no remaining contradiction
**Response date:** 2026-10-21 · **Author:** Lead Developer
**Report under review:** [M01_WAVE1_REPORT.md](M01_WAVE1_REPORT.md)

---

## Summary

All three findings accepted. This correction is **documentation and
decision-analysis only** — no runtime source was changed, nothing was
implemented, and no provider was contacted.

W1-001 is the serious one, and the mistake has a recognisable shape. I verified
that `BillableOperationsEnabled` defaults to `false` and that idempotency keys
exist, then described both as if they protected the spike. They are real
controls; they are simply **enforced somewhere the spike never goes.** The error
was not about whether the guards work but about _where they sit_:

> **A control only protects the path that passes through it.** Verifying a guard
> exists is not the same as establishing that it is on the path you are about to
> take.

Worse, the advice I gave inverted the safe setting. I told the operator to enable
the guard for the paid steps — which would have protected nothing on the CLI path
while removing the only thing restraining the running application. The one
instruction that changed a safety-relevant setting made things strictly worse.

| Finding | Severity | Status       |
| :------ | :------- | :----------- |
| W1-001  | **P1**   | ✅ Corrected |
| W1-002  | P2       | ✅ Corrected |
| W1-003  | P2       | ✅ Corrected |

---

## W1-001 — The live Stitch cost-control procedure · **CORRECTED**

### Verified in source

| Control                     | Enforced in                                                                          | Protects a direct `stitch` command? |
| :-------------------------- | :----------------------------------------------------------------------------------- | :---------------------------------- |
| `BillableOperationsEnabled` | `StitchDesignProvider.EnsureBillableAllowed` — 3 call sites (`:196`, `:282`, `:372`) | **No**                              |
| Job idempotency keys        | `PostgresJobQueue.EnqueueAsync`, via `(organization_id, kind, key)`                  | **No**                              |

`StitchProcessCommandRunner` declares the option at `StitchCommandRunner.cs:28`
and **never reads it** — the check lives one layer above, in the adapter. A CLI
invocation starts at the process, so it passes through neither the adapter nor
the job queue. Recording fixtures bypasses the application completely.

Re-running a `stitch generate screen` command therefore charges again. There is
no dedupe on that path.

### The revised cost-control boundary

```
  Application path  (guarded)                 Direct CLI path  (UNGUARDED)
  ─────────────────────────────                ────────────────────────────
  HTTP request                                 operator's terminal
    -> PostgresJobQueue.EnqueueAsync             -> stitch ...
         [idempotency key dedupes]                    [no dedupe]
    -> StitchDesignProvider
         [EnsureBillableAllowed]  <-- the flag    [flag never read]
    -> StitchProcessCommandRunner              -> provider
    -> provider
```

Everything protecting the right-hand column is procedural.

### Changes made

1. **Stated explicitly** that direct CLI fixture recording sits outside the
   application billable guard _and_ outside job idempotency — in all three
   documents, with the enforcement locations named.
2. **Removed the instruction to enable the guard.** It now says the opposite:
   **keep it `false`** throughout, because enabling it protects nothing here and
   removes the application's own restraint.
3. **Removed idempotency keys** from the list of controls protecting the spike,
   and said plainly that a re-run charges again.
4. **Specified what D-2 must authorize**: an exact operation-count limit; a
   monetary cap _only where provider pricing exposes a measurable one_; initially
   one project and one screen generation; and an explicit stop before upload,
   apply-design-system and variants unless separately itemised.
5. **Operator confirmation required immediately before each controlled command**
   — billable or cost unknown alike. One command, one decision.
6. **Stop on any anomaly, never auto-retry a controlled command.** Noted why this is
   sharper than it sounds: the CLI exits 0 on failure (SPIKE-01), so a command
   that looks failed may still have been charged.
7. **"Cost unknown" replaces every unobserved free/low claim.** The old tables
   said create was "None observed", get "Low / none", step 4 "low/no cost" and
   step 8 free. None had been observed — those were assumptions written as
   findings, which is the failure mode `docs/06-spikes/README.md` exists to
   prevent.
8. **Clarified the narrow case for enabling the guard**: only for a separately
   authorized test running _through the application adapter_, configured as
   `Providers__Stitch__BillableOperationsEnabled=true` for that single run —
   **never** by editing `appsettings.json`, whose defaults are the fail-closed
   baseline for every environment including CI.

### No safety script

None was written, and the documents now say one should not be. It could not keep
its promise: the provider exposes no per-call price to the CLI, so a script
cannot know what it has spent. Naming something `safe-stitch.sh` would imply a
bound that does not exist. The authorized set is a **fixed command-count
checklist** — four commands initially — which is at least honest about being a
checklist.

### The initial authorized set

| #   | Command                  | Cost              |
| --- | :----------------------- | :---------------- |
| 1   | `stitch create project`  | **Cost unknown**  |
| 2   | `stitch generate screen` | **Yes, billable** |
| 3   | `stitch get screen`      | **Cost unknown**  |
| 4   | `stitch delete project`  | **Cost unknown**  |

---

## W1-002 — A-08/A-09 and blocker mapping · **CORRECTED**

### What I had wrong

I wrote that unverified response shapes were "the whole of A-09". A-09
(`OPEN_ASSUMPTIONS.md:150`) reads: _"Per-generation cost is acceptable at expected
volume."_ It is a question about **price**, which no response shape can answer.

### The corrected mapping

| Item         | What it actually asks                                                   |
| :----------- | :---------------------------------------------------------------------- |
| **A-08**     | Can `STITCH_API_KEY` be provisioned for unattended **server-side** use? |
| **A-09**     | Is the **observed per-generation cost** acceptable at expected volume?  |
| **SPIKE-02** | The response shapes — **technical evidence**, not an assumption         |

Both assumptions remain **open**. Recording a response shape closes neither; it
supplies part of the evidence SPIKE-02 step 9 needs.

### The corrected gate mapping

The old table claimed step 2 needed D-1 and that D-2 blocked "every fixture".
Both overstated the blocker and hid work that is already possible.

| Gate          | What it gates                                                                                                                                                                                           |
| :------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Neither**   | `--dry-run` (step 2) → `generate-screen.dry-run.json`. No credentials, no network. **Recordable today**                                                                                                 |
| **D-1 only**  | Authenticated readiness and live canvas access → `status.authenticated.json`                                                                                                                            |
| **D-2**       | Billable calls: `generate screen`; plus upload / apply-design-system / variants if itemised                                                                                                             |
| **D-1 + D-2** | `generate-screen.success.json`; and `get-screen.success.json`, which depends **operationally** on the screen created by the authorized generation even if retrieval itself is later observed to be free |
| **Both**      | M01-01's acceptance in full, therefore A-08; and therefore M01-07/08/09 and the complete M01-10                                                                                                         |

**Not every fixture depends on D-2** — the dry-run recording needs neither
decision and could be captured now.

---

## W1-003 — The authentication decision · **REWRITTEN**

### Three overstatements, corrected

| Earlier claim                                | Why it was wrong                                                                                                                                                                                                              |
| :------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| B is "JWT bearer validation"                 | B is a **confidential client** running Authorization Code **server-side**. The browser gets a session cookie; no bearer token reaches it and no JWT is validated there                                                        |
| C "puts a token in browser-readable storage" | C does **not** inherently do that. It supports cookie sessions as well; the bearer mode exists for clients that cannot use cookies. A deployment choice was presented as inherent                                             |
| A → B "means rebuilding the session layer"   | Not necessarily. Both terminate in a server-side cookie session, so with a clean session boundary and organization-membership abstraction the authentication front end can be swapped without rebuilding downstream consumers |

The first two share a root: **A, B, and C-in-cookie-mode all end at the same
place** — a server-issued HttpOnly cookie. They differ in _who authenticates the
user_, not in what the browser holds. Framing B as "bearer tokens" obscured that
and made the A→B move look more expensive than it is, which in turn skewed the
recommendation toward A.

### The corrected option table

|       | Option                                                                                 | Session the browser holds                                                  | Who owns login / MFA / password policy | Notable obligations                                                                                                                                                         |
| :---- | :------------------------------------------------------------------------------------- | :------------------------------------------------------------------------- | :------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **A** | **Local Identity + cookie/BFF** — ASP.NET Core Identity on PostgreSQL, same-origin/BFF | Server-issued **HttpOnly cookie**                                          | **This application**                   | **Antiforgery on all state-changing requests**; password storage, reset, lockout, account recovery, MFA policy; **Data Protection key persistence**                         |
| **B** | **External OIDC confidential client + cookie/BFF** — Authorization Code server-side    | Server-issued **HttpOnly application session cookie, not an access token** | **External provider**                  | **ADR approving an external identity dependency**; pulls the identity-provider/secret portion of **D-5 into M01**. Organization membership and authorization stay **local** |
| **C** | **ASP.NET Core Identity API endpoints**                                                | Cookie session **or** proprietary bearer token                             | This application                       | Bearer tokens are for clients that **cannot use cookies** — **not** an OIDC/SSO replacement. In cookie mode, **the same antiforgery requirements as A**                     |

### Independent architecture recommendation

> **Recommend Option B: external OIDC confidential client with server-side
> cookie/BFF session and local organization membership. If an external identity
> provider cannot be authorized for M01, Option A is the fallback, but its
> password lifecycle, recovery, lockout, MFA policy, antiforgery and Data
> Protection requirements become explicit M01 scope.**

This **supersedes** the Option A recommendation in the first version of the Wave
1 report. That reasoning leaned on the no-external-service constraint and
under-counted what A obliges the team to build and keep correct. Password reset,
lockout, recovery and MFA policy are each a security surface; Data Protection key
persistence is the kind of omission that works in development and fails on the
second instance in production.

**The option is not selected on the Product Owner's behalf.** It is recorded as
D-7 and awaits their decision with the Solution Architect.

### D-7

| Field                 | Value                                                                                                                                                      |
| :-------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Decision**          | Authentication mechanism and identity provider                                                                                                             |
| **Owner**             | **Product Owner + Solution Architect**                                                                                                                     |
| **Blocks**            | **M01-02** (and therefore M01-03 … M01-06, which queue behind it)                                                                                          |
| **Status**            | **Open**                                                                                                                                                   |
| **Distinct from D-3** | D-3 selects the authorization **role model**; D-7 selects how a request proves **who** it is. Any answer to one is compatible with any answer to the other |

Recorded identically in `IMPLEMENTATION_PLAN.md`,
`docs/05-delivery/IMPLEMENTATION_STATUS.md` and
`docs/05-delivery/M01_WAVE1_REPORT.md`.

**D-1, D-2, D-3, D-5 and D-6 statuses are unchanged** by this correction.

---

## Documents corrected

| File                                                               | Corrections                                                                                                                                                                                                                                                                           |
| :----------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `docs/06-spikes/SPIKE-02-stitch-screen-retrieval.md`               | Cost-control section rewritten: what does _not_ protect the spike, what does, the D-2 budget, operator rules, no-safety-script. Step headers' cost claims corrected to "cost unknown"; step 2 marked as needing neither D-1 nor D-2                                                   |
| `backend/tests/AIDesignStudio.UnitTests/Fixtures/Stitch/README.md` | Fixture table gains a **Needs** column with the real D-1/D-2 mapping; guard section rewritten to say it protects nothing here and must stay `false`; idempotency claim removed; A-08/A-09 vs SPIKE-02 evidence separated                                                              |
| `docs/05-delivery/M01_WAVE1_REPORT.md`                             | Guard instruction reversed and explained; D-2 budget and operator rules added; "whole of A-09" corrected; gate-mapping table rewritten; §5 authentication section fully rewritten with the corrected options, the three overstatements, and the Option B recommendation; D-7 recorded |
| `IMPLEMENTATION_PLAN.md`                                           | D-7 row in the decisions table; D-7 note; Wave 1 table M01-02 row now names D-7                                                                                                                                                                                                       |
| `docs/05-delivery/IMPLEMENTATION_STATUS.md`                        | D-7 row and a full D-7 subsection; §1 M01 row names D-7                                                                                                                                                                                                                               |
| `docs/05-delivery/M01_WAVE1_REVIEW_RESPONSE.md`                    | **New** — this response                                                                                                                                                                                                                                                               |

---

## Preserved Wave 1 results

Unchanged, as required:

- **M01 is In progress.**
- **M01-11 local evidence:** API up → **4/4 passed**; API down → **3 passed, 1
  skipped**.
- **M01-11 is not complete** until CI runs.
- **M01-12 remains BLOCKED** — Docker unavailable.
- **436 tests remain the last measured baseline** — 185 unit, 16 architecture,
  57 integration, 14 frontend, 164 contracts.
- **No M01 task is complete.**

---

## Remaining Product Owner decisions

| #       | Decision                                                                                                                                                          | Blocks                                                                                                                                    | Owner                                  |
| :------ | :---------------------------------------------------------------------------------------------------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------- | :------------------------------------- |
| **D-1** | A Stitch account with canvas access, and an API key                                                                                                               | M01-01 live execution                                                                                                                     | Product Owner                          |
| **D-2** | The authorized **controlled-command** list: exact count, cap where measurable, one project + one generation initially, explicit stop before upload/apply/variants | **Every authorized live design operation that is billable or cost unknown**, and A-09. Readiness probes are excluded — they need D-1 only | Product Owner                          |
| **D-3** | Confirm or collapse the four-role model (A-01)                                                                                                                    | M01-03                                                                                                                                    | Product Owner                          |
| **D-7** | **Authentication mechanism and identity provider**                                                                                                                | **M01-02**                                                                                                                                | **Product Owner + Solution Architect** |

---

## Round 2 — W1-RR-001 and W1-RR-002 · **CORRECTED**

**Decision under response:** CHANGES REQUIRED — documentation-only reconciliation
**Date:** 2026-10-21

Both accepted. Both are the same failure, and it is a failure of _completeness_,
not of understanding: round 1 added correct new sections and left the older prose
above them in place. The documents then asserted the right classification in one
section and the old one in another — which is worse than the original error,
because a reader has no way to tell which paragraph is current.

The lesson, and it is the second time this project has produced it (the round-2
M00 patch script had the same shape): **a correction is not applied until every
statement of the old claim is gone.** Adding the right answer next to the wrong
one leaves the wrong one in force.

### W1-RR-001 — cost classification

| Location                  | Was                                          | Now                                                                                    |
| :------------------------ | :------------------------------------------- | :------------------------------------------------------------------------------------- |
| `M01_WAVE1_REPORT.md:52`  | "Steps 1–2 are free"                         | Replaced with the single classification table below                                    |
| `M01_WAVE1_REPORT.md:112` | "Everything from step 3 onward spends money" | Generation billable; create/get/delete cost unknown and must be in D-2's approved list |
| `M01_WAVE1_REPORT.md:118` | "Step 1 — authenticate (free)"               | "requires D-1; cost unknown — no generation occurs"                                    |
| `M01_WAVE1_REPORT.md:124` | "(free, no network)"                         | "(no cost, no network; needs neither D-1 nor D-2)"                                     |
| `SPIKE-02:75`             | "**Steps 3 onward incur cost.**"             | Full classification paragraph, pointing at the cost-control section                    |
| `SPIKE-02:181`            | "inspect the payload (free)"                 | Clarified: this is **our own API**, entirely local, no provider call                   |
| `SPIKE-02` steps 5/6/7    | "(⚠ cost)"                                   | "(⚠ billable — **STOP** unless D-2 itemises it)"                                       |

The classification as round 2 left it — **superseded by round 3 below**, which
marks upload/apply/variants cost unknown rather than merely "stop", and notes that
generation's _amount_ is unobserved:

| Operation                 | Gate                    | Cost                                              |
| :------------------------ | :---------------------- | :------------------------------------------------ |
| `--dry-run`               | **Neither D-1 nor D-2** | **No cost** — no credentials, no network          |
| Authenticated status      | **D-1**                 | **Cost unknown**                                  |
| Create / get / delete     | D-1 + D-2               | **Cost unknown** — must be in D-2's approved list |
| Generation                | D-1 + D-2               | **Billable**                                      |
| Upload / apply / variants | D-1 + D-2               | **Stop** unless separately itemised by D-2        |

`--dry-run` is the **only** operation established as free, and only because
SPIKE-01 verified it makes no network call. Everything else unobserved is "cost
unknown".

### W1-RR-002 — the §6 blocker table

That table had never been touched in round 1 — I corrected the §2 gate mapping
and left the summary table at the end of the report stating the old one.

The "Now" column records the wording **as round 2 left it**; round 4 later narrowed "live command" to "live **design operation**".

| Row | Was                                              | Now (round 2; superseded by round 4)                                               |
| :-- | :----------------------------------------------- | :--------------------------------------------------------------------------------- |
| D-1 | "M01-01 steps 1–2, and everything downstream"    | Authenticated readiness, live canvas access, and **every live provider operation** |
| D-2 | "M01-01 steps 3–7, **every real fixture**, A-09" | Every approved live command that is billable or **cost unknown**, and **A-09**     |

"Every real fixture" is gone. It contradicted both the §2 mapping and the fixture
README, which correctly record that `generate-screen.dry-run.json` needs neither
decision and `status.authenticated.json` needs D-1 alone.

D-1's row also no longer says "steps 1–2": step 2 is the dry-run and needs no
credential.

### Unchanged

W1-003 and D-7 were accepted as correct and were not modified. D-1, D-2, D-3,
D-5 and D-6 statuses are untouched. The Wave 1 conclusions stand: M01 In
progress; M01-11 4/4 with the API up and 3+1 skipped with it down, not complete
until CI; M01-12 BLOCKED; **436 remains the last measured baseline**; no M01 task
complete.

---

## Round 3 — W1-RR2-001 and W1-RR2-002 · **CORRECTED**

**Decision under response:** CHANGES REQUIRED (1 P1, 1 P2)
**Date:** 2026-10-21

### W1-RR2-001 — the controls did not cover cost-unknown commands

Accepted, and this is the most instructive finding in the sequence, because
**my own previous fix created it.**

Round 2 reclassified create / get / delete from "free" to **cost unknown** —
correct, and more honest. But every control sentence was keyed to "paid command"
or "billable command". Read literally, the three commands I had just made honest
about were no longer counted, no longer confirmed before execution, and no longer
covered by the no-retry rule.

> **Widening a risk category without widening the controls that name it moves
> work out of the protected set.** The label became more truthful and the
> procedure became less safe.

That is a worse failure mode than the original overclaim, because it is invisible:
every individual sentence was defensible, and the gap existed only in the
relationship between them.

**The fix is a single named category**, so the two can no longer drift. As round
3 worded it — **superseded by round 4**, which narrows "provider command" to
"design operation":

> Every billable _or cost-unknown_ **live provider command** must be
> pre-authorized, counted, confirmed immediately before execution, and never
> automatically retried.

These are the **controlled commands**. Every control now references that term
rather than "paid" or "billable":

| Site                      | Was                                    | Now                                                                  |
| :------------------------ | :------------------------------------- | :------------------------------------------------------------------- |
| `SPIKE-02:259`            | "counted list of paid commands"        | "counted list of **controlled commands**"                            |
| `SPIKE-02:260`            | "before **each** paid command"         | "before **each controlled command**"                                 |
| `SPIKE-02:261`            | "auto-retry a paid command"            | "auto-retry a **controlled command**"                                |
| `SPIKE-02:270`            | "the number of billable commands"      | "the number of **controlled commands** (billable _or_ cost unknown)" |
| `SPIKE-02:293`            | "Operator rules for each paid command" | "for each **controlled command**"                                    |
| `SPIKE-02:299`            | "Never re-run a paid command"          | "Never re-run a **controlled command**"                              |
| `REPORT:188`              | "a number of billable commands"        | "**controlled commands**, billable _or_ cost unknown"                |
| `REPORT:195`              | "before each paid command"             | "before each **controlled command**"                                 |
| `REPORT:197`              | "retry a paid command"                 | "retry a **controlled command**"                                     |
| `Fixtures README:116,118` | "paid commands" / "a paid command"     | the canonical sentence + **controlled commands**                     |
| `RESPONSE:85,87`          | "each paid command" / "a paid command" | "**controlled command**"                                             |

Delete-the-project is now marked "Step 8 — **itself controlled**", since its cost
is also unobserved.

### Unevidenced billable labels removed

The same rule cuts the other way: operations never executed must not be asserted
billable either.

| Site                    | Was                    | Now                                                    |
| :---------------------- | :--------------------- | :----------------------------------------------------- |
| `SPIKE-02:169` (step 5) | "⚠ billable"           | "**cost unknown** — ⚠ **STOP** unless D-2 itemises it" |
| `SPIKE-02:179` (step 6) | "⚠ billable"           | "**cost unknown** — ⚠ **STOP** unless D-2 itemises it" |
| `SPIKE-02:197` (step 7) | "⚠ billable"           | "**cost unknown** — ⚠ **STOP** unless D-2 itemises it" |
| `Fixtures README:50,51` | "⚠ Incurs cost"        | "**Cost unknown**"                                     |
| `REPORT` cost table     | "Stop unless itemised" | "**Cost unknown — STOP** unless itemised"              |

**`generate screen` remains the only operation labelled billable** — and even
there the _amount_ is unobserved, which is exactly what A-09 asks. Noted inline
so the label is not read as a price.

### W1-RR2-002 — remaining old mapping

"Now" records round 3's wording. Round 4 narrowed its category again — see below.

| Site           | Was                                              | Now (round 3; narrowed in round 4)                                                                                                                                                                                   |
| :------------- | :----------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `REPORT:123`   | One heading: "Commands to execute after D-1/D-2" | **Three gated headings**: _needs neither_ (dry-run, runnable today) · _needs D-1 only_ (authenticated readiness) · _needs D-1 **and** D-2_ (the controlled commands) — plus an explicit **STOP** block for steps 5–7 |
| `RESPONSE:251` | D-2 blocks "Every billable call"                 | "**Every authorized live command that is billable or cost unknown**", and A-09                                                                                                                                       |

The per-gate split also makes the dry-run's availability visible: it is the one
thing that can be recorded today, and the single-heading version hid that.

### Unchanged

W1-003 and D-7 remain accepted and untouched. D-1, D-2, D-3, D-5, D-6 statuses
unchanged. M01 In progress; M01-11 4/4 with the API up, 3+1 skipped with it down,
not complete until CI; M01-12 BLOCKED; **436 remains the last measured
baseline**; no M01 task complete.

---

## Round 4 — W1-RR3-001 · **CORRECTED**

**Decision under response:** CHANGES REQUIRED (1 P1)
**Date:** 2026-10-21

Accepted. The category I introduced in round 3 to close one hole was **drawn too
wide**, and it created a contradiction of its own.

The rule said _every billable or cost-unknown **live provider command**_ is
controlled and therefore needs D-2. But `stitch status` is a live provider
command whose cost is unobserved — so the rule swept it in, while the same
documents correctly mapped `status.authenticated.json` to **D-1 only**. One
operation, two incompatible authorizations, asserted on the same page.

> **A category defined by the wrong attribute is a bug even when every member
> label is right.** Round 3 widened the category to cover the commands that spend
> money; it widened it by _access mechanism_ ("provider command") when the thing
> that actually matters is _what the operation does_ ("design operation").

This is the third variation on one theme in this review sequence — round 1 put a
control on the wrong **path**, round 3 keyed controls to the wrong **cost label**,
and round 3's fix scoped the category by the wrong **attribute**. The lesson that
survives all three: **state precisely which set a rule governs, then check every
member of that set against it.**

### The correction

Canonical rule, now narrowed in all four documents:

> **Every billable _or cost-unknown_ live design operation must be
> pre-authorized, counted, confirmed immediately before execution, and never
> automatically retried.**

With the scope defined explicitly alongside it:

| Operation                                     | Classification                      | Gate        | Counted against D-2? |
| :-------------------------------------------- | :---------------------------------- | :---------- | :------------------- |
| `--dry-run`, `--schema`                       | Local, no network                   | **Neither** | No                   |
| `stitch status`, our readiness endpoint       | **D-1-only readiness probe**        | **D-1**     | **No**               |
| `create` / `generate` / `get` / `delete`      | **Live design operation**           | D-1 + D-2   | **Yes**              |
| `upload` / `apply-design-system` / `variants` | **Live design operation**, optional | D-1 + D-2   | **Yes**, if itemised |

A readiness probe proves a credential works and **touches no design**. That is
the distinction, and it is why it can sit behind D-1 alone without weakening
anything.

**It is not a loophole.** Readiness cost is still unobserved, so the stop rule
still applies: **if readiness returns any unexpected billing behaviour, stop
immediately.** What the narrowing changes is authorization and counting — not the
obligation to stop.

### Sites corrected

| Site                    | Change                                                                                                                                                       |
| :---------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `REPORT:219`            | Canonical sentence → "live design operation", plus the scope definition                                                                                      |
| `REPORT:141`            | Heading "Needs D-1 only — authenticated readiness, **no generation**" → "**readiness probe, not a design operation**"                                        |
| `REPORT:147`            | **Deleted** "Cost unknown, so these are controlled commands: counted and confirmed." Replaced with an explicit not-counted statement plus the stop condition |
| `REPORT` cost table     | Status row marked "readiness probe, **not** a design operation — not in D-2's count"; create/get/delete marked "design operations"                           |
| `SPIKE-02:265`          | Canonical sentence → "live design operation", plus the scope definition                                                                                      |
| `SPIKE-02:88`           | "Everything else … both categories are subject to the same controls" → scoped to design operations, with the readiness exception named                       |
| `SPIKE-02:82`           | Status row marked a readiness probe, not counted against D-2                                                                                                 |
| `Fixtures README:119`   | Canonical sentence → "live design operation", plus the scope definition                                                                                      |
| `Fixtures README` table | Footnote ³ added for `status.authenticated.json`: readiness probe, D-1 only, not counted, stop on unexpected billing                                         |
| `RESPONSE:354`          | Canonical sentence → "live design operation"                                                                                                                 |
| `RESPONSE` D-2 row      | "every authorized live **design operation** that is billable or cost unknown", readiness probes excluded                                                     |

### Unchanged

The rest of the RR2 corrections stand, as the review confirms. W1-003 and D-7
remain accepted. D-1, D-2, D-3, D-5, D-6 statuses unchanged. `generate screen`
remains the only operation labelled billable. M01 In progress; M01-11 4/4 with
the API up and 3+1 skipped with it down, not complete until CI; M01-12 BLOCKED;
**436 remains the last measured baseline**; no M01 task complete.

---

## Round 5 — W1-RR3-001 residual · **CORRECTED**

**Decision under response:** CHANGES REQUIRED (1 P1, residual)
**Date:** 2026-10-21

Accepted. Two summary rows in the report still used "live command", which pulls
the cost-unknown readiness probe back under D-2 — contradicting the narrowed
definition and the D-1-only row directly above it.

| Site         | Was                                                                       | Now                                                                                         |
| :----------- | :------------------------------------------------------------------------ | :------------------------------------------------------------------------------------------ |
| `REPORT:299` | "Every approved live **command** that is billable or cost unknown"        | "Every approved live **design operation** that is billable or cost unknown"                 |
| `REPORT:622` | "The approved live-**command** list" / "Every approved live **command**…" | "The approved live **design-operation** list" / "Every approved live **design operation**…" |

`REPORT:621`'s D-1 row is left as "every live **provider** operation" on purpose:
D-1 genuinely does gate everything live, readiness probes included. Only D-2's
scope needed narrowing.

### Why my round-4 verification missed it

My sweep grepped for the exact superseded string — `"live provider command"` —
and these rows said `"live command"`. I was searching for **the words I had
written**, not for **every phrasing of the claim**. A phrasing I never used was
invisible to a check keyed to my own vocabulary.

> **Verifying a correction by grepping the old wording only finds the instances
> that used it.** The check has to be framed by the _claim_ — "does anything here
> put a readiness probe under D-2?" — not by the string.

This round's sweep was framed that way instead: `live command|live-command|live
provider command|live provider operation|provider command|approved live`. That
pattern finds the two rows the previous one could not, and it is recorded here so
the next correction uses the broader form.

Fourth variation on the same theme, and the sequence is worth stating plainly:

| Round | What was scoped wrongly                                                                |
| :---- | :------------------------------------------------------------------------------------- |
| 1     | The **path** — a guard that did not sit on the CLI route                               |
| 3     | The **cost label** — controls keyed to "paid" after relabelling to "cost unknown"      |
| 4     | The **attribute** — category drawn by access mechanism, not by what the operation does |
| 5     | The **verification** — checked for one phrasing of the claim, not the claim            |

### Also marked

Two historical "Now" columns in the round-2 and round-3 records still read as
current state. Headed "Now (round 2; superseded by round 4)" and "Now (round 3;
narrowed in round 4)", with a line above each. Round 3's quotation of the wide
canonical sentence stays as the review confirmed — it is already labelled
superseded.

### Unchanged

Everything else stands. W1-003 and D-7 accepted. D-1, D-2, D-3, D-5, D-6
statuses untouched. `generate screen` remains the only operation labelled
billable. M01 In progress; M01-11 4/4 with the API up and 3+1 skipped with it
down, not complete until CI; M01-12 BLOCKED; **436 remains the last measured
baseline**; no M01 task complete.

---

## Verification

```
$ npm run format:check
Checking formatting...
All matched files use Prettier code style!
```

**No test suite was re-run, deliberately.** This correction changed no runtime
source, so re-measuring and restating a count would imply a revalidation that did
not happen. The last measured baseline stands as the independent review executed
it: **436 passed, 0 failed, 0 skipped.**

---

## Confirmations

- **No provider call.** No Stitch invocation of any kind during this correction.
- **No spend.** `BillableOperationsEnabled` remains `false` in both
  `appsettings.json` files, and the documents now instruct that it stay so.
- **No authentication implemented.** M01-02 remains stopped at D-7, with no
  authentication code written.
- **No source-code change.** No `.cs`, `.ts`, `.tsx`, schema, migration or
  configuration file was modified. Markdown only.
- **No infrastructure installed.** No OS package, no Docker, no external service.
- **Nothing committed, pushed or deployed.** No PR.
- **No later M01 task started.** M01-03 … M01-10 untouched; every dependency gate
  intact.
- **No fabricated fixtures.** `Fixtures/Stitch/` still contains only its README.
- **A-08 and A-09 remain open.** Neither is claimed resolved.
- **All seven ADRs and their amendments are preserved**, unmodified.

---

**M01 Wave 1 corrections applied. Live Stitch execution remains blocked on
D-1/D-2; M01-02 remains blocked on D-7; M01-03 remains blocked on D-3.**

---

## Outcome — APPROVED 2026-10-21

Independent verification confirmed:

- Readiness probes: **D-1 only**, outside D-2 counting.
- Live design operations: **D-1 + D-2**, controlled.
- Historical incorrect wording clearly marked superseded.
- Both report summary rows use "live design operation".

Gate executed by the reviewer: `npm run format:check` → clean, exit 0. No build
or test suite was run, and **436 remains the prior baseline, not a new
measurement.**

Five rounds, and the whole sequence was one error repeated at different scopes:
**path** (round 1), **cost label** (round 3), **attribute** (round 4),
**verification** (round 5). Each fix was locally right and globally incomplete,
because I checked the thing I had just changed rather than the claim across every
document that asserts it. The durable takeaway for the live run — and for whoever
executes SPIKE-02 — is in § "Cost control": the controls are procedural, the
application guard does not reach the CLI, and `BillableOperationsEnabled` stays
`false`.

**This approval covers the Wave 1 corrections only.** No M01 task is complete.
