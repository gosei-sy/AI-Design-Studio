# Response to M00 Correction Round 4

**Decision under response:** CHANGES REQUIRED (round 4)
**Response date:** 2026-10-20 · **Author:** Lead Developer
**Prior rounds:** [1](M00_CODEX_REVIEW_RESPONSE.md) · [2](M00_RE_REVIEW_RESPONSE.md) · [3](M00_R3_RESPONSE.md)

---

## Summary

All three findings accepted and fixed.

R4-001 is the one worth dwelling on, because it is the fourth consecutive round
on lease handling and the first to find the defect somewhere genuinely new.
Rounds 1–3 were all about the writes a worker makes _while holding_ a lease.
Nothing had examined where the lease comes from.

| Round | Where the hole was                                                            |
| :---- | :---------------------------------------------------------------------------- |
| 1     | No fencing predicate — validated in memory, saved by primary key              |
| 2     | Predicate present, but its time operand captured before an `await`            |
| 3     | Operand resolved at execution — but only on the _transition_ writes           |
| 4     | The _claim_ resolved eligibility in SQL and the lease from the worker's clock |

Round 3's response named the lesson as "a guard is only as atomic as the step
that evaluates it". That was right and incomplete. The sharper version, after
this round:

> **A correct guard fed an inconsistent operand rejects valid writes as
> confidently as invalid ones.**

In all four rounds the predicate was never the thing that was wrong. The operand
was — and in this round the operand was wrong in the one statement nobody had
audited, because it was the statement that _creates_ the thing the other three
rounds were protecting.

Measured totals, run after the corrections:

| Suite                         | Round 3 |     Now |  Added |
| :---------------------------- | ------: | ------: | -----: |
| Unit                          |     185 | **185** |      — |
| Architecture                  |      16 |  **16** |      — |
| Integration (real PostgreSQL) |      52 |  **57** |     +5 |
| Frontend                      |      14 |  **14** |      — |
| Shared contracts              |     163 | **164** |     +1 |
| **Total**                     | **430** | **436** | **+6** |

Integration tests **passed, not skipped** — `skipped: 0` appears in the output
below, against PostgreSQL 15.19.

No unit test was added. R4-001 moved the claim into SQL, and a unit test cannot
observe which clock PostgreSQL used. Putting the evidence where the behaviour
lives is the point of the fix.

Two required commands are **BLOCKED** by the environment and are reported as
such, not as passed: `npm run test:e2e` and `docker compose config`.

---

## R4-001 — The claim is anchored to database time · **FIXED**

### Root cause

`ClaimAsync` selected eligible rows with `clock_timestamp()` — correct, and the
round-3 fix. It then handed each row to the domain:

```csharp
// eligibility: database time
WHERE run_after <= clock_timestamp() ...

// the lease it produced: application time
public bool TryClaim(string leaseOwner, TimeSpan leaseDuration, IClock clock)
{
    var now = clock.UtcNow;
    ...
    LeaseExpiresAt = now + leaseDuration;   // the worker's clock
}
```

One logical transition spanning two clocks. ADR-0005 amendment A6 had stated
"one authoritative clock, and it is PostgreSQL" in round 3, and that claim did
not hold for the single write that creates a lease.

### Why this is an availability bug, not a tidiness one

The failure is asymmetric, and the bad direction is the common one:

| Worker clock      | Consequence                                                                                                                                                                                                   |
| :---------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Behind by > lease | The stored expiry is already past. The worker's **own first heartbeat** is refused, the job returns to the pool, and an attempt is consumed. Enough repetitions and it is dead-lettered without ever running. |
| Ahead             | The lease outlives its nominal duration; crash recovery is delayed by the skew.                                                                                                                               |

The "behind" case is worth stating plainly: the fencing added in rounds 2 and 3
is what converts clock skew into lost work. Before that fencing existed, a skewed
lease was merely wrong; now it is actively rejected. Hardening the write path
without unifying the clock made the system _less_ available under a condition
that is entirely routine — NTP step corrections, suspended VMs, container clock
drift.

### Correction

