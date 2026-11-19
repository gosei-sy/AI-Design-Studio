# M01 Wave 3 Report — M01-03 Organization-Scoped Authorization

**Milestone:** M01 — Authentication, core CRUD, live generation
**Authorized:** 2026-11-03, Product Owner · **Scope:** **M01-03 only**
**Report date:** 2026-11-03 · **Author:** Lead Developer

**M01-03 is submitted for independent review, not marked complete.** **M01-04
has not started**, and no project CRUD endpoint exists.

> ### Update 2026-11-03 — the Wave 3 review returned CHANGES REQUIRED
>
> [The independent review](reviews/M01_WAVE3_CODEX_REVIEW.md) raised **2 P1 and
> 2 P2** findings, all correct. All four are corrected; see
> [M01_WAVE3_RESPONSE.md](reviews/M01_WAVE3_RESPONSE.md). **M01-03 awaits
> independent re-review and is still not accepted.**
>
> | Finding    | Summary                                                                                                                                                                                                                                                                                                                             |
> | :--------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
> | **W3-001** | The tenant filter covered only the three roots carrying `OrganizationId` directly. Contracts, design systems, screens and inventories are tenant-owned through `ProjectId`, were unfiltered, and **the guard could not discover them**. All seven roots now carry the key and are filtered; the guard partitions the whole EF model |
> | **W3-002** | A role change kept the original operator's attribution, so a later grant of Approver was misattributed. There is now an **append-only audit chain** plus last-change columns                                                                                                                                                        |
> | **W3-003** | Plan and status carried contradictory Wave 2 and Wave 3 states. Reconciled by claim                                                                                                                                                                                                                                                 |
> | **W3-004** | The provisioning example piped a literal password, exposing it through shell history. Replaced with a hidden interactive read                                                                                                                                                                                                       |
>
> **The sections below are the 2026-11-03 submission as reviewed.** Where a claim
> was found overstated it is corrected in place and marked, so this report cannot
> be read as evidence for something the tests do not prove.

> ### Update 2026-11-05 — the re-review returned CHANGES REQUIRED
>
> [The independent re-review](reviews/M01_WAVE3_RE_REVIEW.md) confirmed W3-003
> and W3-004 closed and both P1 areas materially improved, then found the
> corrections had been verified **only against fresh, internally consistent
> data**. Two findings, both correct, both corrected; see
> [M01_WAVE3_RE_REVIEW_RESPONSE.md](reviews/M01_WAVE3_RE_REVIEW_RESPONSE.md).
>
> | Finding       | Summary                                                                                                                                                                                                                                                                  |
> | :------------ | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
> | **W3-RR-001** | Denormalizing the tenant key created a **second ownership assertion** and nothing made the two agree: PostgreSQL accepted one organization's key on another's project. "Cannot drift" was true of mutation, false of creation. Five composite foreign keys now refuse it |
> | **W3-RR-002** | Memberships predating the event table got no audit entry. A migration-only `MigrationBaseline` now records what the upgrade **observed** — not a reconstructed provisioning event                                                                                        |
>
> **M01-03 awaits independent re-acceptance and is still not accepted.**

---

## 1. M01-02 — independent acceptance record

**Decision supplied by the independent reviewer on 2026-11-03**, quoted in full:

> M01-02 — Authentication is APPROVED WITH NON-BLOCKING FINDINGS.
>
> All implementation findings are closed. ADR-0008 A2 is accepted, so D-8 is a
> fail-closed pre-production gate and no longer blocks M01-02 acceptance.
>
> D-8 remains unresolved: password-reset delivery is unavailable and no
> Production deployment may proceed until an authorized transport exists.
>
> MFA recovery codes remain an explicitly recorded gap. This approval does not
> resolve D-9, authorize Production deployment, or mark M01 complete.

This is an **external independent-review decision supplied by the reviewer**. It
is not self-acceptance. M01-02 went through six rounds: five returning CHANGES
REQUIRED, with the last developer-side finding (R5-001) closed on 2026-11-02,
then this verdict.

**The two non-blocking findings stay open.** An approval that carried findings
did not close them:

| Finding                  | Status                                                                                                                  |
| :----------------------- | :---------------------------------------------------------------------------------------------------------------------- |
| **D-8** — reset delivery | Open. Fail-closed pre-production gate; **no Production deployment may proceed**. Tracked in IMPLEMENTATION_STATUS.md §7 |
| **MFA recovery codes**   | Open. Recorded as risk **R-5**. Not in M01-03 scope and not attempted                                                   |

