# M01 Wave 3 — Independent Re-review Round 2

**Reviewer:** Codex — Independent Technical Architect, Senior Code Reviewer, QA Lead  
**Date:** 2026-11-05  
**Scope:** Re-verification of W3-RR-001 and W3-RR-002 corrections  
**Decision:** **CHANGES REQUIRED**

## Executive summary

W3-RR-001 is corrected and independently verified. The five project-owned roots
use a single `ProjectOwnership` input in the domain and composite
`(organization_id, project_id)` foreign keys to the project's alternate key in
PostgreSQL. The upgrade migration repairs mismatches from the authoritative
project, refuses orphans transactionally, and the model guards cover the exact
root set and relationship shape.

W3-RR-002 is not yet complete. The new baseline has honest field semantics for
memberships that genuinely predate the event chain, but the migration also adds
a baseline to memberships that already have an observed audit event. This case
exists in the real upgrade window between the migration that introduced the
event table and the new baseline migration. The automated idempotency assertion
does not execute the backfill a second time, and the migration cannot be rolled
back once it has inserted a baseline.

All declared quality gates passed, including 474 backend tests with zero skips
and 7/7 Playwright tests. Those results do not cover the failing upgrade-window
or downgrade cases reproduced during this review.

## Acceptance matrix

| Review item                                                           | Result      | Evidence                                                                                                                              |
| :-------------------------------------------------------------------- | :---------- | :------------------------------------------------------------------------------------------------------------------------------------ |
| W3-RR-001 — enforce project/tenant ownership consistency              | **PASS**    | Alternate key, five composite FKs, exact-set architecture guard, mismatch repair, orphan failure, and direct FK rejection verified    |
| W3-RR-002 — honest baseline for memberships predating the audit chain | **FAIL**    | Baseline semantics are honest for pre-chain rows, but already-audited rows in the migration window receive a false/redundant baseline |
| W3-RR-002 — repeatable/idempotent backfill evidence                   | **FAIL**    | The test migrates an already-current database to current again; EF executes no backfill SQL                                           |
| Migration rollback                                                    | **FAIL**    | Downgrade fails with PostgreSQL `23514` when the old event-type constraint encounters `MigrationBaseline` rows                        |
| Release build and automated suites                                    | **PASS**    | Build 0/0; backend 474/0/0; frontend 14; contracts 164; E2E 7/7                                                                       |
| Docker Compose validation                                             | **BLOCKED** | `docker: command not found`, exit 127                                                                                                 |

## Findings

### W3-RR2-001 — P2 — The baseline migration also baselines memberships that already have observed events

**Location:**

- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit.cs:235`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit.cs:248`
- `backend/tests/AIDesignStudio.IntegrationTests/MigrationUpgradeTests.cs:361`

**Evidence:** The baseline `INSERT` excludes only an existing event of type
`MigrationBaseline`. It does not exclude a membership that already has an
observed `Provisioned`, `RolesChanged`, or `Revoked` event. The test at line 372
creates its membership after the latest migration has already been applied, so
it does not exercise the upgrade window.

This review created a disposable database, migrated it to
`20261103061233_ScopeTenantRootsAndAuditRoleChanges`, inserted a membership and
its real observed `Provisioned` event, and then applied
`20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit`. The resulting
chain was:

```text
Provisioned       | observed-operator
MigrationBaseline | system:migration/EnforceProjectTenantOwnershipAndBaselineAudit
```

**Why it matters:** `MigrationBaseline` is documented as the migration's honest
substitute for history that predates the audit chain. Adding it to a membership
whose provisioning was already observed misclassifies the chain and makes the
baseline-presence signal unreliable for distinguishing reconstructed from
observed history. For an already-revoked membership in this window, the same
predicate can also make the migration treat it as legacy.

**Recommended correction:** Baseline only memberships with no pre-existing
membership events. Keep the reconstructed revocation guard per event type and
condition it on the baseline inserted for genuinely pre-chain rows. Add an
upgrade-path test that stops at migration `20261103061233`, creates a membership
through the then-current audited path (or inserts its observed event faithfully),
applies the new migration, and proves no baseline or reconstructed duplicate is
added.

**Blocks M01-03:** Yes. This is incorrect durable authorization-audit history on
a supported database upgrade path.

### W3-RR2-002 — P2 — The automated idempotency test is a no-op

**Location:**

- `backend/tests/AIDesignStudio.IntegrationTests/MigrationUpgradeTests.cs:340`
- `backend/tests/AIDesignStudio.IntegrationTests/MigrationUpgradeTests.cs:349`
- `docs/05-delivery/reviews/M01_WAVE3_RE_REVIEW_RESPONSE.md:222`

**Evidence:** After the database is already at the latest migration, line 349
calls `MigrateAsync(connectionString)` again with no target. EF sees no pending
migration and executes no SQL. The unchanged row count therefore proves only
that EF does not apply an already-recorded migration twice; it does not prove
that either backfill `INSERT` is idempotent. The response claims the extracted
SQL was manually re-run, but the permanent test described as asserting this
property does not do so.

**Why it matters:** Per-event idempotency was explicitly identified as
load-bearing for avoiding duplicate audit records and preserving reconstructed
revocations. Project rule 4 requires important invariants to have automated
tests; this one currently has only manual evidence.

