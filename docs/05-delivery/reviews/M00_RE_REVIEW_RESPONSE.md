# Response to the M00 Re-Acceptance Review

**Decision under response:** CHANGES REQUIRED (round 2)
**Response date:** 2026-10-20 · **Author:** Lead Developer
**Round 1:** [M00_CODEX_REVIEW_RESPONSE.md](M00_CODEX_REVIEW_RESPONSE.md)

---

## Summary

**All three findings accepted and fixed.** RR-001 was the important one, and the
criticism was exact: my round-1 fix added lease-ownership checks to the _domain_
and left the _persistence_ write non-atomic. It validated in memory and then
saved by primary key — which is the same interleaving the original finding
described, with an extra layer of code in front of it.

The lesson worth recording: moving a check from "absent" to "present in the
application layer" does not make it atomic. Only the database can evaluate a
predicate and apply a write in one step.

| Suite                         | Round 1 |     Now |   Added |
| :---------------------------- | ------: | ------: | ------: |
| Unit                          |     182 | **182** |       — |
| Architecture                  |      16 |  **16** |       — |
| Integration (real PostgreSQL) |      38 |  **44** |      +6 |
| Frontend                      |      14 |  **14** |       — |
| Shared contracts              |      65 | **137** |     +72 |
| **Total**                     | **315** | **393** | **+78** |

All 393 pass, no skips. Every gate green.

---

## RR-001 — Lease fencing is now atomic · **FIXED**

### What was wrong

`PostgresJobQueue` loaded the job, validated the lease in memory, then called
`SaveChangesAsync` — an unconditional `UPDATE ... WHERE id = @id`. No concurrency
token, no predicate on `state`, `lease_owner` or expiry. The interleaving stood.

The renewal loop also merely returned on losing the lease, leaving the handler to
continue billable work and discover the loss only at completion.

### What changed

**1. Conditional updates.** Completion, failure and heartbeat are each one
statement:

```sql
UPDATE app.jobs SET ...
WHERE id = @jobId
  AND state = 'Running'
  AND lease_owner = @leaseOwner
  AND lease_expires_at > @now
```

Zero rows affected means the lease was lost between read and write; the caller is
told so. The domain check stays as a fast path that yields a precise error, but
the database is the authority.

**2. Expired leases are refused even when the owner matches.** The
`lease_expires_at > @now` predicate, mirrored by a `job.lease_expired` check in
the domain. This closes the case the review named: nobody has reclaimed the job
yet, so `lease_owner` still equals the stalled worker.

**3. `AsNoTracking` on both the transition read and `GetAsync`.** Two reasons.
An untracked entity cannot be flushed by a later `SaveChanges`, which would
reintroduce the unconditional write. And raw SQL bypasses the change tracker, so
a _tracked_ read would return a snapshot predating the update and hide the
committed row — which is how `A_non_retryable_failure_moves_the_job_straight_to_dead`
started failing mid-fix, and was a real defect in its own right.

**4. The renewal loop cancels the handler.** `RenewLeaseAsync` now takes the
`CancellationTokenSource` and cancels it on lease loss, with a dedicated catch in
the dispatcher so the abandonment is logged as such rather than recorded as a
job failure.

### Tests

Six new integration tests, including the two the review asked for specifically:

| Test                                                                            | Proves                                                                                                                                                 |
| :------------------------------------------------------------------------------ | :----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `A_worker_that_loses_its_lease_between_reading_and_writing_cannot_complete`     | The interleaving, made deterministic: A reads while its lease is valid, the clock advances, B reclaims, A's write is refused and B's attempt is intact |
| `The_conditional_update_predicate_matches_no_row_for_a_stale_lease`             | The predicate in isolation — 0 rows for a stale owner, 1 for the current owner, 0 on replay                                                            |
| `An_expired_lease_is_refused_even_though_the_owner_still_matches`               | Expiry alone is disqualifying                                                                                                                          |
| `A_current_lease_holder_can_complete_fail_and_renew`                            | The fence is not simply refusing everything                                                                                                            |
| `JobDispatcherTests.Losing_the_lease_mid_execution_cancels_the_running_handler` | A blocking handler is cancelled when the lease is lost                                                                                                 |
| `JobDispatcherTests.A_job_with_no_registered_handler_fails_terminally`          | The dispatcher's own write path works for the legitimate owner                                                                                         |

Both of the review's required tests were **verified to fail on the reverted
code**:

- Removing the fencing predicate → `a stale owner must match no row` fails.
- Removing `handlerScope.CancelAsync()` → the cancellation test times out after
  20s with `the handler should have been cancelled once the lease was lost`.

### A latent crash the dispatcher test found

Writing `JobDispatcherTests` meant constructing a `JobDispatcher` for the first
time in any test. It threw immediately:

```
System.ArgumentOutOfRangeException: Index and length must refer to a location
within the string. (Parameter 'length')
```

The lease-owner identity used `[..64]`, which throws when the composed string is
**shorter** than 64 characters — the normal case on a host with a short name. The
dispatcher would have crashed on startup in most environments. It now truncates
to the column width only when the string exceeds it.

This is a direct dividend of the review insisting on a dispatcher test: no
existing test instantiated the class, so nothing could reach the bug.

---

## RR-002 — Schema coverage is now exhaustive · **FIXED**

The review was right that the alignment check covered only the objects someone
had remembered to list, and that required/optional testing stopped at the two top
levels.

### The concrete mismatch, fixed

`DesignSystem["color"]` was `{...} & Record<string, unknown>`, while the schema's
`additionalProperties` requires every extra colour role to be a hex string.
TypeScript therefore accepted payloads the API would reject. It is now
`Record<string, HexColor | ColorMode[] | undefined>`, with a `HexColor` alias
that documents the constraint the type system cannot express.