Recorded in: `IMPLEMENTATION_PLAN.md` (wave table, M01-03 task header, D-9 row),
`docs/05-delivery/IMPLEMENTATION_STATUS.md` (header, milestone table, new M01
review-history section quoting the decision, §7 D-9 subsection, §8 risks R-5/R-6,
§9 change log), and `README.md` (status paragraph).

No previous measurement or historical review outcome was rewritten. M00's
closure snapshot (§2, §4 of IMPLEMENTATION_STATUS.md) is untouched, and the five
CHANGES REQUIRED rounds remain on the record.

---

## 2. D-9 resolved — ADR-0009

**[ADR-0009: Account proofing is trusted operator provisioning](../02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md)**
— Accepted 2026-11-03, Product Owner + Solution Architect.

A **new ADR**, not an amendment. ADR-0008 is referenced and **not modified**: it
decided authentication and was explicit that it decided nothing about
authorization, so there was nothing in it to supersede. Its amendment A1 is what
raised D-9 in the first place.

The five rules, as approved:

1. Public self-registration **never** grants an organization membership or any
   role.
2. Outside Development, public registration is **disabled entirely**.
3. Only an operator-provisioned account may receive a membership or
   Member/Approver. Proofing is the operator's **recorded assertion** that the
   person was verified out of band.
4. **Exactly one active membership per user** for the MVP, with membership
   modelled **explicitly** so multi-organization support does not later require
   moving organization or role data out of the authorization boundary.
5. **No** client-supplied organization header, organization claim, email address,
   role or request-body field establishes membership.

The ADR also records what this costs — manual onboarding, proofing only as good
as the operator, no self-service anything, one organization per user — and why
the four rejected alternatives were rejected.

---

## 3. The authorization model

### Where each question is answered

| Question                           | Answered by                                                                                                                                                                                        | Not by                            |
| :--------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :-------------------------------- |
| Who is calling?                    | Session cookie → `ICurrentUser.UserId`                                                                                                                                                             | —                                 |
| What may they do?                  | `auth.organization_memberships` → `IOrganizationContext`                                                                                                                                           | any claim, header or body field   |
| Which rows exist for this request? | A global EF query filter on **all seven** tenant-owned roots, defaulting to **none** (W3-001), **plus a composite foreign key so a row's tenant key cannot disagree with its project** (W3-RR-001) | a `Where` clause per query        |
| May they approve?                  | `organization.approver` policy / `OrganizationAccess.RequireApprover()`                                                                                                                            | `RequireRole("Approver")`         |
| Who can grant access?              | `aids-provision`, a console tool                                                                                                                                                                   | no HTTP route, in any environment |

### Roles are organization-scoped, and are not Identity roles

`OrganizationRoles` is a flags enum — `Member = 1`, `Approver = 2` — stored on a
membership row. A user may hold both; at least one is required, in the aggregate
and in the database.

ASP.NET Core Identity's own role store is still registered (it is part of the EF
model from M01-02) and is deliberately **not** how these roles are represented.
Identity roles are global: a row in `auth.user_roles` named `Approver` would make
someone an approver of every tenant. Nothing in the authorization path reads a
role claim, and the test that holds it there assigns a **real** global `Approver`
role to a Member and asserts the Approver-only endpoint still returns `403`.

### The data scope fails closed

```csharp
e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);
```

| Scope            | Rows visible             | Who gets it                                                      |
| :--------------- | :----------------------- | :--------------------------------------------------------------- |
| **None**         | none                     | the default — any context built without a scope                  |
| **Organization** | that organization's only | an HTTP request after a membership resolves                      |
| **System**       | all                      | the worker and `aids-provision`, via `AddInfrastructure(System)` |

Two properties worth stating:

- **A forgotten authorization check produces an empty result set, not a
  cross-tenant read.** An endpoint that never calls `IOrganizationContext` sees
  nothing at all.
- **There is no code path from an HTTP request to the system scope.** The API
  host passes `OrganizationScoping.PerRequest`, and `OrganizationScope.IsSystem`
  is hard-coded `false`.

`AddInfrastructure` now **requires** the scoping choice as a parameter. That was
not the original design; see §8, where forgetting it cost two timed-out tests.

