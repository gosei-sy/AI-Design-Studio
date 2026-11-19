# M01 Wave 3 — Response to re-review round 2

**Review:** [M01_WAVE3_R2_CODEX_REVIEW.md](M01_WAVE3_R2_CODEX_REVIEW.md) · **Decision:** CHANGES REQUIRED (3 P2)
**Response date:** 2026-11-05 · **Author:** Lead Developer
**State:** all three findings corrected. **M01-03 awaits independent re-acceptance and is not accepted. M01-04 has not started.**

All three are correct, and I reproduced each one before changing anything. Two
of them are the same mistake in different clothes: **I wrote tests that could not
reach the state the defect lived in, then read their passing as evidence.**

| Finding        | Reproduced | Status        |
| :------------- | :--------- | :------------ |
| **W3-RR2-001** | yes        | **Corrected** |
| **W3-RR2-002** | yes        | **Corrected** |
| **W3-RR2-003** | yes        | **Corrected** |

W3-RR-001's acceptance is noted with thanks and nothing in it was touched.

---

## W3-RR2-001 — P2 — The baseline was applied to memberships with observed history

### What was wrong

ADR-0009 A3.2 says _"memberships that predate the event table receive one
entry"_. My implementation asked a different question:

```sql
WHERE NOT EXISTS (… WHERE e.membership_id = m.id AND e.type = 'MigrationBaseline')
```

That is "has no baseline yet", not "predates the chain". **Migration 5 created
the event table**, so a deployment sitting at migration 5 writes genuine
`Provisioned` entries through the aggregate — and every one of those memberships
was given a baseline as well.

Reproduced before touching anything: database at migration 5, a membership with
a real observed `Provisioned` event, then migrate forward.

```text
Provisioned       | observed-operator
MigrationBaseline | system:migration/EnforceProjectTenantOwnershipAndBaselineAudit
```

The reviewer's phrasing of the harm is exact: this _"makes the baseline-presence
signal unreliable for distinguishing reconstructed from observed history"_ —
which is the only reason the type exists at all. I had written in DATA_MODEL that
a baseline's presence is how a reader spots a reconstructed chain, and then made
that signal fire on observed ones.

**My own test could not see it.** It created its membership _after_ the latest
migration had run, so there was no upgrade window to be in. The window is a
third state, not a midpoint between "before" and "after".

### Correction

The predicate is now **"no audit events at all"** — a membership with any event
does not predate the chain, by definition. The revocation reconstruction is
guarded per event type **and** conditioned on a baseline existing, so a
membership revoked through the aggregate is excluded twice over.

New test: `A_membership_with_an_observed_event_is_not_baselined_by_the_upgrade`.
It stops at migration 5 and seeds three memberships, which is what the finding
needed:

- one **provisioned** through the audited path → no baseline, its single observed
  entry untouched;
- one **provisioned and revoked** through the audited path → no baseline, and
  exactly one `Revoked` entry, so nothing is duplicated;
- one **genuinely pre-chain** with no events → still gets its baseline, so the
  narrower predicate has not quietly disabled the feature.

Recorded as **ADR-0009 A4.1**.

---

## W3-RR2-002 — P2 — The idempotency test was a no-op

### What was wrong

The assertion re-ran `MigrateAsync(connectionString)` against a database already
at latest. **EF skips an applied migration, so no SQL executed.** The unchanged
count proved that EF does not re-apply migrations — a fact about EF, not about my
`INSERT`s.

The reviewer also caught the documentation defect underneath it: my response
claimed the extracted SQL was manually re-run, and that was true, but the test I
described as the permanent evidence did not do it. Manual evidence plus a
mislabelled test is worse than no test, because it reads as covered. Rule 4 asks
for an automated test, and this invariant had none.

### Correction

The two statements now live in `MembershipAuditBaselineSql.Backfill`, referenced
by the migration, so a test can execute **exactly the statements the migration
runs** rather than a copy that can drift.

`Re_running_the_backfill_statements_changes_nothing` builds a legacy database,
migrates forward, then runs that constant a **second and a third** time —
"idempotent" is not "survives being run twice". It asserts a fingerprint of
counts **per membership and per type**:

```
<membership>:MigrationBaseline:1 | <membership>:MigrationBaseline:1 | <membership>:Revoked:1
```

A global total would hide one row appearing while another disappeared, which is
precisely the failure mode the original single-guard bug would have produced.

`MembershipAuditBaselineSql` carries a **never edit this SQL** warning: an
applied migration must keep meaning what it meant when it was applied, and a
shared constant is the one way that could silently stop being true.

---

## W3-RR2-003 — P2 — The migration could not be rolled back

### What was wrong

`Down()` dropped the widened event-type constraint and immediately restored the
old one — which permits only `Provisioned`, `RolesChanged` and `Revoked` — while
`MigrationBaseline` rows were still in the table.

Reproduced:

```text
23514: check constraint "ck_organization_membership_events_type"
of relation "organization_membership_events" is violated by some row
```

Deterministic for any database holding a single membership. **An emergency
rollback failed on data the migration itself had written** — which is the worst
time for a rollback to be the thing that breaks.

### Correction