| File                                                                     | Change                                                                             |
| :----------------------------------------------------------------------- | :--------------------------------------------------------------------------------- |
| `backend/src/AIDesignStudio.Infrastructure/Jobs/PostgresJobQueue.cs:129` | `ExecuteClaimAsync` — select **and** claim in one data-modifying statement         |
| …`:192`                                                                  | `DeadLetterExhaustedAsync` — exhausted-row sweep, same transaction, same clock     |
| …`:248`                                                                  | `ExhaustedLeaseErrorSqlTemplate` — the domain's message, not a restatement of it   |
| …`:260`                                                                  | `CreateCommand` — raw ADO on the context's connection and ambient transaction      |
| …`:277`                                                                  | `SimulatedNowParameter` — the simulated seam, explicitly typed for the NULL case   |
| …`:74`                                                                   | `ClaimAsync` — two statements, commit, then materialise what PostgreSQL wrote      |
| `backend/src/AIDesignStudio.Domain/Jobs/JobRecord.cs:121`                | `TryClaim` documented as the in-memory model, no longer the queue's path           |
| …`:186`                                                                  | `ExhaustedLeaseErrorTemplate` / `ExhaustedLeaseError` — one source for the wording |

The claim:

```sql
WITH write_time AS MATERIALIZED (
    SELECT COALESCE(@simulated_now::timestamptz, clock_timestamp()) AS now
),
candidate AS (
    SELECT j.id
    FROM app.jobs j
    WHERE (j.state = 'Pending'  AND j.run_after        <= (SELECT now FROM write_time))
       OR (j.state = 'Running'  AND j.lease_expires_at <= (SELECT now FROM write_time))
    ORDER BY j.run_after
    LIMIT @max_jobs
    FOR UPDATE SKIP LOCKED
)
UPDATE app.jobs j
SET state            = 'Running',
    attempt          = j.attempt + 1,
    lease_owner      = @lease_owner,
    lease_expires_at = (SELECT now FROM write_time) + make_interval(secs => @lease_seconds),
    updated_at       = (SELECT now FROM write_time)
WHERE j.id IN (SELECT id FROM candidate)
  AND j.attempt < j.max_attempts
RETURNING j.id
```

Point by point against the requirement:

1. **Same instant for eligibility and mutation** — both read `write_time`.
2. **PostgreSQL sets all persisted claim state** — `state`, `lease_owner`,
   `lease_expires_at`, `attempt`, `updated_at`, in the `SET` clause. There is no
   `started_at` column in `app.jobs`; the attempt's start is implied by
   `updated_at` on this transition, so "where applicable" does not apply. Stated
   rather than silently skipped.
3. **Expiry from PostgreSQL time** — `write_time.now + make_interval(...)`.
   `IClock.UtcNow` is not bound into this statement at all.
4. **Preserved** — `FOR UPDATE SKIP LOCKED`; the `LIMIT @max_jobs` batch cap;
   max attempts as `j.attempt < j.max_attempts`; exhausted-row dead-lettering
   (moved to a second statement in the same transaction, so its terminal
   timestamps come from the same clock); idempotency (untouched — `EnqueueAsync`
   is unchanged); one transaction, committed once.
5. **Not a read-then-mutate** — nothing is read before the write. The read
   _after_ the commit only materialises rows PostgreSQL has already written, and
   writes nothing back.
6. **ADR not edited in place** — amendment **A8** added. A6's text stands as the
   record of what was believed; A8 says where it was wrong.

#### `MATERIALIZED` is load-bearing

`clock_timestamp()` is volatile. Inlined, it would be evaluated at each of the
four references, so the instant that admitted a row would differ from the expiry
stored against it by however long the statement took. `MATERIALIZED` pins one
instant per statement. (`now()` would also be stable, but it is transaction-start
time — the gap round 3 removed.)

#### Why raw ADO rather than `FromSql`

PostgreSQL requires a data-modifying CTE at the top level, and EF wraps a
`FromSql` query in a subselect to project its columns. The statement is issued on
the context's own connection and ambient transaction, so transaction semantics
are unchanged.

#### Two models of one transition, held together

`TryClaim` survives — 23 of the 27 `JobRecordTests` use it to put a job into
`Running` — but production no longer calls it. Two descriptions of one rule drift
apart unless something fails when they do, which was exactly the lesson of the
copied-SQL test round 3 deleted. So:

`The_sql_claim_agrees_with_the_domain_claim_model` claims a job through
`PostgresJobQueue`, applies `TryClaim` to an equivalent fresh job, and asserts
the two agree on `State`, `Attempt` and `LeaseOwner`. Expiry is excluded
deliberately — disagreeing about expiry is the entire point of this round.

The dead-letter message is shared rather than restated:
`JobRecord.ExhaustedLeaseErrorTemplate` has its `{0}`/`{1}` rewritten into
PostgreSQL `format()` positional placeholders. One sentence, one place.

### Tests added

All drive `PostgresJobQueue`. No copied SQL. No `Thread.Sleep`.

| Required coverage                        | Test                                                                                                                                 |
| :--------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------- |
| Worker clock behind → valid lease        | `A_worker_clock_an_hour_behind_the_database_still_mints_a_usable_lease`                                                              |
| Worker clock ahead → no lengthening      | `A_worker_clock_an_hour_ahead_of_the_database_does_not_lengthen_its_lease`                                                           |
| Expiry = DB claim time + duration        | `The_persisted_lease_expiry_is_database_claim_time_plus_the_lease_duration`                                                          |
| Concurrent claimers cannot both claim    | `Eight_workers_racing_for_one_job_produce_exactly_one_claim` (+ the existing `Two_workers_claiming_concurrently_take_disjoint_jobs`) |
| Attempt limits, exhausted dead-lettering | `A_job_that_repeatedly_crashes_its_worker_is_dead_lettered` (existing, still passing)                                                |
| SQL and domain do not diverge            | `The_sql_claim_agrees_with_the_domain_claim_model`                                                                                   |

Three details worth flagging, because they are where a weaker test would have
passed without proving anything:

**The "behind" test renews, it does not merely inspect.** Asserting that the
stored expiry looks plausible would pass on an implementation that stored the
right value and still refused to act on it. The test calls
`TryHeartbeatAsync` and requires `true`: the lease the worker minted must be one
the worker can actually hold.

**The expiry test is tolerance-free.** It brackets the claim between two
`SELECT clock_timestamp()` reads and asserts the stored expiry falls in
`[before + Lease, after + Lease]`. No fuzz factor to tune, and no way for it to
pass by accident.

**`SkewedSystemClock` is deliberately not an `ISimulatedClock`.** The simulated
seam substitutes its instant for the server's, which makes the two clocks
indistinguishable — precisely the property these tests must break. So the skew
tests run against a real server clock, with the application clock offset by an
hour against a five-minute lease: the two cannot be confused.

### Deliberate regressions

| Reverted                                                | Result                                                                     |
| :------------------------------------------------------ | :------------------------------------------------------------------------- |
| `lease_expires_at` from a bound application timestamp   | **3 failed** — all three clock tests                                       |
| `AND j.attempt < j.max_attempts` removed from the claim | **1 failed** — `A_job_that_repeatedly_crashes_its_worker_is_dead_lettered` |

The first is the regression the review required. Exact output:

```
failed ... A_worker_clock_an_hour_behind_the_database_still_mints_a_usable_lease (42ms)
failed ... The_persisted_lease_expiry_is_database_claim_time_plus_the_lease_duration (23ms)
failed ... A_worker_clock_an_hour_ahead_of_the_database_does_not_lengthen_its_lease (18ms)
  total: 57
  failed: 3
  succeeded: 54
```

Both regressions were reverted and the suite re-run green before anything else
was done.

### One correction to my own first attempt

My first versions of the two skew tests enqueued the job with the skewed clock as
well as claiming with it. The `+1h` case then failed for the wrong reason: a fast
clock sets `run_after` an hour in the database's future, so the job was correctly
ineligible and nothing was claimed. The arrangement was wrong, not the fix.

Enqueueing now always uses a true clock, and only the claiming worker is skewed —
which is also the realistic shape, since the API enqueues and a worker claims.
Noted because it is the second round running in which setting up the test
correctly was harder than the fix, and in both cases the difficulty was the
signal.

---

## R4-002 — The TypeScript/schema claim corrected · **FIXED**

### What was overclaimed

`index.ts` said, of the round-3 colour split:

> Split into two properties so the type is **exact**. … TypeScript rejects what
> the schema rejects.