> **Corrected after W3-001.** As submitted, only `DesignProject`,
> `AssetReference` and `JobRecord` had filters — the three roots that already
> carried `OrganizationId`. `DesignContract`, `DesignSystem`, `Screen` and
> `FeatureInventory` are tenant-owned through `ProjectId` and had none, so they
> were readable by primary key across tenants. All seven now carry the key
> directly and are filtered (ADR-0009 A1).
>
> **Corrected again after W3-RR-001.** Carrying the key directly created a second
> ownership assertion, and a filter cannot keep two assertions honest — the
> database accepted them disagreeing. All five project-owned roots now reference
> `projects (organization_id, id)` through a composite foreign key, and the
> aggregates take a single `ProjectOwnership` value instead of two loose ids
> (ADR-0009 A3.1).

### Membership is the one unfiltered organization-bearing table

Filtering memberships by the scope would be circular — the resolver could never
read the row that tells it which organization to filter by. So that one table
carries its own predicate in each query, the exemption is listed by name in
`OrganizationScopeGuardTests`, and the only query against it has its own test.

---

## 4. Operator provisioning — threat model

`aids-provision` (`backend/src/AIDesignStudio.Provisioning`) is the only path
that grants access.

### What it is defending against, and how

| Threat                                                                        | Control                                                                                                                  | Verified by                                                                                                                                      |
| :---------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------- |
| Anyone who reaches the network grants themselves access                       | Not an HTTP endpoint. Requires shell access to a host holding the connection string                                      | `No_http_endpoint_can_grant_a_membership` enumerates the real route table                                                                        |
| A bootstrap route left reachable "until setup is done"                        | No bootstrap route exists at all                                                                                         | same test                                                                                                                                        |
| Self-registration escalating to access                                        | Registration creates a user and nothing else; unmapped outside Development                                               | `Development_self_registration_creates_no_membership_and_no_roles`, `Public_registration_is_absent_outside_development`                          |
| **A password read from `/proc/<pid>/cmdline`** by any local user              | A password is **never** an argument. `--password` is refused with an explanation; the value is read from stdin, unechoed | `A_password_passed_as_an_argument_is_refused`, and the live run in §7                                                                            |
| A password or reset token in logs, stdout, or a pasted ticket                 | Nothing printed or logged contains either. Logs go to **stderr**, so stdout is only the provisioning record              | live run in §7 — `grep -c 'Correct-Horse'` over both streams returned `0`                                                                        |
| An unattributable grant of access                                             | `--actor` is required; `provisioned_by` is `NOT NULL`; revocation records `revoked_by` and `revoked_at`                  | `A_membership_nobody_asserted_is_refused`, `Provisioning_refuses_an_unattributed_grant`, `The_database_refuses_a_revocation_with_no_audit_trail` |
| A grant to a user id that does not exist                                      | Foreign key to `auth.users`                                                                                              | `The_database_refuses_a_membership_for_a_user_that_does_not_exist`                                                                               |
| An account silently moved between tenants by a command that reads as additive | Provisioning into a second organization is refused; the operator must revoke first                                       | `Provisioning_an_account_into_a_second_organization_is_refused`                                                                                  |
| A role change resetting someone's credential as a side effect                 | An existing account's password is never touched                                                                          | `Re_provisioning_an_account_adjusts_its_roles_without_duplicating_the_membership`                                                                |
| Provisioning into a deployment the API would refuse to serve                  | The tool starts the same host, so the same fail-closed startup gates apply                                               | observed — see the Production note below                                                                                                         |

### What it does not defend against, stated plainly

- **A malicious or careless operator.** The system records who asserted that a
  person was verified; it cannot check that they did. ADR-0009 accepts this: the
  trust is moved and made auditable, not established.
- **Database credentials in the wrong hands.** Anyone who can write to
  `auth.organization_memberships` can grant access. The check constraints stop
  malformed rows, not authorized-looking ones.
- **No rate limit, no second approver, no notification.** A single operator can
  grant access silently. For an MVP with a handful of users this is accepted; it
  is the first thing to revisit if the team grows.

### Production, today

`aids-provision` **cannot run in Production**, because it starts the same host as
the API and that host refuses to start without a password-reset transport
(**D-8**, ADR-0008 A2). That is the approved fail-closed gate working as
designed, not a defect — but it means the first Production provisioning run is
blocked behind D-8, which is worth knowing before a deployment is planned.

---

## 5. Database changes

One migration: **`20261103040427_AddOrganizationsAndMemberships`**. Two new
tables in the `auth` schema, no change to any existing table.

### `auth.organizations`

`id` · `name varchar(200)` **unique** · `created_at` · `updated_at`

### `auth.organization_memberships`

`id` · `organization_id` · `user_id` · `roles integer` · `status varchar(32)` ·
`provisioned_by varchar(256) NOT NULL` · `revoked_at` · `revoked_by` ·
`created_at` · `updated_at`

