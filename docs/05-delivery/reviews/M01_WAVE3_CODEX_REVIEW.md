# Independent Review — M01 Wave 3 / M01-03

**Review date:** 2026-11-03  
**Reviewer:** Codex — Independent Technical Architect, Senior Code Reviewer, and QA Lead  
**Decision:** **CHANGES REQUIRED**

## 1. Executive summary

Wave 3 establishes a sound authentication-to-membership boundary, a Development-only
registration route, explicit Member/Approver roles, a fail-closed request scope, a
trusted-operator provisioning host, and database-enforced membership invariants. The
submitted build and test totals were independently reproduced: **444 .NET tests, 14
frontend tests, 164 contract tests, and 7 Playwright tests passed with zero skips** when
the required PostgreSQL, API, and frontend processes were running. A fresh database
accepted all four migrations and exposed the seven claimed membership constraints.

M01-03 is nevertheless not complete. Its central structural isolation mechanism only
filters entities that carry `OrganizationId` directly. `DesignContract`,
`DesignSystem`, `Screen`, and `FeatureInventory` are tenant-owned through `ProjectId`,
have no global filter, and are not discovered by the architecture guard. The required
integration proof covers only projects, despite the acceptance criterion explicitly
requiring project, contract, screen, and asset isolation. This is not currently exposed
through an implemented contract or screen API, but it is a core architectural blocker
for M01-05 and M01-06 and means M01-03's acceptance claim is false.

The provisioning path also fails to persist who performed a role change. Re-provisioning
can grant `Approver` while leaving `ProvisionedBy` attributed to the original operator.
That makes a security-sensitive grant incorrectly attributable in the authoritative
database state.

There are **2 P1** and **2 P2** findings. M01-04 must not begin under the documented
dependency rule until the two P1 findings are corrected and independently re-verified.

## 2. Acceptance criteria matrix

| Criterion                                                                                  | Result           | Independent evidence                                                                                                                                       |
| :----------------------------------------------------------------------------------------- | :--------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------- |
| M01-02 external acceptance is recorded without closing D-8 or MFA recovery-code risk       | **PASS**         | Plan, status, README, and Wave 3 report record the supplied decision; D-8 and the MFA recovery-code risk remain open.                                      |
| D-3 and D-9 are resolved by an accepted ADR                                                | **PASS**         | ADR-0009 records Member/Approver, trusted operator provisioning, one active membership, and server-side membership resolution.                             |
| Membership is keyed by immutable user ID and database constrained                          | **PASS**         | Domain model, foreign keys, checks, and filtered unique index were inspected; all seven constraints were read from a freshly migrated PostgreSQL database. |
| Public registration is unavailable outside Development                                     | **PASS**         | Source and passing integration tests cover anonymous and authenticated callers outside Development.                                                        |
| Organization and roles are resolved from server-side membership, not client input          | **PASS**         | Endpoint/context source and integration tests cover header, query, route/body, claim, and global Identity-role attempts.                                   |
| Every tenant query is structurally organization-scoped                                     | **FAIL**         | Only `DesignProject`, `AssetReference`, and `JobRecord` have query filters. Four project-owned aggregate roots are unfiltered. See W3-001.                 |
| Integration test proves organization A cannot read B's project, contract, screen, or asset | **FAIL**         | The named test creates and queries projects only. No contract, screen, or asset is exercised. See W3-001.                                                  |
| Architecture test catches any missing tenant filter                                        | **FAIL**         | Discovery is limited to direct `OrganizationId` properties and cannot see project-owned entities. See W3-001.                                              |
| Member/Approver mechanism and Approver policy exist                                        | **PASS**         | Policies and domain guard are tested. No approve/reject/waive endpoint exists yet; endpoint enforcement remains M01-05 work and was not required here.     |
| Every grant, role change, and revocation is attributable                                   | **FAIL**         | Initial grants and revocations are persisted, but role changes retain the initial `ProvisionedBy`. See W3-002.                                             |
| Migration is synchronized and applies from empty                                           | **PASS**         | No pending model change; all four migrations applied to a disposable empty database.                                                                       |
| Build, automated tests, formatting, lint, and production frontend build                    | **PASS**         | Independently executed; results are in section 6.                                                                                                          |
| Browser flow                                                                               | **PASS locally** | 7/7 passed with the real Development API and HTTPS frontend. CI execution remains **NOT VERIFIED**.                                                        |
| Docker Compose validation                                                                  | **BLOCKED**      | `docker` is not installed; exit 127.                                                                                                                       |