The first sentence described a real improvement. The second was false while
`HexColor` is `string`: `{ primary: "red" }` compiles and the schema rejects it.
R3-002's own changelog row repeated the word "exact", so that row is corrected
too.

### Correction

No code generation introduced. JSON Schema remains the runtime authority. The
useful roles/modes structural typing is preserved — `danger: ["light"]` is still
a type error, which was the genuine half of R3-002.

Reworded, with the limit stated rather than implied:

- **`packages/contracts/src/index.ts`** — the file header now has explicit _what
  the alignment covers_ and _what it does not_ sections, naming `pattern`,
  `minLength`, `maxLength`, `uniqueItems` and numeric bounds as not enforced;
  states that nothing in the package is generated and that generation is out of
  M00 scope.
- **`HexColor`** — "An alias for `string`, and therefore **not enforced by the
  compiler**: `"red"` and `""` satisfy this type and are rejected by the
  schema's `pattern`."
- **The `color` member** — now says the split fixes the _structural_ half and
  "does **not** make the type exact", with the counter-example inline.
- **`docs/02-architecture/API_CONTRACTS.md`** — new paragraph: _The alignment is
  structural, not semantic_, with the operational consequence spelled out — the
  API must validate every payload against the schema rather than trusting a
  typed client.

### Proven, not asserted

The review's instruction was to claim nothing a test does not prove. So the
limitation itself is now a test:

```ts
it("accepts at compile time what the schema rejects at runtime, for lexical constraints", () => {
  const typeChecksButIsInvalid: DesignSystem = {
    ...minimalDesignSystem,
    color: { roles: { primary: "red" } },
  };

  expect(validateDesignSystem(typeChecksButIsInvalid)).toBe(false);
});
```

This compiles **only because** `HexColor` is `string`, and asserts the schema
catches it. If the types were ever generated and made exact, the file would stop
compiling — which is the correct way to be told that the documentation needs
updating. (+1 contract test: 163 → 164.)

`schema-alignment.test.ts` already described itself as a _structural_ check and
needed no change.

---

## R4-003 — Documentation and fixtures reconciled · **FIXED**

### Root cause

Round 3 measured first and then swept — but swept with `rg` for whole numbers,
which cannot see a figure that is correct in one document and stale in another
expressed differently ("137 tests" vs "163 tests total"), and does not check that
a _table_ reconciles to its own total. Two of the three categories the review
found were of exactly that kind.

The per-suite tables were the worst case: they had been maintained by counting
`[Fact]` attributes. A `[Theory]` contributes one attribute and several tests, so
the unit table summed to 173 against a measured 185 and nobody had added it up.

Every figure below is now a **discovered** count, taken from the runners.

### Measured counts, by class

| Suite        | Classes                                                                                                                                                                                                                                           |   Total |
| :----------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------: |
| Unit         | JobRecord 27, StitchThemeProjection 24, StitchDesignProvider 20, ScreenApproval 20, DesignSystem 20, DesignContract 15, FeatureInventory 14, AssetStorage 14, ArtifactParsing 12, DesignProject 11, StitchEnvelopeParsing 5, JobDispatcherLease 3 | **185** |
| Architecture | ModuleBoundary 16                                                                                                                                                                                                                                 |  **16** |
| Integration  | DurableJobQueue 31, Persistence 16, ApiEndpoint 8, JobDispatcher 2                                                                                                                                                                                |  **57** |
| Frontend     | api 8, system-status 6                                                                                                                                                                                                                            |  **14** |
| Contracts    | contracts 37, schema-alignment 29, schema-coverage 98                                                                                                                                                                                             | **164** |

Note `StitchAdapterTests.cs` holds two classes, which is part of why the old
file-keyed table never reconciled.

### Every location corrected

