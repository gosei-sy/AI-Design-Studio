# M01 Wave 3 — Response to the independent re-review

**Re-review:** [M01_WAVE3_RE_REVIEW.md](M01_WAVE3_RE_REVIEW.md) · **Decision:** CHANGES REQUIRED (1 P1, 1 P2)
**Response date:** 2026-11-05 · **Author:** Lead Developer
**State:** both findings corrected. **M01-03 awaits independent re-acceptance and is not accepted. M01-04 has not started.**

Both findings are correct. The re-review's central observation is the one worth
leading with:

> the corrections were verified only for fresh, internally consistent data.

That is exactly right, and it explains why 455 tests passed over a defect. The
fixture migrates to latest before any test runs, and the aggregates only ever
build consistent data — so **a suite that can only create new data can only
verify new data.** Neither finding was reachable without starting from a
database that already had rows.

---

## W3-RR-001 — P1 — The denormalized tenant key could disagree with the project

### What was wrong

ADR-0009 A1 denormalized `organization_id` onto the four project-owned roots and
justified not constraining it in one sentence:

> Safe to denormalize because a project's organization is immutable… so the two
> values cannot drift.

**True of mutation, false of creation.** A project's organization cannot change,
so an already-valid pair stays valid — but nothing stopped an invalid pair being
written in the first place. The reviewer ran the obvious probe and PostgreSQL
answered `INSERT 0 1`.

Denormalizing created a **second ownership assertion**, and I left nothing to
make the two agree. The filter then classifies the row by the field that is
wrong. The re-review's sentence is the principle I should have applied when I
wrote the comment: _a global filter cannot repair contradictory ownership data
after it is written._

This is also the third generation of one mistake in this milestone. W3-001 was
"the guard only checks what I thought of". W3-RR-001 is "the comment only
reasons about the case I thought of". Both times the words carried the weight a
mechanism should have.

### Correction

**1. The database refuses the pair.** `app.projects` exposes
`(organization_id, id)` as an alternate key — trivially unique, since `id` alone
is the primary key, but declaring it is what lets a child reference the _pair_.
All five project-owned roots then carry

```
(organization_id, project_id) → projects (organization_id, id)   ON DELETE RESTRICT
```

named `fk_design_contracts_project_tenant`, `fk_design_systems_project_tenant`,
`fk_screens_project_tenant`, `fk_feature_inventories_project_tenant`,
`fk_assets_project_tenant`. `assets` is included: it has carried both ids since
M00 and had the same hole.

**2. The application takes ownership as one value**, which is the re-review's
second required item. A new `ProjectOwnership` in SharedKernel pairs the two ids,
`DesignProject.Ownership` produces one, and the five factories take it instead of
two loose `Guid`s:

```csharp
DesignContract.Create(project.Ownership, clock);
```

**What this is not:** unforgeable. Anything can call `ProjectOwnership.Of` with
two unrelated ids. It is a shape that makes the mistake conspicuous rather than
idiomatic — the guarantee is the constraint, and claiming otherwise would repeat
the "cannot drift" overstatement in a new place. The type's own XML doc says so.

**3. The migration makes the data satisfiable, or stops.** Two problems with two
different right answers:

- **mismatched** — the child disagrees with its project. **Resolved**, because
  the project is the authoritative owner so the value is knowable, with a
  `RAISE NOTICE` of the count per table.
- **orphaned** — the child's `project_id` references no project. **Fails**, with
  `RAISE EXCEPTION` naming the table and count. Nothing can determine the right
  tenant, and deleting a tenant's row is not a migration's decision. The whole
  migration is one transaction, so a failure leaves the schema untouched.

**4. The guard asserts the relationship, not only the filter.** Three new
assertions in `OrganizationScopeGuardTests`: every project-owned root has a
foreign key whose properties are exactly `(OrganizationId, ProjectId)` with
principal key `(OrganizationId, Id)` on `DesignProject`; the project-owned set is
**asserted by value** so a sixth root must be classified deliberately; and
`app.projects` exposes the pair as a key at all.

**5. Per-table constraint tests.** Five parameterized cases prove each table
refuses one tenant's key on another's project, plus five proving the matching
pair still inserts — without that half, a constraint that rejected everything
would pass.

### The approved convention exception

These five foreign keys cross module boundaries, which DATA_MODEL forbade. The
exception was **approved by the Product Owner on 2026-11-05 for this purpose and
no other**, and is recorded as **ADR-0009 A3.1** rather than as a quiet edit to
the conventions table. The conventions table now names the exception and its
scope; the ADR states the reasoning — tenant isolation is a security invariant,
module extractability is a hypothetical future convenience — and says explicitly
that this is **not** permission for arbitrary cross-module foreign keys.

### Evidence

**The reviewer's probe, re-run by hand** on a disposable database migrated to
latest:

