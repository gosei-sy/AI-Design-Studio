# Open Product Assumptions

Assumptions the product direction rests on that are **not** validated. Recorded
separately from the PRD so they cannot be mistaken for findings.

The rule: if a statement would normally require user research or production data
and we have neither, it belongs here — not in the PRD as a requirement.

| Status            | Meaning                                                      |
| :---------------- | :----------------------------------------------------------- |
| **Open**          | Unvalidated. Treat conclusions drawn from it as provisional. |
| **Accepted risk** | Unvalidated, and we are proceeding deliberately.             |
| **Resolved**      | Validated or disproved; the outcome is recorded.             |

---

## Product and audience

### A-01 — The four roles reflect how teams actually divide this work

**Status:** Open · **Confidence:** Low · **Affects:** authorization model (M01), review UX

We assume Product Owner, UX Lead, Developer and Reviewer are meaningful distinct
roles with the authority split in PRD §3. In small teams one person may hold all
four, which would make role separation friction rather than value.

**Why it matters:** the authorization model is M01 work. Building four roles that
collapse to one in practice is wasted effort and a usability cost.
**How to validate:** interview 5–8 teams already using AI design tools; ask who
approves what today.
**If wrong:** collapse to two roles (member, approver) and make approval a
per-project setting.

### A-02 — Teams will author a Design Contract before generating

**Status:** Open · **Confidence:** Low · **Affects:** the entire product thesis

The product's value depends on users doing contract work _before_ the fun part.
The appeal of AI design tools is immediacy; we are adding a gate in front of it.

**Why it matters:** this is the central product risk. If users skip contract
authoring, every downstream guarantee is empty.
**How to validate:** prototype the contract authoring flow and measure completion
before first generation.
**If wrong:** derive a provisional contract from the first generation and ask the
user to confirm it, inverting the order without losing the artifact.

### A-03 — "Consistency" is the pain users actually feel

**Status:** Open · **Confidence:** Medium · **Affects:** positioning, MVP priorities

We assume inconsistency across AI-generated screens is a felt problem rather
than an inferred one. It may be that speed, cost or prompt quality dominates.

**How to validate:** ask teams to rank their top three frustrations unprompted.
**If wrong:** the design-system conformance work matters less than provider
orchestration and cost control.

---

## Redesign workflow

### A-04 — Teams can produce a feature inventory

**Status:** Accepted risk · **Confidence:** Low · **Affects:** FR-6, the redesign guarantee

The handoff gate (ADR-0007) is only as good as the inventory. We assume teams
can and will enumerate what their existing application does.

**Why it matters:** this is the stated limit of the guarantee (PRD §8.1). If
inventories are routinely incomplete, the gate gives false confidence — the
worst possible outcome for a safety mechanism.
**How to validate:** ask three teams to inventory a real application; measure
time taken and completeness against the actual route list.
**If wrong:** derive a candidate inventory from route discovery (`stitch capture
routes` lists routes found in a codebase) and make the human task review rather
than recall.
**Proceeding because:** the alternative — no gate — is strictly worse, and the
limit is documented rather than hidden.

### A-05 — Screenshots are the common redesign input

**Status:** Open · **Confidence:** Medium · **Affects:** import priorities, ADR-0006 framing

The brief specifies screenshots. But DOM capture gives far higher fidelity, and
if most users have a running application, screenshot import is the lesser path
rather than the primary one.

**How to validate:** ask whether a running instance is reachable during redesign
work.
**If wrong (most users have a running app):** promote DOM capture to the primary
flow and treat screenshot upload as the fallback. Code already supports both.

### A-06 — A screen key can be matched between old and new

**Status:** Open · **Confidence:** Medium · **Affects:** FR-6 coverage mapping

`Screen.Key` is assumed stable enough to match a redesigned screen to an
existing one. A redesign that merges or splits screens breaks the 1:1 mapping.

**Why it matters:** `CoveringScreenIds` is already a list, so many-to-one works.
Many-to-many (one new screen covering parts of several old ones _and_ vice
versa) is not modelled.
**If wrong:** coverage becomes a many-to-many join and the gate logic needs
revisiting.

---

## Provider integration

### A-07 — Stitch's canvas API is stable enough to build on

**Status:** Accepted risk · **Confidence:** Medium · **Affects:** the whole Stitch adapter

`@google/stitch` is at 0.11.1 — pre-1.0, published 2026-10-01, with two releases
in eight days. Its README states Loop "may change without a major version bump".

