# SPIKE-02: Generating or retrieving a screen through Stitch

**Status:** ⛔ **UNVERIFIED — no credentials available**
**Date:** 2026-10-20 · **Subject:** `@google/stitch` 0.11.1

> This integration is **not verified**. No live Stitch call has been made. The
> adapter code exists and its request shaping is tested, but no response mapping
> has been confirmed against a real account. Do not describe screen generation
> as working.

---

## Why it could not be verified

1. **No Stitch account or API key.** Canvas operations (projects, screens,
   design systems) require `STITCH_API_KEY`, which must be provisioned out of
   band (SPIKE-01, Finding 2).
2. **OAuth cannot be completed headlessly.** `stitch login` opens a browser and
   listens on loopback port 8085. This environment has neither an interactive
   browser session nor a user to consent.
3. **Making a live call would incur cost** without authorization. The brief
   forbids external spending, and `BillableOperationsEnabled` defaults to
   `false` for the same reason.

---

## What _was_ verified without credentials

| Verified                              | How                                                                                                           |
| :------------------------------------ | :------------------------------------------------------------------------------------------------------------ |
| Request shaping for `generate screen` | `--dry-run` returned the ordered tool calls `create_project` → `generate_screen_from_text` with our arguments |
| Argument validation                   | A request targeting neither an existing nor a new project is rejected before any call                         |
| Device mapping is lossless            | All four `TargetDevice` values exist in the provider schema                                                   |
| Failure classification                | `MISSING_CANVAS_CREDENTIALS` observed live and classified non-retryable                                       |
| The cost guard holds                  | `BILLABLE_OPERATIONS_DISABLED` raised for every billable method                                               |
| Envelope parsing                      | Tested against verbatim recorded CLI output                                                                   |

So: everything up to the network boundary is verified. Everything past it is not.

---

## Specifically unverified

| Assumption in the adapter                            | Risk if wrong                                                     |
| :--------------------------------------------------- | :---------------------------------------------------------------- |
| `generate screen` success returns `data.id`          | `CLI_MALFORMED_RESPONSE` on first live run; field name corrected  |
| …returns `data.project`                              | Falls back to the request's project ref; may be wrong             |
| …returns `data.canvasUrl`                            | Deep link null; cosmetic                                          |
| `get screen` exposes markup under `html` or `markup` | Markup null — matters for conformance checking (post-MVP)         |
| …exposes a render under `imageUrl` or `renderUrl`    | Preview unavailable                                               |
| `upload screen` returns the same envelope            | Import mapping wrong                                              |
| Generation is synchronous within the CLI call        | If it returns a pending handle, the job handler needs a poll loop |
| `--timeout 180s` is sufficient                       | `CLI_TIMEOUT`, retried — mechanism already verified               |

The last one is worth noting: if generation turns out to be asynchronous
provider-side, the job handler gains a polling stage. The durable job mechanism
already supports this (`Heartbeat` extends the lease), so it is a handler change,
not an architecture change.

### Mitigation already in place

- `IScreenVariantCapability` is **not declared**, because its response mapping is
  entirely unverified. Callers asking for it get `null`, not a broken call.
  (M01-07.)
- All response field reads are null-tolerant and raise
  `CLI_MALFORMED_RESPONSE` with the missing field named.
- Field-name assumptions are isolated in `StitchDesignProvider`; correcting one
  is a one-line change.

---

## Reproducible test plan

Run this once credentials exist. Each step states its expected result and the
code it validates.

**Cost, stated accurately** (corrected per W1-RR-001, then W1-RR2-001):