| File                                                                | Correction                                                                                                                                                                                                                                                                                                           |
| :------------------------------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IMPLEMENTATION_PLAN.md`                                            | "two verified, two partially verified, one blocked … and one fully verified" → **three verified, two partially verified, one blocked**, pointing at the spike table as authoritative; 430 → 436                                                                                                                      |
| `docs/02-architecture/API_CONTRACTS.md`                             | "Two suites" → **Three suites**; 163 → 164; new structural-not-semantic paragraph (R4-002)                                                                                                                                                                                                                           |
| `docs/02-architecture/decisions/ADR-0004…`                          | **Amendment A1** mapping `color.primary` → `color.roles.primary` etc. Accepted table left as written                                                                                                                                                                                                                 |
| `backend/tests/AIDesignStudio.UnitTests/DesignSystemTests.cs`       | `color.primary` → `color.roles.primary`; typography and shape added; note on why the short blobs are deliberately not valid design systems                                                                                                                                                                           |
| `backend/tests/AIDesignStudio.IntegrationTests/PersistenceTests.cs` | Same; the fixture had also been missing `typography` and `shape` entirely                                                                                                                                                                                                                                            |
| `docs/01-product/PRD.md`                                            | 137 → 164 contract tests; NFR-6 430 → 436                                                                                                                                                                                                                                                                            |
| `docs/01-product/USER_STORIES.md`                                   | 137 → 164 contract tests                                                                                                                                                                                                                                                                                             |
| `docs/06-spikes/SPIKE-05-…`                                         | 137 → 164 contract tests                                                                                                                                                                                                                                                                                             |
| `docs/04-engineering/TEST_STRATEGY.md`                              | Summary table 430 → 436; **unit table rebuilt by class** with a `Total` row; **integration table rebuilt** with `JobDispatcherTests` added and a `Total` row; contracts per-file breakdown; 253 → 258; the "developer without PostgreSQL" figure 139 → 201; a note that counts are discovered, not attribute-tallied |
| `docs/05-delivery/IMPLEMENTATION_STATUS.md`                         | M00 row now says **Not accepted**; criterion 5 recomputed; cumulative-additions table by round; measured output replaced; blocked-commands table added; scale 430 → 436, ADRs "two amended" → three; round-1 and round-2 changelog rows corrected; round-4 row appended                                              |
| `docs/06-spikes/SPIKE-06-…`                                         | Round-4 section, including what the new tests do **not** prove                                                                                                                                                                                                                                                       |
| `README.md`, `CLAUDE.md`, `AGENTS.md`                               | 253 → 258, 163 → 164, 52 → 57; skip-reporting examples recomputed                                                                                                                                                                                                                                                    |
| `docs/02-architecture/decisions/ADR-0005…`                          | **Amendment A8** (R4-001)                                                                                                                                                                                                                                                                                            |
| `.gitignore`                                                        | `.vitest/` added — written by `vitest --reporter=json`, which is how the per-file counts were measured                                                                                                                                                                                                               |

### Fixtures verified, not eyeballed

The corrected fixtures were run through the actual schema with Ajv rather than
hand-checked:

```
VALID    DesignSystemTests.Tokens
VALID    PersistenceTests.tokens
INVALID  OLD color.primary shape  -> /color must have required property 'roles';
                                     /color must NOT have additional properties
```

The shorter blobs in `DesignSystemTests` (`{"schemaVersion":1,"name":"Revised"}`)
are **intentionally** not valid design systems and were left alone: those tests
are about version immutability and content hashing, where the payload is an
opaque string whose only relevant property is whether it changed. The domain does
not validate tokens at M00. A comment now says so, so this does not read as the
same defect next round.

### Required stale search

| Term            | Remaining occurrences                                                                                       |
| :-------------- | :---------------------------------------------------------------------------------------------------------- |
| `137`           | None outside review-response documents                                                                      |
| `236`           | None anywhere                                                                                               |
| `379`           | None anywhere                                                                                               |
| `393`           | Only in review-response documents and the cumulative-additions table row labelled "After round 2"           |
| `two verified`  | None                                                                                                        |
| `Two suites`    | None                                                                                                        |
| `color.primary` | ADR-0004 A1's mapping table (as the historical path, by design) and one code comment explaining the history |

---

## Verification — actual output

### Backend

```
$ dotnet build
    0 Warning(s)
    0 Error(s)

$ dotnet test
  AIDesignStudio.ArchitectureTests  passed (6s 120ms)
  AIDesignStudio.IntegrationTests   passed (17s 143ms)
  AIDesignStudio.UnitTests          passed (6s 017ms)

  total: 258
  failed: 0
  succeeded: 258
  skipped: 0