**Why it matters:** the adapter depends on the CLI's JSON envelope, error codes
and theme schema.
**Mitigation already in place:** the capability abstraction isolates the blast
radius (ADR-0003); the font enum count is asserted so a schema change fails the
build; `--schema` lets us re-derive constraints mechanically.
**Proceeding because:** it is the provider the product is specified around, and
the abstraction makes replacement tractable.

### A-08 — An API key can be provisioned for server-side use

**Status:** **Open — blocking for M01** · **Confidence:** Unknown · **Affects:** all live generation

This is the single most consequential open item.

Stitch canvas operations need `STITCH_API_KEY`, obtained separately from OAuth.
We do not know whether such a key can be provisioned for **unattended
server-side use**, what it costs, whether it is per-user or per-organization, or
what its rate limits are.

The OAuth flow is interactive: `stitch login` opens a browser and listens on a
loopback port. That is unusable from a background worker. If canvas operations
require OAuth _in addition_ to an API key for some endpoints, server-side
generation may not be possible as designed.

**Why it matters:** M01 cannot deliver live generation without this resolved.
**How to validate:** obtain a Stitch account; attempt `STITCH_API_KEY`
provisioning; run the SPIKE-02 test plan.
**If wrong (no server-side credential exists):** the architecture changes
materially — generation would need a user-delegated token held per user, with
all the consent and refresh machinery that implies. **This requires
architectural re-approval, not a workaround.**

### A-09 — Per-generation cost is acceptable at expected volume

**Status:** Open · **Confidence:** Unknown · **Affects:** quota design (M02), pricing

We have no pricing data for Stitch generation. Models are named
(`GEMINI_3_8_FLASH`, `GEMINI_3_5_FLASH_LITE`) but we do not know the cost per
screen or per variant.

**Mitigation in place:** billable operations default off; dry-run planning is
free; idempotency keys prevent duplicate charges.
**How to validate:** obtain pricing; measure a representative project.
**If wrong (cost is high):** variants (1–5 per request) become the first thing
to restrict, and quota accounting moves earlier than M02.

### A-10 — One provider is enough for the MVP

**Status:** Accepted risk · **Confidence:** Medium · **Affects:** MVP scope

Only Stitch is adapted. The brief also names Claude Design.

**Proceeding because:** the capability abstraction exists precisely so a second
adapter is additive. Building one adapter well beats two shallowly.
**Revisit when:** A-08 resolves negatively, which would make a second provider
urgent rather than optional.

---

## Workflow and UX

### A-11 — Approval is a single-approver decision

**Status:** Open · **Confidence:** Medium · **Affects:** data model, approval UX

`ApprovedBy` is a single string. Multi-approver sign-off, approval quorums and
delegation are not modelled.

**If wrong:** approval becomes its own entity with a collection of approvals —
a schema change, though a contained one.

### A-12 — Users will accept contract-drift friction

**Status:** Open · **Confidence:** Low · **Affects:** FR-4, perceived usability

`screen.contract_drift` invalidates screens when the contract changes. This is
correct and will be experienced as the system undoing work.

**Why it matters:** correct-but-annoying mechanisms get worked around.
**How to validate:** observe how often contracts change mid-project in a
prototype.
**If wrong:** needs a graduated response — classify contract changes by whether
they actually affect a given screen, rather than invalidating all of them.

### A-13 — Developers want a specification, not code

**Status:** Open · **Confidence:** Medium · **Affects:** FR-8 handoff format (M03)

PRD §2 says the product does not generate code. If developers expect components,
a specification will feel like a half-measure.

**How to validate:** ask developers what they want to receive from a design
handoff.

---

## Resolved

### A-14 — Google Stitch has a programmatic interface

**Status:** **Resolved — confirmed**, 2026-10-20

`@google/stitch` 0.11.1 (Apache-2.0, Google LLC) provides an agent-first CLI and
MCP server. Every command supports `--json` and `--schema`. Verified by direct
execution; see [../06-spikes/SPIKE-01-stitch-auth-and-capabilities.md](../06-spikes/SPIKE-01-stitch-auth-and-capabilities.md).

Note what is _not_ resolved: that the interface exists says nothing about A-08
(server-side credentials) or A-09 (cost).

### A-15 — Stitch can represent an arbitrary design system

**Status:** **Resolved — disproved**, 2026-10-20

Stitch's theme model is substantially constrained: 67 named fonts, one
`colorMode`, one seed colour plus four role overrides, five roundness steps, no
elevation, motion, breakpoints or component variants. Measured from
`stitch edit design-system --schema`.

Consequence: ADR-0004, the lossy-projection-with-warnings design. This
assumption's disproof is why the projection layer exists.