| Operation                                                                   | Gate                    | Cost                                                   |
| :-------------------------------------------------------------------------- | :---------------------- | :----------------------------------------------------- |
| `--dry-run` (step 2)                                                        | **Neither D-1 nor D-2** | **No cost** — no credentials, no network               |
| Authenticated status (step 1) — readiness probe, **not** a design operation | **D-1**                 | **Cost unknown** — not counted against D-2             |
| Create / get / delete (3, 4, 8) — design operations                         | D-1 + D-2               | **Cost unknown**                                       |
| Generation (step 3)                                                         | D-1 + D-2               | **Billable** (amount unobserved — that is A-09)        |
| Upload / apply / variants (5–7)                                             | D-1 + D-2               | **Cost unknown — STOP** unless D-2 itemises separately |

Only `--dry-run` is established as free, and only because SPIKE-01 verified it
makes no network call.

**Every billable or cost-unknown _design operation_ is subject to the same
controls**, regardless of which of the two it is. The authenticated status check is
the one deliberate exception: it is a D-1-only **readiness probe**, not a design
operation, so it is not counted against D-2's budget — though an unexpected
billing response from it is still a stop condition. See § "Cost control for
whoever runs this".

### Prerequisites

```bash
npm install -g @google/stitch        # or npx @google/stitch
stitch --version                     # expect 0.11.1 or later
```

Obtain a Stitch account with canvas access and an API key.

### Step 1 — Authenticate (requires D-1; cost unknown but no generation occurs)

```bash
stitch login                         # interactive; browser + loopback
export STITCH_API_KEY=<key>
stitch status --json
```

**Expect:** `authenticated: true` and `canvas.present: true`.

**Validates:** `StitchDesignProvider.CheckReadinessAsync`. Then confirm through
our API:

```bash
curl -s localhost:5080/api/v1/providers/google-stitch/readiness
# expect isUsable: true, missingCredentialNames: []
```

### Step 2 — Confirm the dry-run plan still matches (no cost; needs neither D-1 nor D-2)

```bash
stitch generate screen --new-project "AIDS Spike" \
  --prompt "Login screen with email and password" \
  --device MOBILE --dry-run --json
```

**Expect:** the same `requests[]` shape recorded in SPIKE-01. A change here means
the provider's surface moved and the adapter needs re-verifying.

`--dry-run` runs with **no credentials and no network** (SPIKE-01), so this step
needs neither D-1 nor D-2 and may be executed offline today. It was in fact
re-executed during M01-01 offline preparation, which is how the request surface
was confirmed unchanged.

### Step 3 — Generate one screen (⚠ **BILLABLE** — requires D-1 **and** D-2)

```bash
stitch create project --title "AIDS Spike" --json
# capture data.id as PROJECT_ID

stitch generate screen --project "$PROJECT_ID" \
  --prompt "Login screen with email and password" \
  --device MOBILE --title "Login" --json > /tmp/generate.json
cat /tmp/generate.json
```

**Record:** the exact keys of `data`. This is the primary unknown.

**Then check each assumption:**

| Assumption                            | Check                                        |
| :------------------------------------ | :------------------------------------------- |
| `data.id` exists and is the screen id | `jq -e '.data.id'`                           |
| `data.project` exists                 | `jq -e '.data.project'`                      |
| `data.canvasUrl` exists               | `jq -e '.data.canvasUrl'`                    |
| The call is synchronous               | Did it return only when generation finished? |

**Validates:** `GenerateScreenAsync`. Update the field reads if any differ, and
add the recorded output as a test fixture so it is covered from then on.

### Step 4 — Retrieve the screen (**cost unknown**; depends on step 3 having run)

```bash
stitch get screen "$SCREEN_ID" --project "$PROJECT_ID" --json > /tmp/get.json
jq 'keys' /tmp/get.json
jq -r '.data | keys' /tmp/get.json
```

**Record:** whether markup appears, and under which key. Check `html`, `markup`,
`content`, `code`.

**Validates:** `GetScreenAsync`. Note: a third-party adapter
(`@design-parity/adapter-stitch`) describes fetching "HTML+Tailwind through the
Stitch SDK", which suggests markup is available — **but that is third-party
evidence, not verification.**