$ dotnet format --verify-no-changes
(no output; exit 0)

$ dotnet-ef migrations has-pending-model-changes \
    --project backend/src/AIDesignStudio.Infrastructure \
    --startup-project backend/src/AIDesignStudio.Api
No changes have been made to the model since the last migration.
```

Per suite, each run separately: **unit 185**, **architecture 16**,
**integration 57** — every one `skipped: 0`. The integration suite **passed
against PostgreSQL 15.19**; it is not being counted while skipped.

### Frontend and shared contracts

```
$ npm test
  frontend            Test Files  2 passed (2)    Tests  14 passed (14)
  packages/contracts  Test Files  3 passed (3)    Tests  164 passed (164)

$ npm run typecheck      clean
$ npm run lint           clean
$ npm run format:check   All matched files use Prettier code style!

$ npm run build --workspace frontend
✓ Generating static pages using 5 workers (4/4) in 17.0s
Route (app)
┌ ◐ /
└ ○ /_not-found
✓ Compiled successfully
```

**Total: 436 passing, 0 failing, 0 skipped.**

### BLOCKED — reported, not worked around

```
$ npm run test:e2e --workspace frontend
  4 failed
    [chromium] › e2e/status.spec.ts:14:7 › system status page › renders the application shell
    [chromium] › e2e/status.spec.ts:21:7 › … reports either a health result or a clear connection error
    [chromium] › e2e/status.spec.ts:37:7 › … lists provider capabilities when the API is reachable
    [chromium] › e2e/status.spec.ts:54:7 › … never renders a credential value

  Error: browserType.launch: Executable doesn't exist at
  /home/claude/.cache/ms-playwright/chromium_headless_shell-1248/chrome-headless-shell-linux64/chrome-headless-shell
  ╔════════════════════════════════════════════════════════════╗
  ║ Looks like Playwright was just installed or updated.       ║
  ║ Please run the following command to download new browsers: ║
  ║     npx playwright install                                 ║
  ╚════════════════════════════════════════════════════════════╝
```

```
$ docker compose config
/bin/bash: line 9: docker: command not found
(exit 127)

$ command -v docker
(no output — not installed)
```

**Neither is counted as passed.** Both failures are missing executables, not
defects in the specs or the compose file — and both remain unverified artifacts,
labelled as such wherever they are mentioned. No product code was modified to
work around either. Tracked as **M01-11** (Playwright Chromium) and **M01-12**
(Docker Compose).

---

## Remaining external blockers

| #          | Blocker                                                  | Owner             | Unblocked by                                       |
| :--------- | :------------------------------------------------------- | :---------------- | :------------------------------------------------- |
| **D-1**    | No Stitch account or `STITCH_API_KEY`                    | **Product Owner** | Provisioning a credential for a non-production org |
| **D-2**    | No authorization to spend with a provider                | **Product Owner** | An explicit capped budget                          |
| **A-08**   | Unknown whether a server-usable Stitch credential exists | **Product Owner** | D-1; may require architectural re-approval         |
| **M01-11** | Playwright Chromium not installed                        | Environment       | `npx playwright install chromium`                  |
| **M01-12** | Docker not installed                                     | Environment       | A Docker daemon, to execute `docker-compose.yml`   |

D-1, D-2 and A-08 are the Product Owner's alone. SPIKE-02 stays **blocked** with
a reproducible test plan rather than a mocked substitute.

---

## Confirmations

- **No M01 work started.** The claim rewrite is a correction to an M00
  deliverable, not new scope.
- **Nothing committed, pushed or deployed.** The repository still has no commits.
- **No provider call.** No Stitch invocation, no network request to any provider.
- **`BillableOperationsEnabled` remains `false`.** No money spent.
- **No guard or acceptance criterion weakened.** Every predicate from rounds 1–3
  is intact; this round added one and proved it fails when removed.
- **No ADR decision edited in place.** ADR-0005 A8 and ADR-0004 A1 are
  amendments; the accepted text of both stands as written.
- **No code generation implemented, and nothing claims it was.**
- **No test skipped or deleted to make a number look better.** The only test
  removed in any round was round 3's copied-SQL test, which measured itself.
- **M00 is not self-approved.**

---

**M00 corrections applied — awaiting independent re-acceptance.**