### Seven constraints, each stopping a specific thing

Read back from the fresh database in §7:

| Constraint                                  | Definition                                                                   |
| :------------------------------------------ | :--------------------------------------------------------------------------- |
| `ux_organization_memberships_active_user`   | `UNIQUE (user_id) WHERE status = 'Active'` — one active membership per user  |
| `ck_organization_memberships_roles`         | `roles > 0 AND roles <= 3` — at least one role, no undefined role            |
| `ck_organization_memberships_status`        | `status IN ('Active','Revoked')`                                             |
| `ck_organization_memberships_revoked_audit` | `status <> 'Revoked' OR (revoked_at IS NOT NULL AND revoked_by IS NOT NULL)` |
| `ck_organization_memberships_active_audit`  | `status <> 'Active' OR (revoked_at IS NULL AND revoked_by IS NULL)`          |
| `fk_organization_memberships_user`          | `→ auth.users(id) ON DELETE CASCADE`                                         |
| `fk_organization_memberships_organization`  | `→ auth.organizations(id) ON DELETE RESTRICT`                                |

Plus `ux_organizations_name` and `ix_organization_memberships_organization_id_status`.

**Two deliberate deviations from this schema's conventions**, both documented in
DATA_MODEL.md:

1. **`roles` is an integer, not a `varchar` enum.** A flags value has no single
   valid string form, and the integer is what lets "at least one role" and "no
   unknown roles" be check constraints instead of prose.
2. **Two foreign keys exist**, against the no-FK-across-modules rule. Both are
   inside `auth` and neither crosses a module boundary; a membership row is a
   grant of access, and one pointing at a user id that was never issued would
   hand that access to whoever received the id later.

The audit constraints are **split in two** rather than written as one
disjunction. A single `(Revoked AND …) OR (Active AND …)` rule is also violated
by an unknown status, which would make a status error and an audit error
indistinguishable — and would have made two of the tests below unable to say
which constraint they were exercising.

---

## 6. Endpoint behaviour

Three endpoints, all under `/api/v1/organizations`, all answering from
server-side membership state. Full contract in
[API_CONTRACTS.md §3a](../02-architecture/API_CONTRACTS.md).

| Endpoint                              | 200               | 401        | 403                                | 404               |
| :------------------------------------ | :---------------- | :--------- | :--------------------------------- | :---------------- |
| `GET /organizations/current`          | own org + roles   | no session | no membership                      | —                 |
| `GET /organizations/{organizationId}` | own org only      | no session | no membership                      | **any other org** |
| `GET /organizations/current/members`  | **Approver only** | no session | no membership, or no Approver role | —                 |

**404, not 403, for another tenant.** A 403 would confirm the organization
exists and that the caller merely is not in it — a tenant-existence oracle, the
same class of leak as the account-enumeration oracles corrected in W2-002. The
refusal happens _before_ any query runs, and the response is byte-identical for
an organization that exists and one that does not.

**Registration changed.** `POST /api/v1/auth/register` is mapped in
**Development only**. Outside Development the route does not exist, which
produces two different answers worth distinguishing:

- **anonymous → `401`**, because the authorization middleware applies ADR-0008's
  fallback policy to requests matching no endpoint at all;
- **authenticated → `404`**, the router's own answer.

Both are asserted. The second is the one that proves absence — a 404 assertion on
an anonymous call would have passed just as happily against a live-but-protected
handler.

**No approve/reject/waive endpoint exists**, so the Approver policy is applied to
one real shipping endpoint (the member roster) and nothing else. See §9.

---

## 7. Tests and execution results

### Commands, with actual output

All run on 2026-11-03 against PostgreSQL 15.19 on this machine.

```
$ dotnet build --configuration Release
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet test --configuration Release
  total: 444
  failed: 0
  succeeded: 444
  skipped: 0
  duration: 1m 21s

$ dotnet format --verify-no-changes
(exit 0, no output)

$ npm test
  frontend            Test Files 2 passed (2)   Tests 14 passed (14)
  packages/contracts  Test Files 3 passed (3)   Tests 164 passed (164)

$ npm run typecheck            → exit 0
$ npm run lint                 → exit 0
$ npm run format:check         → All matched files use Prettier code style!
$ npm run build --workspace frontend
  Route (app)  ┌ ◐ /   └ ○ /_not-found   ƒ Proxy (Middleware)

$ npm run test:e2e --workspace frontend
  Running 7 tests using 7 workers
  7 passed (1.2m)

$ dotnet-ef migrations has-pending-model-changes \
    --project backend/src/AIDesignStudio.Infrastructure \
    --startup-project backend/src/AIDesignStudio.Api
No changes have been made to the model since the last migration.

$ npm run audit:prod
found 0 vulnerabilities

$ dotnet list package --vulnerable --include-transitive
The given project `…` has no vulnerable packages given the current sources.   (× 10 projects)

$ git diff --check
(exit 0, no output)
```