### Step 5 — Upload a screen (**cost unknown** — ⚠ **STOP** unless D-2 itemises it)

```bash
printf '<!doctype html><html><body><h1>Checkout</h1></body></html>' > /tmp/probe.html
stitch upload screen /tmp/probe.html --project "$PROJECT_ID" \
  --route /checkout --title Checkout --json
```

**Validates:** `ImportScreenAsync`.

### Step 6 — Apply a design system (**cost unknown** — ⚠ **STOP** unless D-2 itemises it)

Project our design system first and inspect the payload. This is **our own API**, entirely local — no provider call, so no provider cost:

```bash
curl -s -X POST localhost:5080/api/v1/projects/{id}/design-system/projections/google-stitch \
  | jq '{theme: .nativeThemeJson, warnings: .warnings}'
```

Then apply it and confirm the provider accepts the payload:

```bash
stitch edit design-system --project "$PROJECT_ID" --json '<nativeThemeJson>'
```

**Validates:** `ApplyDesignSystemAsync` and the projection's correctness against
the live schema.

### Step 7 — Variants (**cost unknown** — ⚠ **STOP** unless D-2 itemises it; blocked capability)

```bash
stitch generate variants --project "$PROJECT_ID" --screen "$SCREEN_ID" \
  --prompt "Warmer colours, denser layout" --count 2 \
  --creative-range EXPLORE --aspects LAYOUT,COLOR_SCHEME --json
```

**Record** the response shape, then implement `IScreenVariantCapability` and add
it to `Capabilities`. **Until this step is done, the capability stays
undeclared.** (M01-07.)

### Step 8 — Clean up (**cost unknown**)

```bash
stitch delete project --id "$PROJECT_ID" --yes
```

### Step 9 — Record the findings

1. Update this file: change status to Verified, with the recorded shapes.
2. Add each recorded stdout as a fixture in `StitchAdapterTests.cs`.
3. Correct any field-name assumption in `StitchDesignProvider`.
4. Record observed cost and latency per call, for A-09 and quota design.
5. Resolve or escalate **A-08** in `OPEN_ASSUMPTIONS.md`.

---

## Cost control for whoever runs this

### What does **not** protect this spike

This had to be corrected (M01-W1-001). The two controls previously listed here
are real, but **neither applies to a `stitch ...` command typed in a terminal**:

| Control                     | Where it is enforced                                                           | Protects direct CLI? |
| :-------------------------- | :----------------------------------------------------------------------------- | :------------------- |
| `BillableOperationsEnabled` | `StitchDesignProvider.EnsureBillableAllowed` — 3 call sites inside the adapter | **No**               |
| Job idempotency keys        | `PostgresJobQueue.EnqueueAsync`, via the `(organization_id, kind, key)` index  | **No**               |

`StitchProcessCommandRunner` declares the option but never checks it; the check
lives one layer up, in the adapter. A direct CLI invocation starts at the
process, so it passes through neither the adapter nor the job queue. **Running
this spike bypasses the application entirely.**

Two consequences follow, and the second is the one that matters:

- Setting `BillableOperationsEnabled = true` **gives this spike no protection
  whatsoever**, because nothing in the CLI path reads it.
- It would make things _worse_: it is the only thing stopping the running
  application from spending. **Leave it `false`.**

Equally, "idempotency prevents repeat charges" was wrong here. Re-running a
`stitch generate screen` command charges again. There is no dedupe on the CLI
path.

### What actually protects this spike

Procedure, operator discipline, and a hard command count. Nothing else.

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

This document calls that set the **controlled commands**. The term exists because
the previous wording keyed every control to "paid" or "billable" while the cost
table had been corrected to say create, get and delete are _cost unknown_ — so,
read literally, those three fell outside counting, confirmation and the no-retry
rule (W1-RR2-001). **Making the cost labels more honest moved commands out of the
protected set.** A category is only as safe as the controls that reference it by
name.