## 3. Findings

### W3-001 — P1 — Tenant isolation omits project-owned aggregate roots

**Locations**

- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:104`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:114`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:156`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:187`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:222`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:263`
- `backend/src/AIDesignStudio.Infrastructure/Persistence/AppDbContext.cs:301`
- `backend/tests/AIDesignStudio.ArchitectureTests/OrganizationScopeGuardTests.cs:57`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthorizationTests.cs:621`
- `IMPLEMENTATION_PLAN.md:227`
- `docs/02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md:66`
- `docs/05-delivery/M01_WAVE3_REPORT.md:365`

**Evidence**

`AppDbContext` defines query filters only for `DesignProject`, `AssetReference`, and
`JobRecord`. `DesignContract`, `DesignSystem`, `Screen`, and `FeatureInventory` carry
`ProjectId` and have no tenant filter. The architecture guard discovers only CLR types
with a direct `OrganizationId` property, so it cannot report those four omissions.

`Organization_a_cannot_read_organization_bs_rows` seeds and reads two projects only.
It does not create or query the contract, screen, or asset named by the acceptance
criterion. The report's mapping of that test to “organization B's resources” therefore
overstates the evidence. Deliberately removing the project filter proves only that the
guard can detect a missing filter from the already-discovered direct-property set.

**Why it matters**

An M01-05 or M01-06 handler that queries a contract, design system, screen, or inventory
by primary key can return another tenant's row unless the author manually remembers a
project join. The stated fail-closed property and ADR-0009's claim that omission cannot
cause a leak are not true for those roots. No currently implemented HTTP endpoint was
found exploiting this path; the defect is a structural blocker before those endpoints
are added.

**Required correction**

1. Apply a fail-closed tenant filter to every tenant-owned aggregate root, including
   those owned through `ProjectId`, or remodel ownership so the tenant key is directly
   and consistently filterable.
2. Expand the architecture guard to discover direct and indirect tenant ownership. It
   must fail when any project-owned aggregate root loses its filter.
3. Add real two-tenant integration tests covering list and by-ID access for project,
   contract, design system, screen, feature inventory, asset, and job paths as applicable.
4. Deliberately regress at least one indirect root and show that the production-model
   guard and integration proof fail.
5. Reconcile ADR-0009 and the Wave 3 evidence mapping to what the tests actually prove.

**Blocks M01-04:** **Yes.** M01-03's documented acceptance criteria are unmet, and
M01-04 depends on accepted M01-03. The project filter itself works, but proceeding would
institutionalize a tenant-isolation mechanism that fails for the immediately following
contract/design-system tasks.

### W3-002 — P1 — Re-provisioned role grants retain the original operator attribution

**Locations**

- `backend/src/AIDesignStudio.Domain/Organizations/OrganizationMembership.cs:89`
- `backend/src/AIDesignStudio.Domain/Organizations/OrganizationMembership.cs:159`
- `backend/src/AIDesignStudio.Infrastructure/Organizations/OperatorProvisioningService.cs:105`
- `backend/src/AIDesignStudio.Infrastructure/Organizations/OperatorProvisioningService.cs:232`
- `backend/src/AIDesignStudio.Infrastructure/Organizations/OperatorProvisioningService.cs:262`
- `backend/tests/AIDesignStudio.IntegrationTests/AuthorizationTests.cs:382`
- `docs/02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md:100`

**Evidence**

The service constructs the current `provisionedBy` value and passes it into
`ApplyMembershipAsync`. For an existing active membership, that method calls
`active.ChangeRoles(roles, clock)` and discards the current actor. `ChangeRoles` changes
only `Roles` and the aggregate update timestamp. The integration test verifies the new
role but does not verify attribution after re-provisioning.

Consequently, if operator A initially grants Member and operator B later grants
Approver, the membership returned by the API and stored in PostgreSQL still names A as
`ProvisionedBy`. A log message names B, but the authoritative membership record does
not.

**Why it matters**

Granting Approver is a privileged authorization change. The implementation and ADR say
every role change is operator-controlled and auditable, but the durable state
misattributes that grant. Logs are not a substitute for an authoritative audit record
unless their durability, integrity, retention, and correlation are explicitly designed.

**Required correction**

Persist the actor and time for every role change. Prefer an append-only membership audit
record that captures old roles, new roles, actor, time, membership, and organization;
at minimum, the authoritative model must distinguish initial provisioning from the most
recent role change without overwriting history. Add a test in which different actors
create the membership, grant Approver, change roles, and revoke it, then verify the
persisted audit chain.

**Blocks M01-04:** **Yes.** This violates the accepted account-proofing/audit decision
inside M01-03, which M01-04 depends on.

### W3-003 — P2 — Current milestone documents contain mutually exclusive Wave 2 and Wave 3 states

**Locations**

- `IMPLEMENTATION_PLAN.md:14`
- `IMPLEMENTATION_PLAN.md:179`
- `IMPLEMENTATION_PLAN.md:529`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:6`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:17`
- `docs/05-delivery/IMPLEMENTATION_STATUS.md:116`

**Evidence**

The plan's current-state section still says Wave 2/M01-02 is the only authorized scope,
and the M01-02 task still says it is not accepted, while the task table correctly says
M01-02 was accepted and M01-03 was implemented. The appendix has a malformed M01-03 row
that says “Not started.”

The status header says Wave 3, but its authorization paragraph says nothing beyond Wave
2 has started. The M01 milestone row first says M01-03 is implemented and D-9 resolved,
then later in the same cell says M01-02 awaits acceptance, D-9 is unresolved, and
M01-03 is not authorized. `Last updated` is also 2026-11-02 despite the 2026-11-03
state.

**Why it matters**

These are operational control documents used to decide what work is authorized. Their
contradictions can cause either unauthorized work or unnecessary stoppage and undermine
the traceability claimed by the Wave 3 report.

**Required correction**

Reconcile every current-state section to one dated Wave 3 state, while preserving older
facts only in explicitly historical snapshots or changelog entries. Fix the malformed
appendix table row and sweep by claim, not only by exact wording.

**Blocks M01-04:** **No by itself**, but it must be corrected as part of the Wave 3
response.

### W3-004 — P2 — Provisioning evidence puts a literal password in shell history and process arguments

**Location:** `docs/05-delivery/M01_WAVE3_REPORT.md:398`

**Evidence**

The report correctly warns that `--password hunter2` exposes a password in shell history
and `/proc/<pid>/cmdline`, but the successful example immediately uses:

```text
echo 'Correct-Horse-9-Battery!' | aids-provision ...
```

The literal remains in shell history and may be visible in the `echo` process command
line. The tool does not receive it as its own argument, but the operator procedure still
exposes it through the producer process.

**Why it matters**

This is the documented procedure for trusted operator provisioning. Copying it creates
the exact local secret exposure the CLI is designed to reject.

**Required correction**

Replace literal `echo` examples with a hidden interactive read or an approved secret
source that feeds stdin without placing the value in command arguments, environment
defaults, logs, or shell history. Add an operator note explaining that stdin alone is
not safe if the upstream producer exposes the literal.

**Blocks M01-04:** **No**, but the unsafe runbook must be corrected before operational
use.

## 4. Architecture assessment

The module split remains appropriate for a modular monolith. Authentication and
organization contracts live in Application, the aggregate and role rules live in
Domain, persistence and Identity integration live in Infrastructure, and HTTP/session
concerns remain in the API host. Making organization scoping a required composition-root
choice is a strong fail-closed design for API, worker, and provisioning hosts.

The main architectural failure is treating direct `OrganizationId` as equivalent to
tenant ownership. Tenant ownership is transitive through the project aggregate; the EF
model and guard must represent that relationship. Until W3-001 is corrected, the model
does not provide the structural isolation ADR-0009 promises.

The dedicated provisioning host is proportionate for the MVP and avoids a network grant
surface. Its durable audit model is incomplete for role changes (W3-002). No new broker,
cache, database, or external service was introduced.

## 5. Security assessment

Verified strengths:

- Authentication is required by default and the real browser flow preserves HttpOnly,
  Secure, SameSite cookie behavior through the same-origin proxy.
- Antiforgery rejection is exercised in Chromium.
- Public registration is Development-only and grants no membership.
- Membership uses immutable user IDs and server-side state; client-supplied tenant and
  role inputs do not establish authorization.
- Database checks, foreign keys, and the one-active-membership index provide defense in
  depth.
- No vulnerable NuGet or production npm packages were reported by the executed audits.
- `BillableOperationsEnabled` remained false; no Stitch/provider command was executed.

Security corrections required:

- W3-001 must close the indirect tenant-query path before contract/screen/design-system
  endpoints exist.
- W3-002 must make privilege changes durably attributable.
- W3-004 must remove the secret-exposing operator example.

Known open risks were not misclassified as new Wave 3 defects: D-8 reset delivery, D-5
Data Protection key encryption, MFA recovery codes, D-1/D-2 provider access/spend, and
the inability to start the Production provisioning host until D-8 is resolved.

## 6. Test and command execution

| Command                                                                        | Outcome                                                                                                                                                                          |
| :----------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet build --configuration Release`                                         | **PASS** — 0 warnings, 0 errors; all 10 projects built.                                                                                                                          |
| `dotnet test --configuration Release`                                          | **PASS** — 444 succeeded, 0 failed, 0 skipped. PostgreSQL-backed integration tests ran.                                                                                          |
| `dotnet format --verify-no-changes`                                            | **PASS** — exit 0, no output.                                                                                                                                                    |
| `npm test`                                                                     | **PASS on sequential rerun** — 14 frontend and 164 contract tests. An earlier resource-contended parallel review run timed out frontend workers; it was not counted as evidence. |
| `npm run typecheck`                                                            | **PASS** — both TypeScript workspaces, exit 0.                                                                                                                                   |
| `npm run lint`                                                                 | **PASS** — ESLint exit 0.                                                                                                                                                        |
| `npm run format:check`                                                         | **PASS** — Prettier reported all matched files compliant.                                                                                                                        |
| `npm run build --workspace frontend`                                           | **PASS** — production build compiled and generated all four static pages.                                                                                                        |
| `npm run test:e2e --workspace frontend` without API                            | **FAIL as expected for incomplete environment** — 3 passed, 4 failed; all four failures traced to `ECONNREFUSED 127.0.0.1:5080`.                                                 |
| Start real Development API, then rerun `npm run test:e2e --workspace frontend` | **PASS** — 7 passed, 0 failed, 0 skipped.                                                                                                                                        |
| `dotnet-ef migrations has-pending-model-changes ...`                           | **PASS** — no model changes. Warning: tool 10.0.12 is older than runtime 10.0.13.                                                                                                |
| Apply migrations to empty `aids_codex_review_20261103`                         | **PASS** — all four migrations applied; 7 membership constraints and expected indexes read back; disposable database then dropped.                                               |
| `dotnet list package --vulnerable --include-transitive`                        | **PASS** — no vulnerable packages in all 10 projects.                                                                                                                            |
| `npm run audit:prod`                                                           | **PASS** — 0 vulnerabilities.                                                                                                                                                    |
| `git diff --check`                                                             | **PASS** — exit 0.                                                                                                                                                               |
| `docker compose config`                                                        | **BLOCKED** — `docker: command not found`, exit 127.                                                                                                                             |