**Per-suite counts, discovered from the runners** — these are the figures **as
submitted**; the post-correction totals are in
[M01_WAVE3_RESPONSE.md](reviews/M01_WAVE3_RESPONSE.md) (455 backend, 633
overall):

| Suite                              |   Tests | Change    |
| :--------------------------------- | ------: | :-------- |
| `AIDesignStudio.UnitTests`         |     257 | +56       |
| `AIDesignStudio.ArchitectureTests` |      19 | +3        |
| `AIDesignStudio.IntegrationTests`  |     168 | +46       |
| `frontend` (Vitest)                |      14 | unchanged |
| `packages/contracts` (Vitest)      |     164 | unchanged |
| **Total**                          | **622** | **+105**  |
| E2E (counted separately)           |   7 / 7 | unchanged |

**No skips.** PostgreSQL stopped three times during this session and each time
the integration suite reported skips; each run was repeated after restarting it,
and the figures above are from runs with **0 skipped**. A skip is not a pass.

### Fresh database — all migrations from empty

> **As submitted: four migrations.** A fifth,
> `20261103061233_ScopeTenantRootsAndAuditRoleChanges`, was added by the W3-001
> and W3-002 corrections and re-verified against a fresh database.

```
$ ConnectionStrings__Default="…Database=aids_fresh_…" dotnet-ef database update
Applying migration '20261020053356_InitialSchema'.
Applying migration '20261020094048_ReviewCorrections'.
Applying migration '20261026041140_AddIdentityAndDataProtection'.
Applying migration '20261103040427_AddOrganizationsAndMemberships'.
Done.

$ psql -c "\dt auth.*"
 auth | data_protection_keys     | auth | organization_memberships | auth | organizations
 auth | role_claims | roles | user_claims | user_logins | user_roles | user_tokens | users
(10 rows)
```

The seven constraints and five indexes in §5 were read back from this database
with `pg_get_constraintdef` and `pg_indexes`; the definitions quoted there are
that output. The database was dropped afterwards.

### The required test list, mapped to tests

| Required proof                                                           | Tests                                                                                                                                                                                                                                                                                                                                                                                                                        |
| :----------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Anonymous requests are rejected                                          | `An_anonymous_request_is_refused` (×2), `An_anonymous_request_for_a_specific_organization_is_refused`                                                                                                                                                                                                                                                                                                                        |
| Authenticated but unprovisioned has no organization access               | `An_authenticated_but_unprovisioned_account_reaches_no_organization`, `An_unprovisioned_session_has_a_closed_data_scope`                                                                                                                                                                                                                                                                                                     |
| Development self-registration creates no membership or roles             | `Development_self_registration_creates_no_membership_and_no_roles`                                                                                                                                                                                                                                                                                                                                                           |
| Non-Development public registration is disabled                          | `Public_registration_is_absent_outside_development`, `Registration_is_mapped_in_development_for_the_same_caller`                                                                                                                                                                                                                                                                                                             |
| Provisioning creates exactly the approved organization and roles         | `Operator_provisioning_creates_exactly_one_organization_and_one_membership`, `Re_provisioning_…`, `A_provisioned_account_can_sign_in_and_see_its_organization`, + 3 refusal cases                                                                                                                                                                                                                                            |
| A Member cannot perform Approver-only actions                            | `A_member_cannot_perform_an_approver_only_action`, `A_member_is_refused_approval_authority` (unit)                                                                                                                                                                                                                                                                                                                           |
| An Approver can                                                          | `An_approver_can_perform_an_approver_only_action`, `An_approver_passes_the_approval_guard` (unit, ×2)                                                                                                                                                                                                                                                                                                                        |
| A user holding both roles works                                          | `An_account_holding_both_roles_can_do_both`, `Both_roles_together_are_valid` (unit)                                                                                                                                                                                                                                                                                                                                          |
| Organization A cannot read organization B's resources                    | `No_tenant_owned_aggregate_leaks_across_organizations` — all **seven** roots, list and by-id; `An_approver_cannot_list_another_organizations_members`. **(W3-001: the version submitted for review covered projects only, and this row overstated it.)**                                                                                                                                                                     |
| Cross-organization access returns 404                                    | `A_cross_organization_read_is_not_found_rather_than_forbidden`, `An_existing_other_organization_and_a_nonexistent_one_look_the_same`                                                                                                                                                                                                                                                                                         |
| Client-supplied organization and role values cannot change authorization | `A_client_supplied_organization_is_ignored`, `A_client_supplied_role_cannot_reach_an_approver_only_action`, **`A_global_identity_role_named_Approver_grants_nothing`**                                                                                                                                                                                                                                                       |
| Role and membership changes immediately affect authorization             | `Granting_the_approver_role_takes_effect_on_the_next_request`, `Revoking_a_membership_removes_access_on_the_next_request`, `Revoking_a_membership_records_the_revocation`, `A_revoked_account_can_be_provisioned_again`                                                                                                                                                                                                      |
| The database rejects duplicate/invalid memberships                       | `The_database_refuses_a_second_active_membership_for_one_user`, `…_an_invalid_role_set` (×3), `…_an_unknown_membership_status`, `…_a_revocation_with_no_audit_trail`, `…_an_active_membership_that_claims_to_be_revoked`, `…_a_membership_for_a_user_that_does_not_exist`, `…_of_an_organization_that_does_not_exist`, `…_two_organizations_with_the_same_name`, `The_database_allows_many_revoked_memberships_for_one_user` |
| Every organization-scoped query path is guarded                          | `OrganizationScopeGuardTests` — **6** tests that partition the whole EF model (was 3 tests over a self-selected subset; W3-001)                                                                                                                                                                                                                                                                                              |

