# Independent Re-review — M01 Wave 3 / M01-03

**Review date:** 2026-11-03  
**Reviewed response:** `M01_WAVE3_RESPONSE.md`  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

The response materially improves both original P1 areas. All seven tenant roots now
have filters, the architecture guard partitions the complete EF model, the two-tenant
integration tests cover list and primary-key reads, new role changes persist their
actor, and the API roster reports the latest role-change attribution. W3-003 and
W3-004 are fully corrected.

The submitted verification totals were independently reproduced: **455 backend tests,
14 frontend tests, 164 contract tests, and 7 E2E tests passed with zero skips**. Build,
formatting, lint, type checking, migration synchronization, frontend production build,
and dependency audits also passed.

M01-03 is still not acceptable because the corrections were verified only for fresh,
internally consistent data. An upgrade-path test with pre-existing rows reproduced two
gaps:

1. PostgreSQL accepts a tenant-owned aggregate whose `organization_id` belongs to
   organization A while its `project_id` identifies organization B's project. The
   global filter then treats the row as A's, despite the project relationship saying B.
   The documentation's claim that the denormalized keys “cannot drift” is therefore
   false.
2. A membership that exists before the correction migration receives no audit event.
   The new chain is complete only for memberships created after the migration.

There are **1 P1** and **1 P2** findings. M01-04 remains gated on M01-03 re-acceptance.

## 2. Original finding disposition

| Original finding                                      | Result                  | Evidence                                                                                                                                                                             |
| :---------------------------------------------------- | :---------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| W3-001 — tenant isolation omitted project-owned roots | **PARTIALLY CORRECTED** | Seven filters, complete-model guard, and two-tenant tests are present and pass. The denormalized tenant key is not constrained to match the owning project's tenant; see W3-RR-001.  |
| W3-002 — role changes retained original attribution   | **PARTIALLY CORRECTED** | New grants/changes/revocations create attributed events and roster metadata. Existing memberships are not represented in the new event table after upgrade; see W3-RR-002.           |
| W3-003 — contradictory milestone documents            | **VERIFIED CORRECTED**  | Current-state plan/status sections consistently show M01-02 accepted, M01-03 in re-review, and M01-04 not started. Historical measurements remain labelled as snapshots.             |
| W3-004 — literal password in operator example         | **VERIFIED CORRECTED**  | Runbook and CLI help use hidden interactive input or a shell variable with builtin `printf`; both state that stdin is only as safe as its producer. A guard test covers the warning. |

## 3. Findings

### W3-RR-001 — P1 — Denormalized tenant and project keys can identify different organizations

**Locations**

- `backend/src/AIDesignStudio.Domain/DesignContracts/DesignContract.cs:49`
- `backend/src/AIDesignStudio.Domain/DesignContracts/DesignContract.cs:68`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:96`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:125`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:259`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261103061233_ScopeTenantRootsAndAuditRoleChanges.cs:61`
- `docs/02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md:190`
- `docs/02-architecture/DATA_MODEL.md:390`

**Evidence from source**

The four corrected aggregate factories accept `organizationId` and `projectId` as two
independent GUIDs and can validate only that neither is empty. `AssetReference` already
has the same shape. `AppDbContext` adds filters and indexes but no relationship or
constraint tying `(organization_id, project_id)` to the corresponding project's
`(organization_id, id)`. The migration backfills existing rows correctly but likewise
adds no continuing constraint.

Project organization immutability prevents an already-valid pair from changing; it does
not prevent an invalid pair from being created initially.

**Execution proof**

A disposable database was migrated through
`20261103040427_AddOrganizationsAndMemberships`, then seeded with organization A,
organization B, and one project per organization. After applying
`20261103061233_ScopeTenantRootsAndAuditRoleChanges`, PostgreSQL accepted:

```sql
INSERT INTO app.design_contracts
    (id, project_id, organization_id, created_at)
VALUES
    (..., <organization-B-project>, <organization-A>, now());
```

The insert returned `INSERT 0 1`. Reading the row and its project returned two different
tenant keys:

```text
contract.organization_id = 11111111-1111-4111-8111-111111111111
project.organization_id  = 66666666-6666-4666-8666-666666666666
```

The query-filter and two-tenant tests do not attempt this state, so all 455 tests pass
while the database permits it.

**Why it matters**

The filter classifies the inconsistent contract as organization A's data even though it
occupies organization B's project. This can expose or mutate a child through the wrong
tenant context and can deny organization B its one-contract-per-project slot. The same
class of inconsistency is possible for design systems, screens, feature inventories,
and assets. No current contract/screen/design-system endpoint exposes the path, but
M01-05 and M01-06 will create exactly those paths.

**Required correction**

1. Enforce project ownership in PostgreSQL for every project-owned tenant root. One
   suitable design is a unique/alternate key on projects `(organization_id, id)` plus
   composite foreign keys from `(organization_id, project_id)` on contracts, design
   systems, screens, feature inventories, and assets.
2. Derive the organization key from a scoped, loaded project in application paths; do
   not accept two unrelated client-derived identifiers.
3. Make the migration detect and resolve or fail clearly on orphaned/mismatched existing
   rows before adding the constraints.
4. Add database tests proving that each project-owned table rejects a mismatched pair,
   and extend the model guard to detect a missing tenant/project relationship.
5. Regress one composite relationship deliberately and show the guard and integration
   proof fail.

**Blocks M01-04:** **Yes.** This is a core tenant-ownership invariant inside M01-03,
and M01-04 is documented as depending on accepted M01-03.

### W3-RR-002 — P2 — Upgrade migration leaves pre-existing memberships outside the audit chain

**Locations**