`Down()` now removes what `Up()` inserted, before restoring the constraint, and
the rollback semantics are **decided and recorded** rather than left implicit
(**ADR-0009 A4.2**):

| On rollback                              | Why                                                                                                                                             |
| :--------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------- |
| `MigrationBaseline` rows **deleted**     | The type does not exist in the older schema; they were this migration's output                                                                  |
| Reconstructed `Revoked` rows **deleted** | Also its output. Identifiable only while their baseline exists — so deleted first                                                               |
| **Observed** events **kept**             | They predate the migration. A rollback that emptied the table would destroy real audit history                                                  |
| Repaired tenant keys **not restored**    | Those values were wrong and the project is the authoritative owner. Restoring an incorrect tenant key is the one thing a rollback should refuse |

The last row is a limit worth naming: a downgrade returns the **schema** and this
migration's **audit rows**, not the pre-repair data. Someone rolling back to
inspect a mismatch will not find it.

`The_migration_rolls_back_with_baselined_memberships_present` walks the real
sequence — migration 4, seed legacy rows, migration 5, seed an **observed**
membership, migration 6, then down to 5 — and asserts:

- the downgrade succeeds;
- every baseline is gone;
- **the observed entry survives**, which is a stronger claim than "the table is
  empty" and the only version that is safe on a database that has served
  traffic;
- the old constraint is back and rejects `MigrationBaseline`;
- migrating **forward again** works, so a rollback is not a one-way door.

I added the observed-membership half after the manual probe showed one event
surviving where my first draft asserted zero. The draft would have passed only
because every membership in it was legacy.

---

## Verification — actual output, 2026-11-05

```
$ dotnet build --configuration Release
  0 Warning(s), 0 Error(s)

$ dotnet test --configuration Release
  total: 477   failed: 0   succeeded: 477   skipped: 0

$ dotnet format --verify-no-changes            → exit 0

$ npm test
  frontend            14 passed (14)
  packages/contracts  164 passed (164)

$ npm run typecheck / lint / format:check      → exit 0 / exit 0 / Prettier clean
$ npm run build --workspace frontend           → production build completed

$ npm run test:e2e --workspace frontend        (real Development API on :5080)
  7 passed (41.5s)

$ dotnet-ef migrations has-pending-model-changes …
  No changes have been made to the model since the last migration.

$ npm run audit:prod                           → found 0 vulnerabilities
$ dotnet list package --vulnerable --include-transitive
                                               → no vulnerable packages, 10/10 projects
$ git diff --check                             → exit 0
```

**Per suite:** 262 unit · 25 architecture · **190** integration · 14 frontend ·
164 contracts = **655**, plus 7 E2E counted separately. **+3 backend tests**, all
three in `MigrationUpgradeTests`, and **0 skipped**.

### Migration from empty

```
12 migration-apply lines (6 migrations, logged twice by the tool)

ak_projects_organization_id_id
fk_assets_project_tenant · fk_design_contracts_project_tenant
fk_design_systems_project_tenant · fk_feature_inventories_project_tenant
fk_screens_project_tenant

ck_organization_membership_events_type =
  CHECK (type = ANY ('Provisioned','RolesChanged','Revoked','MigrationBaseline'))
```

### The reviewer's two probes, re-run by hand

**Upgrade window** — database at migration 5 with one observed and one legacy
membership, migrated forward:

```
legacy MigrationBaseline | system:migration/EnforceProjectTenantOwnershipAndBaselineAudit
window Provisioned       | observed-operator
```

Only the legacy membership is baselined. The observed chain is untouched.

**Downgrade** — same database, back to migration 5:

```
Done.
exit=0
events remaining: 1
```

Exit 0 instead of `23514`, and the one remaining event is the observed
`Provisioned` that correctly survived.

**Every disposable database was dropped:**

```
$ SELECT … FROM pg_database WHERE datname LIKE 'aids\_%'   → (none)
```

---

## Still open, unchanged by this response

| Item                                         | Status                                                                          |
| :------------------------------------------- | :------------------------------------------------------------------------------ |
| **D-8** — reset delivery transport           | **Open.** Fail-closed pre-production gate; no Production deployment may proceed |
| **D-5** — deployment target, secret store    | **Open.** Production also refuses to start without a Data Protection protector  |
| **D-1 / D-2** — Stitch account, capped spend | **Open.** `BillableOperationsEnabled` remains `false`                           |
| **MFA recovery codes**                       | **Open** — risk R-5, a non-blocking finding of M01-02's acceptance              |
| **M01-11** — E2E in CI                       | 7/7 locally; the CI run needs a push, which is not authorized                   |
| **M01-12** — Docker Compose                  | **BLOCKED.** Docker is not installed                                            |

**S-22 remains partial**, for the three reasons now on the record: the chain is
append-only by construction of the aggregate rather than by database privilege,
pre-migration history is unknowable, and a rollback removes this migration's own
audit rows.

No Stitch call, no spend, no commit, push, PR or deployment, no new
infrastructure, no Production configuration.

**M01-02 remains accepted** with its two recorded non-blocking findings.
**M01-03 is corrected and submitted for independent re-acceptance. It is not
accepted. M01-04 has not started.**
