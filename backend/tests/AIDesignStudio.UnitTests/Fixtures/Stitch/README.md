# Recorded Stitch CLI responses

**This directory is intentionally empty of fixtures.** It exists so that the
place to put real provider responses is decided before anyone is holding one,
rather than improvised under time pressure.

Created during M01-01 offline preparation. See
[SPIKE-02](../../../../../docs/06-spikes/SPIKE-02-stitch-screen-retrieval.md) for
the test plan that produces these files, and
[M01_WAVE1_REPORT.md](../../../../../docs/05-delivery/M01_WAVE1_REPORT.md) for
what is and is not verified today.

---

## The rule

> **Nothing goes in here that did not come out of a real provider call.**

No hand-written approximations, no "this is probably the shape", no output
copied from provider documentation. A fabricated fixture is worse than an absent
one: an absent fixture is a known gap, while a fabricated one makes the adapter
look verified against a response shape nobody has ever seen. That distinction is
the whole point of `docs/06-spikes/README.md`'s verified/unverified split, and
ADR-0003's rule that an unverified capability must be **absent** from
`Capabilities` rather than present and hopeful.

Until a file here is produced by step 3 or 4 of the SPIKE-02 plan, the response
shapes for `generate screen` and `get screen` remain **unverified**. Those shapes
are **SPIKE-02 technical evidence** — they are not themselves an assumption:

- **A-08** asks whether a server-usable Stitch credential exists at all.
- **A-09** asks whether the observed per-generation **cost** is acceptable at
  expected volume.

Both stay **open**. Recording a response shape does not close either; it supplies
part of the evidence step 9 needs.

## Expected filenames

One file per recorded call, named for the command that produced it. These are
the names the eventual tests will look for; the files do not exist yet.

| File                               | Produced by                                | Needs        | Cost             |
| :--------------------------------- | :----------------------------------------- | :----------- | :--------------- |
| `generate-screen.dry-run.json`     | SPIKE-02 step 2 — `--dry-run`              | **Neither**  | None, no network |
| `status.authenticated.json`        | SPIKE-02 step 1 — `stitch status --json` ³ | **D-1 only** | Cost unknown     |
| `create-project.success.json`      | SPIKE-02 step 3 — `stitch create project`  | D-1 + D-2    | **Cost unknown** |
| `generate-screen.success.json`     | SPIKE-02 step 3 — `stitch generate screen` | D-1 + D-2    | ⚠ **Billable**   |
| `get-screen.success.json`          | SPIKE-02 step 4 — `stitch get screen`      | D-1 + D-2 ¹  | **Cost unknown** |
| `upload-screen.success.json`       | SPIKE-02 step 5                            | D-1 + D-2 ²  | **Cost unknown** |
| `apply-design-system.success.json` | SPIKE-02 step 6                            | D-1 + D-2 ²  | **Cost unknown** |

¹ Retrieval may itself turn out to be free — that has not been observed. It still
needs D-2 **operationally**, because there is nothing to retrieve until the
authorized generation in step 3 has created a screen.

² **Cost unknown**, and only if upload, apply-design-system or variants are
**separately itemised** in D-2's cap. The default authorization stops after one
project and one generation. Nothing here is asserted billable without evidence —
only `generate screen` carries that label.

³ A **readiness probe**, not a live design operation: it proves the credential
works and touches no design. D-1 only, and **not** counted against D-2's
controlled-command budget. Stop immediately if it returns unexpected billing
behaviour.

**Not every fixture depends on D-2.** The dry-run recording needs neither
decision and could be captured today; `status.authenticated.json` needs D-1
alone. Saying otherwise overstates the blocker and hides work that is already
possible.

"Cost unknown" is used wherever cost has not been **observed**. An earlier
version of this table said create was "None observed" and get was "Low / none" —
those were assumptions written as findings. Record real costs in SPIKE-02 step 9
before calling anything free.

Record the **raw stdout**, unedited and unformatted, including the full
envelope. Redact only credential values, and note in the file's sibling
`.notes.md` that a redaction happened and where. Do not reformat JSON: the
adapter parses what the CLI actually emits, and whitespace-normalising a fixture
quietly removes evidence of what that was.

## How these are consumed

The seam is already in place and needs no new infrastructure.
`IStitchCommandRunner` (`AIDesignStudio.Infrastructure/Providers/Stitch/StitchCommandRunner.cs`)
is the single boundary between the adapter and the process. `StitchAdapterTests`
already drives the adapter through `FakeStitchRunner`, which returns a
`StitchResult` built from a stdout string.

So a recorded fixture is wired in by reading the file into that same
`FakeStitchRunner` — no production code changes, and no network in the test run:

```
File  ->  StitchResult.RawStdout  ->  FakeStitchRunner  ->  StitchDesignProvider  ->  assertions
```

Today those tests hold their stdout as inline string literals, which is correct
for the hand-written error-envelope cases: they are specimens of documented
failure codes, not recordings. Real recordings belong in files because they are
large, because they must be diffable when the provider's surface moves, and
because their provenance matters.

## Two things that must not drift

**The envelope is read from `success`, never the exit code.** The Stitch CLI
exits 0 on failure (SPIKE-01). Any fixture-backed test must go through the
adapter, which already knows this, rather than asserting on a process result.

**The billable guard does not protect this recording, and must stay `false`.**

Recording these fixtures means running `stitch ...` directly. That starts at the
process, so it goes through neither `StitchDesignProvider` — where
`EnsureBillableAllowed` lives — nor `PostgresJobQueue`, where idempotency lives.
`StitchProcessCommandRunner` declares the option but never reads it.

So **enabling `BillableOperationsEnabled` would protect nothing here** and would
remove the only thing stopping the running application from spending. Leave it
`false`. Likewise, idempotency keys do not deduplicate a CLI invocation: re-running
a generate command charges again.

What protects the recording session is procedural, and only procedural:

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

SPIKE-02 calls that set the **controlled commands**. Both cost categories get the
same treatment, deliberately: keying the controls to "paid" alone would exclude
`create project`, `get screen` and `delete project`, whose cost has never been
observed (W1-RR2-001).

`status.authenticated.json` is produced by a readiness probe, so it needs **D-1
only** and is not counted against D-2 — which is why the rule says _design
operation_ rather than _provider command_ (W1-RR3-001). See SPIKE-02 § "Cost
control for whoever runs this".

The guard may be enabled **only** for a separately authorized test that runs
_through the application adapter_, and then via environment-scoped configuration
(`Providers__Stitch__BillableOperationsEnabled=true` for that single run) — never
by editing the `appsettings.json` defaults, which are the fail-closed baseline
for every environment including CI. **No test in this repository may enable
it.**