### New: `schema-coverage.test.ts` (72 tests)

Coverage by **discovery**, not enumeration:

1. Walk every object node in each schema, recording the path a caller would use
   to reach it (`0` indexes an array, `*` steps into an `additionalProperties`
   value).
2. Assert every discovered path appears in a declared list — so a newly nested
   object fails here rather than being silently uncovered.
3. For every `required` field **at every depth**, omit it from a fully populated
   fixture and assert the payload becomes invalid.
4. For every optional field **at every depth**, omit it and assert it stays
   valid.
5. For every `additionalProperties` contract, probe a conforming and a
   non-conforming value.

Cases 3–5 are generated from the schema, so they grow when the schema does. The
nine contract object paths and thirteen design-system paths the review listed as
uncovered — `informationArchitecture`, `accessibility`, `contentRules`, `color`,
`shape`, `components.*` and the rest — are now all covered, and the coverage
assertion fails if any new one appears.

`API_CONTRACTS.md`'s claim has also been rewritten to describe what the two
suites actually do, rather than the blanket "every required/optional rule".

### On the alternative

The review offered generating TypeScript from the schemas. That is the stronger
answer and worth doing, but it is a build-pipeline change with its own
failure modes, and M01 needs the schemas wired at the API boundary first. The
recursive check gives equivalent drift detection today; code generation is
recorded as a candidate for M01-05 when the schemas gain their first real
consumer.

---

## RR-003 — Documentation reconciled · **FIXED**

The review was right, and my round-1 claim that documentation was fully
reconciled was false. The cause is worth stating plainly: one of my patch scripts
accumulated edits in memory and wrote only at the end, so when a later assertion
in the same script failed, **four already-applied edits were silently discarded**
— including the status file's headline. I then verified the narrower follow-up
script and not the whole file.

Every item the review listed, and the result of a repo-wide sweep rather than
spot fixes:

| Item                                                  | Fixed                                                                                  |
| :---------------------------------------------------- | :------------------------------------------------------------------------------------- |
| Status line 4 still `Complete`                        | Now **"Review corrections applied; awaiting re-acceptance"**                           |
| Status lines 15, 128 still 213 tests                  | Now 393                                                                                |
| SPIKE-06 global idempotency index                     | Now `(organization_id, kind, idempotency_key)`                                         |
| SPIKE-06 old test counts, stale lease guidance        | Counts corrected; lease section rewritten for renewal semantics                        |
| spikes/README obsolete index evidence, old totals     | Both corrected                                                                         |
| `JobRecord.cs` "unique per job kind"                  | Now "per organization and job kind"                                                    |
| ADR-0003 cites nonexistent `M00-CX-012`               | Now cites "review §9, discrepancy 7"                                                   |
| Test strategy attributes E2E block to the Node server | Now attributes it to Chromium, and records that the review disproved the earlier claim |

Beyond the listed examples, a sweep for stale totals across every document
normalised all counts to the measured values, and SPIKE-06 and ADR-0005 gained
sections recording both review rounds — including that this spike was first
reported as fully verified and was not.

ADR-0005 amendment **A5** records the RR-001 correction without editing the
accepted decision, per rule 2. The decision's original idempotency text now
carries an inline pointer to amendment A3 rather than being rewritten.

---

## Verification

Measured, not recalled:

```
dotnet build                          0 warnings, 0 errors
dotnet format --verify-no-changes     clean
dotnet test                           242 passed, 0 failed, 0 skipped
  Unit                                182
  Architecture                          16
  Integration (real PostgreSQL)         44
npm test                              14 frontend + 137 contracts
npm run typecheck / lint              clean
npm run format:check                  clean
npm run audit:prod                    0 vulnerabilities
dotnet-ef migrations has-pending      "No changes ... since the last migration"
Clean-database migration              both migrations applied to a fresh database
```

**Total: 393 passing, 0 failing, 0 skipped.**

### Guard tests verified against their own regressions

| Guard                 | Reverted                                     | Result                                              |
| :-------------------- | :------------------------------------------- | :-------------------------------------------------- |
| SQL lease fence       | Predicate removed from the `WHERE` clause    | `a stale owner must match no row` fails             |
| Handler cancellation  | `handlerScope.CancelAsync()` removed         | Times out: `the handler should have been cancelled` |
| Mutable collections   | `=> _versions.AsReadOnly()` → `=> _versions` | `DesignContract.Versions returns a mutable List`    |
| Schema/type alignment | Property added to schema / removed from TS   | Runtime test fails / `tsc` fails                    |

---

## Accepted without change

The review's note on CX-003 is adopted: the organization-scoped recovery design
stands, and **M01 must ensure the losing transaction is rolled back and recovery
uses a clean unit of work**. Added to the M01 prerequisites appendix in
[IMPLEMENTATION_PLAN.md](../../../IMPLEMENTATION_PLAN.md) so it is a stated
constraint on M01-07 rather than a note in a review.

---

## Still blocked

Unchanged, and all external:

| Item                                | Needs                                                                        |
| :---------------------------------- | :--------------------------------------------------------------------------- |
| Live Stitch verification (SPIKE-02) | **D-1** credential + **D-2** capped spend authorization — Product Owner only |
| Docker Compose                      | A Docker-capable host (M01-12)                                               |
| Browser E2E                         | `npx playwright install chromium` (M01-11)                                   |
| Authentication / authorization      | M01-02, M01-03 — not in M00 scope                                            |

M00 remains **not self-accepted**. Nothing committed, nothing deployed, no
provider call, no spend. M01 unstarted.