Plus, not required but load-bearing: `The_api_host_is_never_system_scoped`,
`No_http_endpoint_can_grant_a_membership`,
`Organization_scoping_did_not_open_an_unauthenticated_hole`,
`Antiforgery_still_refuses_a_state_change_without_a_token`, and
`Any_approval_endpoint_must_carry_the_approver_policy` — which is **vacuously
true today** and is labelled as such in its own doc comment, because no
approve/reject/waive endpoint exists yet. It exists to fail when M01-05 adds the
first one without the policy.

### The guard test was verified by breaking it

Per rule 4, the guard was checked by reintroducing the defect it exists to catch.

**As submitted**, `DesignProject`'s filter was removed and the guard failed with
`["DesignProject"]`. The review correctly pointed out that this proved only that
the guard could detect a missing filter **within the set it had already
discovered** — which was the whole problem.

**After the W3-001 correction**, the regression was repeated on
`DesignContract`, a root that is tenant-owned _indirectly_ — the case the
original guard was blind to:

```
failed OrganizationScopeGuardTests.Every_entity_in_the_model_is_filtered_owned_or_explicitly_exempt
  unclassified should be empty but had 2 items and was
  ["DesignContract", "DesignContractVersion (owned by DesignContract)"]
failed OrganizationScopeGuardTests.Every_aggregate_root_carrying_the_tenant_key_is_filtered
failed OrganizationScopeGuardTests.All_seven_known_tenant_tables_are_filtered
failed AuthorizationTests.No_tenant_owned_aggregate_leaks_across_organizations
failed AuthorizationTests.A_closed_scope_sees_no_tenant_row_of_any_kind
```

Three guard tests and both two-tenant integration proofs fail; all pass again on
restore, and all 22 architecture tests pass.

### The provisioning CLI was run, not just compiled

Against a throwaway migrated database, which was dropped afterwards:

```
$ aids-provision provision --email a@b.test … --password hunter2
aids-provision: --password is not supported and never will be. A password passed as a
command-line argument is readable by every local user through /proc/<pid>/cmdline and is
recorded in your shell history. Treat that password as compromised and choose another.
The tool reads it from standard input.

# W3-004: this example originally read `echo 'literal' | aids-provision`, which
# puts the password in shell history and in the producer's process arguments —
# the same two places the --password refusal above exists to protect. Corrected,
# and re-run:
$ read -rs INITIAL_PASSWORD
$ printf '%s' "$INITIAL_PASSWORD" | aids-provision provision \
    --email alice@example.test --organization "Acme Design" \
    --role Member --actor "alice-granter@ops.test"
$ unset INITIAL_PASSWORD
Provisioned.
  user          01a1a038-6798-7be1-9c3c-badfad9502d6 (alice@example.test) [created]
  organization  01a1a038-69e8-73e9-911a-27eeffe7689e (Acme Design) [created]
  roles         Member
  asserted by   alice-granter@ops.test via aids-provision@holyclaude

$ echo 'x' | aids-provision provision --email bob@example.test --organization "Other Co" …
(exit 1 — provisioning.user_already_provisioned)

$ aids-provision revoke --email bob@example.test --actor "security@example.test"
Revoked membership 01a19fa3-580f-7cd1-9ba5-334e49d907ec for bob@example.test.

$ psql -c "SELECT u.email, m.roles, m.status, m.provisioned_by, m.revoked_by …"
 alice@example.test |  3 | Active  | sy.vo@…  via aids-provision@holyclaude |
 bob@example.test   |  1 | Revoked | sy.vo@…  via aids-provision@holyclaude | security@… via aids-provision@holyclaude

$ grep -c 'Correct-Horse' stdout stderr
stdout:0
stderr:0
```