| Control                                                                 | Status                     |
| :---------------------------------------------------------------------- | :------------------------- |
| A fixed, counted list of **controlled commands**, authorized in advance | **Required** — see below   |
| Operator confirmation immediately before **each controlled command**    | **Required**               |
| Stop on any anomaly; **never** auto-retry a **controlled command**      | **Required**               |
| `BillableOperationsEnabled` stays **`false`** throughout                | **Required**               |
| Delete the project afterwards                                           | Step 8 — itself controlled |
| Use `GEMINI_3_5_FLASH_LITE` to minimise spend                           | Recommended                |

### The controlled-command budget D-2 must authorize

D-2 is not a general "yes to spending". To authorize this spike it must state:

1. **An exact operation-count limit** — the number of **controlled commands**
   (billable _or_ cost unknown), not a vague ceiling.
2. **A monetary cap**, _if and only if_ provider pricing exposes one that can be
   measured. Where it does not, say so rather than implying a cap exists.
3. **Initially: one project and one screen generation.** Nothing more.
4. **An explicit stop before upload (step 5), apply-design-system (step 6) and
   variants (step 7)** unless those calls are separately itemised in the cap.

The initial authorized set is therefore:

| #   | Command                  | Billable         | Authorized initially |
| --- | :----------------------- | :--------------- | :------------------- |
| 1   | `stitch create project`  | **Cost unknown** | Yes                  |
| 2   | `stitch generate screen` | **Yes**          | Yes                  |
| 3   | `stitch get screen`      | **Cost unknown** | Yes                  |
| 4   | `stitch delete project`  | **Cost unknown** | Yes (step 8)         |

**"Cost unknown" is deliberate.** This spike has never been executed, so no
pricing for create, get, delete or apply has been _observed_. Earlier versions of
this table asserted some of these were free; that was an assumption presented as
a fact. Record the observed cost of each as step 9, and only then may any be
called free.

### Operator rules for each controlled command

- **Confirm before running it** — every controlled command, billable or cost
  unknown alike. Read it aloud, check it against the authorized list, then run it.
  One command, one decision.
- **Stop immediately** on: unexpected output, a retry suggestion from the CLI, a
  timeout, an ambiguous envelope, or any billing behaviour not described here.
- **Never re-run a controlled command automatically.** The CLI exits 0 on failure
  (SPIKE-01), so a non-zero exit is not what tells you something went wrong — and
  a command that appears to have failed may still have been charged. Investigate
  before deciding, with a human making that decision.
- **Count as you go.** If the count would exceed the authorized number, stop and
  go back to the Product Owner.

### Enabling the application guard — a separate, narrower thing

The guard may be turned on **only** for a test that runs _through the application
adapter_, and only under its own authorization, distinct from authorizing this
CLI spike.

When that happens it is configured per environment —
`Providers__Stitch__BillableOperationsEnabled=true` as an environment variable
for that one run — **not** by editing the `appsettings.json` defaults. Those
defaults are the fail-closed baseline for every environment, including
developers' machines and CI; changing them changes the default everywhere, which
is the opposite of what a one-off authorized test needs.

### No safety script

A wrapper script that claimed to enforce a monetary limit is not offered here,
and should not be written. It could not do what its name promised: the provider
exposes no per-call price to the CLI, so a script cannot know what it has spent.
A counted checklist is honest about being a checklist. A script called
`safe-stitch.sh` invites the belief that spending is bounded by something other
than the operator's attention.

---

## The blocking question

Before any of the above, one thing must be answered:

> **Can `STITCH_API_KEY` be provisioned for unattended server-side use, and is it
> per-user or per-organization?**

If it is per-user and cannot be used by a service, generation requires per-user
delegated OAuth tokens with consent, storage and refresh. That changes the
authorization model and the job payload, and **requires architectural
re-approval rather than an implementation workaround**.

This is assumption **A-08**, and it is the first task of M01 (**M01-01**)
deliberately: it gates the milestone's main deliverable.