The first unqualified `dotnet-ef database update` targeted the repository's configured
Development database and applied the pending Wave 3 migration there. The fresh-database
validation was then repeated with an explicit review-only database, verified, and that
database was dropped. No source file was changed by the review.

## 7. Documentation integrity

The Wave 3 report's measured build/test totals are reproducible, and its disclosed gaps
around D-8, D-5, MFA recovery, CI, Docker, and future approval endpoints are accurate.

The following claims are not supported:

- “Every organization-scoped query path is guarded” — the guard cannot discover
  project-owned roots.
- “Organization A cannot read organization B's resources” as evidence for the full
  acceptance criterion — the test covers projects only.
- “Tenant isolation is structural” for all future CRUD — true only for the three roots
  with direct filters.
- Current plan/status state — contradicted by stale Wave 2 text in the same documents.

Historical results should remain historical; only current-state sections need
reconciliation.

## 8. Required corrections

Before M01-03 can be accepted:

1. Correct and prove tenant scoping for all direct and project-owned tenant roots
   (W3-001).
2. Persist a complete, attributable audit record for role changes (W3-002).
3. Reconcile plan/status current-state documentation (W3-003).
4. Replace the unsafe password example and clarify the stdin boundary (W3-004).
5. Rerun build, all .NET tests with zero skips, frontend/contracts tests, formatting,
   migration sync, and the relevant tenant/audit regression tests.

CI E2E and Docker remain separately tracked M01-11/M01-12 items; they are not the basis
for this `CHANGES REQUIRED` decision.

## 9. M01 readiness decision

**CHANGES REQUIRED**

M01-02 remains accepted with its recorded non-blocking findings. M01-03 is not accepted.
Under the documented dependency chain, **M01-04 must not start until W3-001 and W3-002
are corrected and M01-03 is independently re-accepted**. No M01-04 implementation was
reviewed or authorized by this decision.

No Stitch call, billable operation, commit, push, PR, deployment, infrastructure
installation, or source-code correction was performed during this review.