- `backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261103061233_ScopeTenantRootsAndAuditRoleChanges.cs:94`
- `backend/src/AIDesignStudio.Domain/Organizations/OrganizationMembership.cs:119`
- `docs/02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md:216`

**Evidence from source**

The correction migration creates `auth.organization_membership_events` but contains no
backfill or baseline operation for rows already present in
`auth.organization_memberships`. Current tests create databases at the latest migration
or create memberships through the corrected aggregate, so they cannot detect this
upgrade gap.

**Execution proof**

The disposable database was stopped at the prior migration and seeded with an active
membership carrying `provisioned_by = 'legacy-operator'`. The correction migration
applied successfully. This query then returned `0`:

```sql
SELECT count(*)
FROM auth.organization_membership_events
WHERE membership_id = <pre-existing-membership>;
```

The membership row still contains its original attribution, but the documented
“one entry per provision, role change and revocation” event chain does not include it.

**Why it matters**

Auditors see different history depending solely on whether the grant occurred before or
after this migration. A later role-change event can become the first event in the chain,
making it appear that the membership had no recorded origin. This is a migration
correctness issue, not a failure of the corrected runtime aggregate.

**Required correction**

Add and test an explicit upgrade policy for existing memberships. Because the old defect
means prior role-change history may be unknowable, do not fabricate a complete history.
Either:

- insert an honestly labelled migration/baseline event describing the state and known
  attribution at the migration boundary, plus reconstructable revocation metadata; or
- fail the migration when old rows exist and require a documented operator migration
  procedure.

Add a migration test that starts at the previous migration with active and revoked
memberships, applies the correction, and verifies the resulting audit semantics.

**Blocks M01-04:** **Yes through the milestone dependency.** It is not a project-CRUD
defect, but W3-002 is not completely corrected and M01-03 remains unaccepted.

## 4. Architecture and security assessment

The new whole-model partition is a substantial improvement over the original guard. It
now catches unknown EF entity types, confirms all tenant roots have filters, confirms
the filter expressions read the request scope, and makes exemptions reviewable. The
two-tenant and closed-scope tests exercise all seven roots and primary-key reads.

The remaining architectural gap is referential, not filtering: denormalization creates
a second tenant-ownership assertion, so the database must guarantee both assertions
agree. A global filter cannot repair contradictory ownership data after it is written.

Runtime role-change attribution is now sound for corrected-code writes. The event is
created in the aggregate and saved with the state change, and `RolesChangedBy` no longer
overwrites `ProvisionedBy`. The explicitly documented limitation around database-level
tamper resistance remains acceptable for S-22 partial. The missing upgrade baseline in
W3-RR-002 must still be addressed.

No new external service or infrastructure was added. No Stitch command or billable
operation was executed during this review.

## 5. Test and command execution

| Command/check                                             | Outcome                                                                                                                               |
| :-------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------ |
| `dotnet build --configuration Release`                    | **PASS** — 0 warnings, 0 errors; all 10 projects built.                                                                               |
| `dotnet test --configuration Release --no-build`          | **PASS** — 455 succeeded, 0 failed, 0 skipped. Integration tests ran against PostgreSQL.                                              |
| `dotnet format --verify-no-changes`                       | **PASS** — exit 0, no output.                                                                                                         |
| `npm test`                                                | **PASS** — 14 frontend and 164 contract tests.                                                                                        |
| `npm run typecheck`                                       | **PASS** — frontend and contracts, exit 0.                                                                                            |
| `npm run lint`                                            | **PASS** — ESLint exit 0.                                                                                                             |
| `npm run format:check`                                    | **PASS** — all matched files use Prettier style.                                                                                      |
| `npm run build --workspace frontend`                      | **PASS** — production build and four static pages completed.                                                                          |
| Development API + `npm run test:e2e --workspace frontend` | **PASS** — 7 passed, 0 failed, 0 skipped.                                                                                             |
| `dotnet-ef migrations has-pending-model-changes ...`      | **PASS** — no model changes; tool 10.0.12/runtime 10.0.13 warning remains.                                                            |
| Previous migration → seeded rows → latest migration       | **PASS mechanically**, but exposed both findings: mismatched tenant/project insert accepted; pre-existing membership event count `0`. |
| `npm run audit:prod`                                      | **PASS** — 0 vulnerabilities.                                                                                                         |
| `dotnet list package --vulnerable --include-transitive`   | **PASS** — no vulnerable packages across 10 projects.                                                                                 |
| `git diff --check`                                        | **PASS** — exit 0.                                                                                                                    |
| `docker compose config`                                   | **BLOCKED** — `docker: command not found`, exit 127.                                                                                  |

The disposable re-review database was dropped after the upgrade-path probes. No source,
configuration, schema, or test file was changed by this review.

## 6. Required corrections and readiness

Before M01-03 can be accepted:

1. Enforce and test tenant/project key consistency for every project-owned root
   (W3-RR-001).
2. Define and test an honest audit baseline for memberships that predate the event table
   (W3-RR-002).
3. Amend ADR-0009 A1/A2 and the data-model claims so they describe the corrected
   database and upgrade guarantees precisely.
4. Rerun the full 455-test baseline with zero skips, the new upgrade/constraint tests,
   migration synchronization, frontend gates, and E2E.

## 7. Decision

**CHANGES REQUIRED**

W3-003 and W3-004 are closed. W3-001 and W3-002 are improved but not fully closed.
M01-02 remains accepted with its existing non-blocking findings. M01-03 remains
unaccepted, and **M01-04 must not start until these two corrections are independently
re-verified**.

No commit, push, PR, deployment, provider call, spend, infrastructure installation, or
source-code correction was performed.