---

## 8. Two defects this work introduced, and how they surfaced

Both are fixed. Recording them because each says something about the design.

**1. The worker and the CLI could not start.** `IOrganizationContext` was
registered in `AddInfrastructure`, and it depends on `ICurrentUser` — which only
the API host has. Every non-API host failed at service validation. The fix moved
the registration to the API host, where "the current caller" is a question that
has an answer. Found by **running the CLI**; no test would have caught it,
because no test boots the worker or the tool's composition root.

**2. Two job dispatcher tests timed out.** The new query filter on `JobRecord` is
correct, and the dispatcher's test host had no scope — so the filter matched
nothing, the dispatcher saw an empty queue, and it waited for work it could not
see. The test now registers the system scope exactly as the real worker does.

The second one is the more interesting failure, because of its shape: **a
forgotten scope makes a worker silently idle, not loud.** That is a bad failure
mode to leave reachable, so `AddInfrastructure` now takes
`OrganizationScoping` as a **required parameter**. Forgetting it is a compile
error; there is no third outcome where a host guesses.

Three M01-02 tests also had to change, and the change is worth naming so nobody
reads it as weakening them: they created their fixture account by calling
`POST /auth/register` against a **Staging** host, which M01-03 unmapped. Two now
create the account through the user store directly and still assert exactly what
they asserted before (recovery-response uniformity, lockout invisibility). The
third tests the registration endpoint itself and now runs against Development,
which is the only place that endpoint exists. **No assertion was removed or
relaxed.**

---

## 9. Remaining gaps and blockers

### In M01-03's own scope

| Gap                                                              | Why                                                                                                                                                                                                                                                                                        |
| :--------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **No approve/reject/waive endpoint carries the Approver policy** | None exists — they are M01-05. The policy and `RequireApprover()` are implemented, registered and tested, and `Any_approval_endpoint_must_carry_the_approver_policy` fails if M01-05 adds one without it. **The mechanism is verified; the operation it will protect does not exist yet.** |
| No E2E spec for the organization flow                            | Every guarantee here is a status code, a visible row or a refused write, all asserted against the real host. A browser adds nothing; the one browser-only property (the cookie jar) was covered in M01-02                                                                                  |
| No endpoint lists a user's organizations                         | One active membership per user makes it a single value, returned by `/current`                                                                                                                                                                                                             |
| The member roster returns ids, not names or emails               | An address is self-asserted (W2-005) and names are not modelled. Deliberate under-disclosure                                                                                                                                                                                               |

### Blockers and open decisions, unchanged by this work

| Item                                         | Status                                                                                                                             |
| :------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------- |
| **D-8** — reset delivery transport           | **Open.** Fail-closed pre-production gate; no Production deployment may proceed. Also blocks the first Production provisioning run |
| **D-5** — deployment target, secret store    | **Open.** Production also refuses to start without a Data Protection key protector                                                 |
| **D-1 / D-2** — Stitch account, capped spend | **Open.** M01-01 live execution stays blocked; `BillableOperationsEnabled` is `false`                                              |
| **MFA recovery codes**                       | **Open** — risk R-5, a non-blocking finding of M01-02's acceptance. Not attempted here                                             |
| **M01-11** — E2E in CI                       | 7/7 locally; the CI run needs a push, which is not authorized                                                                      |
| **M01-12** — Docker Compose                  | **BLOCKED.** Docker is not installed in this environment                                                                           |

None of these was resolved by assumption, and no provider call was made.

### Accepted costs now on the record

- **Onboarding is manual** (risk R-6). Every account and role change needs an
  operator with shell access.
- **One organization per user.** The table shape allows more; the index does not.
- **No self-service recovery outside Development**, which is D-8 plus D-9
  together.

---

## 10. Confirmation of the constraints