**Recommended correction:** Exercise the actual backfill statements a second
time in an automated upgrade-path test, preferably by putting the SQL in a
testable migration helper/source shared with the migration or by executing the
generated migration SQL under a controlled setup. Assert counts by event type
and membership, not only a global total.

**Blocks M01-03:** Yes, together with W3-RR2-001. The claimed permanent evidence
for a load-bearing audit invariant is absent.

### W3-RR2-003 — P2 — The new migration cannot be rolled back after inserting a baseline

**Location:**

- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit.cs:281`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit.cs:319`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit.cs:351`

**Evidence:** `Down()` removes the expanded event-type constraint and immediately
recreates the old constraint, which allows only `Provisioned`, `RolesChanged`,
and `Revoked`. It does not remove or otherwise handle rows of type
`MigrationBaseline` first.

The disposable database used above was then downgraded to
`20261103061233_ScopeTenantRootsAndAuditRoleChanges`. The command exited 1 with:

```text
23514: check constraint "ck_organization_membership_events_type"
of relation "organization_membership_events" is violated by some row
```

The migration transaction rolled back, leaving the database at the latest
version.

**Why it matters:** An emergency rollback of this migration fails on the data
the migration itself creates. The failure is deterministic for every database
with at least one membership. There is no automated downgrade test to expose it.

**Recommended correction:** Define and document the rollback semantics for
`MigrationBaseline` rows, then make `Down()` compatible with them before
restoring the old check constraint. Add an upgrade-then-downgrade integration
test with at least one baselined membership. If the architectural decision is
that this migration is intentionally irreversible, encode that explicitly and
document the operational recovery procedure rather than shipping a `Down()`
that begins and then fails on expected data.

**Blocks M01-03:** Yes. The newly introduced authorization schema migration has
a broken rollback path.

## Architecture and security assessment

The project/tenant correction is architecturally sound. `ProjectOwnership`
reduces accidental parameter mismatch without claiming to be the security
boundary; PostgreSQL remains the enforcement point. ADR-0009 A3.1 narrowly
records the cross-module referential-integrity exception rather than weakening
the general module convention. The exact-set model guard and database upgrade
tests materially improve coverage.

The remaining findings are audit-integrity and delivery-safety issues, not
cross-tenant data exposure. No new authentication or authorization bypass was
found. S-22 correctly remains partial. The baseline bug nevertheless makes the
documented observed-versus-reconstructed distinction unreliable for a real
migration window and must be corrected before accepting M01-03.

## Commands run

| Command                                                                  | Outcome                                                                                  |
| :----------------------------------------------------------------------- | :--------------------------------------------------------------------------------------- |
| `dotnet build --configuration Release`                                   | **PASS** — 0 warnings, 0 errors                                                          |
| `dotnet test --configuration Release --no-build`                         | **PASS** — 474 passed, 0 failed, 0 skipped; 262 unit / 25 architecture / 187 integration |
| `dotnet format --verify-no-changes`                                      | **PASS** — exit 0                                                                        |
| `npm test`                                                               | **PASS** — 14 frontend and 164 contracts                                                 |
| `npm run typecheck`                                                      | **PASS** — exit 0                                                                        |
| `npm run lint`                                                           | **PASS** — exit 0                                                                        |
| `npm run format:check`                                                   | **PASS** — Prettier clean                                                                |
| `npm run build --workspace frontend`                                     | **PASS** — production build completed                                                    |
| `npm run test:e2e --workspace frontend` with the Development API running | **PASS** — 7 passed, 0 failed, 0 skipped                                                 |
| `dotnet-ef migrations has-pending-model-changes ...`                     | **PASS** — no model changes since the last migration                                     |
| Fresh database to migration 5, seed observed event, migrate to latest    | **FAIL as a product probe** — unexpected `MigrationBaseline` added                       |
| Downgrade that database from migration 6 to migration 5                  | **FAIL** — exit 1, PostgreSQL `23514`                                                    |
| `npm run audit:prod`                                                     | **PASS** — 0 vulnerabilities                                                             |
| `dotnet list package --vulnerable --include-transitive`                  | **PASS** — no vulnerable packages across 10 projects                                     |
| `git diff --check`                                                       | **PASS** — exit 0                                                                        |
| `docker compose config`                                                  | **BLOCKED** — Docker is not installed, exit 127                                          |

`dotnet-ef` emitted a tool/runtime version warning (`10.0.12` tool versus
`10.0.13` runtime), but the EF commands themselves completed as reported except
for the intentionally exercised downgrade failure.

The disposable review database was dropped after the probes. No source code,
schema, migration, configuration, provider setting, or existing documentation
was modified by this review.

## Required corrections

1. Restrict baseline creation to memberships with no prior audit events and add
   the missing migration-window test.
2. Replace the no-op idempotency assertion with a test that actually executes
   the backfill twice and asserts per-membership/per-type results.
3. Make the downgrade path work for expected baseline data, or formally record
   and operationalize an intentional irreversibility decision.
4. Re-run the full zero-skip gates and the targeted upgrade/downgrade probes.

## Decision

**CHANGES REQUIRED.** W3-RR-001 is accepted. W3-RR-002 is not yet accepted, and
M01-03 remains unaccepted. M01-04 must not start under the milestone sequencing
rule.