```
ERROR:  insert or update on table "design_contracts" violates foreign key
        constraint "fk_design_contracts_project_tenant"
DETAIL: Key (organization_id, project_id)=(11111111-…, bbbbbbbb-…) is not
        present in table "projects".
```

…and the matching pair: `INSERT 0 1`.

**The mismatch resolution**, migrating from `20261103061233` with a bad row in
place:

```
pre-migration    contract.organization_id = 11111111-1111-4111-8111-111111111111
                 project.organization_id  = 66666666-6666-4666-8666-666666666666
post-migration   contract.organization_id = 66666666-6666-4666-8666-666666666666
```

**The orphan failure**, same starting point with a screen pointing at no project:

```
Npgsql.PostgresException (0x80004005): P0001: Cannot enforce project tenant
ownership: app.screens: 1 row(s);  These rows reference a project that does not
exist, so their owning organization is not knowable. Resolve them deliberately —
the migration will not guess, and will not delete tenant data.

exit=1
SELECT count(*) FROM pg_constraint WHERE conname='fk_screens_project_tenant' → 0
```

**Deliberate regression.** `fk_screens_project_tenant` removed from the model:

```
failed OrganizationScopeGuardTests.Every_project_owned_root_is_tied_to_its_projects_tenant
  unconstrained should be empty but had 1 item and was ["Screen"]
```

Restored; 25 architecture tests pass.

**The database-side half of that regression is permanent rather than manual.**
`MigrationUpgradeTests.A_mismatched_tenant_and_project_is_accepted_before_the_constraint_and_refused_after`
migrates to the pre-constraint migration, asserts the reviewer's insert
**succeeds** there, migrates forward, and asserts it is **refused**. A one-sided
test would have been weaker: asserting only that the insert now fails leaves open
whether it ever succeeded, and the finding was precisely that it did.

I also attempted an ad-hoc database-side regression by hand and it was badly
constructed — I had suppressed the migration's output, so the schema never
applied and the probe proved nothing. It is not quoted as evidence. The standing
test above covers that half properly.

---

## W3-RR-002 — P2 — Memberships predating the audit chain had no entry

### What was wrong

A2 created the append-only chain and said nothing about rows that already
existed. The reviewer stopped a database at the prior migration, seeded an active
membership, applied the correction, and got `count(*) = 0`. An auditor would see
different history depending only on whether a grant predated the migration, and
a later role change would become the first entry in a chain with no recorded
origin.

### Correction, with the semantics the Product Owner corrected

My first design was wrong in a way worth recording, because the correction is
subtle and I would not have found it myself. I proposed a baseline carrying
`provisioned_by`, the current role set, and `created_at` — which **asserts that
the person held those roles at that time.** For any membership whose roles
changed before the upgrade that is false, and I would have written a fabrication
into an audit table while believing I was being careful.

The approved semantics state only what the migration could observe:

| Field            | Value                     | Why                                                  |
| :--------------- | :------------------------ | :--------------------------------------------------- |
| `actor`          | `system:migration/<name>` | The migration made this observation, not an operator |
| `occurred_at`    | migration time            | When it was observed, not when the grant happened    |
| `previous_roles` | **NULL**                  | Not recoverable                                      |
| `new_roles`      | the role set as found     | The only observable fact                             |

`provisioned_by` and `created_at` stay on the membership row as the separately
recorded original attribution. A **revoked** legacy membership also gets a
reconstructed `Revoked` entry from its recorded `revoked_by`, `revoked_at` and
role set, because those three fields directly support that statement.

`MigrationBaseline` is **migration-only**:
`OrganizationMembershipTests.The_aggregate_never_produces_a_migration_baseline`
exercises provision, change-roles and revoke and asserts none of them can emit
it. If a runtime path ever did, a reader could no longer tell a reconstructed
chain from an observed one, which is the whole value of a separate type.

### The idempotency flaw, which was real

The Product Owner's third correction caught a defect in my proposed SQL. I had
guarded both inserts with one predicate:

```sql
WHERE NOT EXISTS (SELECT 1 FROM …events WHERE membership_id = m.id)
```

The baseline insert runs first, so by the time the revocation insert evaluated
that guard **an event would already exist** — and every legacy revoked membership
would have silently lost its reconstructed revocation. The guards are now per
event type, and the revocation additionally requires a baseline to exist, so it
applies only to memberships this migration actually baselined.

Verified by extracting the two `INSERT`s from the migration and re-running them
verbatim against an already-migrated database:

```
before re-run   MigrationBaseline x2   Revoked x1
after re-run    MigrationBaseline x2   Revoked x1
```

…and asserted in the upgrade test, which re-applies the migration and compares
the event count.

### Evidence

Migrating from `20261103040427` with one active and one revoked legacy
membership:

```
legacy-operator | MigrationBaseline | actor=system:migration/EnforceProjectTenantOwnershipAndBaselineAudit | prev=NULL | new=3 | at=2026-11-05
legacy-operator | Revoked           | actor=legacy-security                                                | prev=1    | new=NULL | at=2026-10-06
legacy-operator | MigrationBaseline | actor=system:migration/EnforceProjectTenantOwnershipAndBaselineAudit | prev=NULL | new=1 | at=2026-11-05

SELECT count(*) … WHERE type='Provisioned' → 0
```

Nothing is labelled `Provisioned`, because nothing was observed being
provisioned.

### Two consequences I am stating rather than leaving to be discovered

- **The chain is not chronological across the upgrade boundary.** The
  reconstructed `Revoked` entry above carries its real `revoked_at`
  (`2026-10-06`), which precedes the baseline's migration-time stamp
  (`2026-11-05`). Read a chain as "events at their recorded times, plus one
  observation made at migration time" — not as a timeline. Sorting by
  `occurred_at` will look odd, and that is correct rather than broken.
- **A `Revoked` entry on a membership that also has a `MigrationBaseline` was
  reconstructed**, not observed. The baseline's presence is the signal, and
  DATA_MODEL says so.

**S-22 stays partial**, now for two reasons: the chain is append-only by
construction of the aggregate rather than by database privilege, and
pre-migration history is unknowable.

---

## Two defects in my own tests, both the same shape

Worth recording because they nearly produced tests that passed for the wrong
reason.

1. **The assets probe omitted `original_file_name`**, which is `NOT NULL`. The
   insert failed with `23502` instead of the foreign-key `23503` — so the test
   was asserting a constraint it never reached.
2. **The post-migration probe reused a project that already had a contract**, so
   the unique index on `project_id` fired `23505` before the foreign key was
   consulted. Fixed with a second, contract-free project of organization B.

Both are the same lesson: **a probe asserting one constraint must violate only
that constraint.** A row that breaks two tells you nothing about either.

---

## Verification — actual output, 2026-11-05

```
$ dotnet build --configuration Release
  0 Warning(s), 0 Error(s)

$ dotnet test --configuration Release
  total: 474   failed: 0   succeeded: 474   skipped: 0

$ dotnet format --verify-no-changes            → exit 0

$ npm test
  frontend            14 passed (14)
  packages/contracts  164 passed (164)

$ npm run typecheck                            → exit 0
$ npm run lint                                 → exit 0
$ npm run format:check                         → All matched files use Prettier code style!
$ npm run build --workspace frontend           → production build completed

$ npm run test:e2e --workspace frontend        (real Development API on :5080)
  7 passed (45.7s)

$ dotnet-ef migrations has-pending-model-changes …
  No changes have been made to the model since the last migration.

$ npm run audit:prod                           → found 0 vulnerabilities
$ dotnet list package --vulnerable --include-transitive
                                               → no vulnerable packages, 10/10 projects
$ git diff --check                             → exit 0
```

**Per suite:** 262 unit · 25 architecture · 187 integration · 14 frontend · 164
contracts = **652**, plus 7 E2E counted separately. **+19 backend tests** over
the 455 the re-review reproduced, and **0 skipped** — including the one
conditional `Assert.Skip` I had written into the upgrade suite, which is gone: a
test that skips when it finds nothing reports success for having looked.

### Migration from empty

```
Applying migration '20261020053356_InitialSchema'.
Applying migration '20261020094048_ReviewCorrections'.
Applying migration '20261026041140_AddIdentityAndDataProtection'.
Applying migration '20261103040427_AddOrganizationsAndMemberships'.
Applying migration '20261103061233_ScopeTenantRootsAndAuditRoleChanges'.
Applying migration '20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit'.
Done.

$ -- read back
ak_projects_organization_id_id
fk_assets_project_tenant
fk_design_contracts_project_tenant
fk_design_systems_project_tenant
fk_feature_inventories_project_tenant
fk_screens_project_tenant

$ -- ix_projects_organization_id (subsumed by the alternate key)
(empty)
```

### Migration with existing data

Three upgrade tests, each on its own disposable database:
`A_mismatched_tenant_and_project_is_accepted_before_the_constraint_and_refused_after`,
`An_orphaned_project_reference_fails_the_migration_and_adds_no_constraint`,
`A_membership_that_predates_the_audit_chain_gets_an_honest_baseline`, plus
`A_membership_created_after_the_upgrade_has_no_baseline` as the counterpart that
stops a backfill-everything implementation from passing.

**Every disposable database was dropped.**

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

No Stitch call, no spend, no commit, push, PR or deployment, no new
infrastructure, no Production configuration.

**M01-02 remains accepted** with its two recorded non-blocking findings.
**M01-03 is corrected and submitted for independent re-acceptance. It is not
accepted. M01-04 has not started.**