| Constraint                                                  | Status                                                                                                                                                                                                                                                                        |
| :---------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| No Stitch or provider call                                  | ✅ None made. The test host points at a nonexistent executable                                                                                                                                                                                                                |
| No spend                                                    | ✅ None                                                                                                                                                                                                                                                                       |
| `BillableOperationsEnabled=false`                           | ✅ Unchanged, and asserted by `The_billable_guard_is_disabled_in_the_test_host`                                                                                                                                                                                               |
| No external service or infrastructure                       | ✅ Two tables and one console project. No IdP, mail service, Redis, broker or second database                                                                                                                                                                                 |
| No commit, push, PR, deployment or Production configuration | ✅ None. `git status` shows a working tree only                                                                                                                                                                                                                               |
| D-8, D-5, M01-11 CI, M01-12 not resolved by assumption      | ✅ All four reported open above                                                                                                                                                                                                                                               |
| **M01-04 not begun**                                        | ✅ **No project CRUD endpoint exists.** The only new routes are the three in §6                                                                                                                                                                                               |
| No Admin role                                               | ✅ Two roles only                                                                                                                                                                                                                                                             |
| Existing guards not weakened                                | ✅ 444 backend tests pass with 0 skips, including all 16 original architecture tests and every M01-02 authentication, antiforgery, enumeration, billable, domain, storage and job-lease test. `Organization_scoping_did_not_open_an_unauthenticated_hole` asserts it directly |
| Seven accepted ADRs and their amendments preserved          | ✅ ADR-0009 is new and supersedes nothing. ADR-0008 unmodified                                                                                                                                                                                                                |

---

## 11. Files changed

**New — backend**

```
backend/src/AIDesignStudio.Domain/Organizations/Organization.cs
backend/src/AIDesignStudio.Domain/Organizations/OrganizationMembership.cs
backend/src/AIDesignStudio.Application/Organizations/OrganizationAccess.cs
backend/src/AIDesignStudio.Application/Organizations/IOrganizationContext.cs
backend/src/AIDesignStudio.Infrastructure/Organizations/OrganizationScopes.cs
backend/src/AIDesignStudio.Infrastructure/Organizations/OrganizationContext.cs
backend/src/AIDesignStudio.Infrastructure/Organizations/OperatorProvisioningService.cs
backend/src/AIDesignStudio.Infrastructure/Persistence/Migrations/20261103040427_AddOrganizationsAndMemberships*.cs
backend/src/AIDesignStudio.Api/Identity/OrganizationAuthorization.cs
backend/src/AIDesignStudio.Api/Endpoints/OrganizationEndpoints.cs
backend/src/AIDesignStudio.Provisioning/{AIDesignStudio.Provisioning.csproj,Program.cs,CommandLine.cs}
```

**New — tests**

```
backend/tests/AIDesignStudio.UnitTests/OrganizationMembershipTests.cs
backend/tests/AIDesignStudio.UnitTests/ProvisioningCommandLineTests.cs
backend/tests/AIDesignStudio.IntegrationTests/AuthorizationTests.cs
backend/tests/AIDesignStudio.ArchitectureTests/OrganizationScopeGuardTests.cs
```

**Modified — backend:** `AppDbContext` (DbSets, mappings, constraints, three
query filters, scope-aware constructor), `DependencyInjection` (required
`OrganizationScoping`), `SessionAuthentication` (policies, handler,
`IOrganizationContext`), `AuthEndpoints` (Development-only registration),
`Api/Program.cs`, `Worker/Program.cs`, `ICurrentUser`, `ApplicationUser`,
`IdentityServiceCollectionExtensions`, `ModuleBoundaryTests`, `DatabaseFixture`,
`JobDispatcherTests`, `AuthenticationTests`, `AIDesignStudio.slnx`.

**New — docs:** `ADR-0009`, this report.

**Modified — docs:** `README.md`, `CLAUDE.md`, `AGENTS.md`,
`IMPLEMENTATION_PLAN.md`, `decisions/README.md`, `DATA_MODEL.md` (§9a, §10a),
`API_CONTRACTS.md` (§3a, error codes), `SECURITY_REQUIREMENTS.md` (S-20, S-21),
`TEST_STRATEGY.md` (counts), `OPEN_ASSUMPTIONS.md` (A-01),
`IMPLEMENTATION_STATUS.md`.

---

**M01-03 was submitted for independent review on 2026-11-03, the review returned
CHANGES REQUIRED, and all four findings are corrected
([response](reviews/M01_WAVE3_RESPONSE.md)). It is not complete and not accepted.
M01-04 has not started.**
