# ADR-0009: Account proofing is trusted operator provisioning

- **Status:** Accepted (M01) — **as amended by A1, A2, A3 and A4**
- **Date:** 2026-11-03
- **Deciders:** Product Owner, Solution Architect
- **Resolves:** D-9
- **Supersedes:** nothing
- **Relates to:** [ADR-0008](ADR-0008-authentication-is-local-identity-with-cookie-sessions.md) (authentication), which this does not amend

## Context

ADR-0008 settled **authentication** and was explicit that it settled nothing
about **authorization**. Its amendment A1 raised **D-9** and named the gap
precisely:

> D-9 exists because registration is public and unconfirmed, so an email address
> is self-asserted. The audit subject is now the **immutable user id** rather
> than the address (W2-005), which makes attribution stable — but not _proved_.

The measured situation at the start of M01-03:

| Fact                                                                          | Where it came from                                 |
| :---------------------------------------------------------------------------- | :------------------------------------------------- |
| `POST /api/v1/auth/register` was public and anonymous in every environment    | M01-02 as shipped                                  |
| Email confirmation was **off**, because no mail transport exists (D-8)        | `IdentityServiceCollectionExtensions`, ADR-0008 A1 |
| `ApplicationUser` carried no organization and no role                         | M01-02, deliberately deferred to M01-03            |
| `OrganizationId` already existed and was indexed on projects, assets and jobs | M00 data model                                     |
| D-3 had been resolved to **two roles**, Member and Approver                   | Product Owner, 2026-10-21                          |

So anyone who could reach the API could create an account, and the only thing
stopping that account from reaching data was that no organization-scoped
endpoint existed yet. M01-03 adds them. Something had to decide which accounts
get access, and on what evidence.

The honest options were: confirm email addresses (needs D-8, which is
unresolved), integrate an identity provider that does its own proofing (needs
D-5 and an external dependency the milestone is not authorized to add), or take
proofing out of the software entirely.

## Decision

**Account proofing for the MVP is trusted operator provisioning.**

1. **Public self-registration never grants an organization membership or any
   role.** A registered account is authenticated and authorized for nothing.
2. **Outside Development, public registration is disabled entirely.** The route
   is not mapped, so there is no handler to reach.
3. **Only an account explicitly provisioned by a trusted operator may receive an
   organization membership or the Member/Approver roles.** Proofing is therefore
   the operator's recorded assertion that the person was verified out of band,
   and that assertion is stored on the membership.
4. **Exactly one active organization membership per user**, for the MVP.
   Membership is modelled **explicitly**, as its own aggregate and table, so
   supporting several later is a change to an index rather than a migration of
   organization and role data out of the authorization boundary.
5. **No client-supplied organization header, organization claim, email address,
   role, or request-body field is trusted to establish membership.** The current
   organization is resolved from server-side membership state only.

### Where the boundary sits

| Concern                           | Mechanism                                                                      |
| :-------------------------------- | :----------------------------------------------------------------------------- |
| Who the caller is                 | Session cookie → `ICurrentUser.UserId` (ADR-0008)                              |
| What they may do                  | `auth.organization_memberships` → `IOrganizationContext`                       |
| Which rows exist for this request | A global EF query filter on **every tenant-owned aggregate root** (see A1)     |
| Approve, reject, waive            | The `organization.approver` policy, and `OrganizationAccess.RequireApprover()` |
| Granting access                   | `aids-provision`, a console tool. No HTTP route, in any environment            |

**Roles are organization-scoped, and they are not ASP.NET Core Identity roles.**
Identity roles live in `auth.user_roles` and are global: a row there named
`Approver` would make someone an approver of every tenant. Nothing in the
authorization path reads a role claim.

**The data scope fails closed.** A request that has not resolved a membership —
including an endpoint whose author forgot to — is scoped to _no_ organization,
and every organization-scoped query returns nothing. The elevated "system" scope
is reachable only by a process that asks for it in its own composition root: the
job worker, which dispatches work for all tenants, and the provisioning tool.
There is no code path from an HTTP request to that elevation.

### Operational consequence, stated rather than discovered

With registration disabled outside Development and **no password-reset delivery
until D-8 is resolved** (ADR-0008 A2), a provisioned user's first password is set
by the operator and communicated out of band. There is no self-service recovery
in a deployed environment. This is a real cost of taking Option A in ADR-0008
and of resolving D-9 this way, and it is the strongest argument for resolving
D-8 early.

A second consequence follows from A2's fail-closed gate: because the host refuses
to start in Production without a reset transport, **`aids-provision` cannot run
in Production either** — it starts the same host so that it cannot provision into
a deployment the API would refuse to serve.

## Consequences

**Accepted:**

- **Onboarding is manual.** Every account and every role change needs an
  operator with shell access and a database connection string. That does not
  scale, and it is not meant to: the MVP has a handful of users.
- **Proofing is only as good as the operator.** The system records _who_
  asserted that a person was verified; it cannot check that they did. This moves
  trust rather than establishing it, and the record is what makes the move
  auditable.
- **No self-service anything** — no sign-up that leads anywhere, no password
  reset, no invitation flow. All three need D-8 or a later milestone.
- **One organization per user.** A consultant working with two client tenants
  needs two accounts until the index is relaxed.

**Gained:**

- **Authorization cannot be obtained by anyone who merely reaches the network.**
  Registration grants nothing, and the thing that does grant is not on the
  network at all.
- **No unauthenticated or self-asserted path to tenant data**, which is what
  made S-21 partial.
- **Tenant isolation is structural.** It is a property of the EF model, so
  M01-04's CRUD inherits it without writing a predicate, and omitting one cannot
  cause a leak. **See A1** — as first implemented this was true of three roots,
  not seven — **and A3**, because a filter alone cannot keep a denormalized key
  honest: that needs a constraint.
- **Revocation is immediate and auditable.** Authorization is read per request,
  so a withdrawn membership takes effect on the next one; the revoked row stays
  as the record of who had access and who removed it.

## Verification

Which tests hold this decision in place. All run against a real PostgreSQL
database and the real API host.

| Claim                                                           | Test                                                                                                                                                                                                                        |
| :-------------------------------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Registration grants no membership and no role                   | `AuthorizationTests.Development_self_registration_creates_no_membership_and_no_roles`                                                                                                                                       |
| Registration is absent outside Development                      | `AuthorizationTests.Public_registration_is_absent_outside_development`                                                                                                                                                      |
| …and present in Development, for the same caller                | `AuthorizationTests.Registration_is_mapped_in_development_for_the_same_caller`                                                                                                                                              |
| An authenticated unprovisioned account reaches nothing          | `AuthorizationTests.An_authenticated_but_unprovisioned_account_reaches_no_organization`                                                                                                                                     |
| The data scope fails closed                                     | `AuthorizationTests.An_unprovisioned_session_has_a_closed_data_scope`                                                                                                                                                       |
| Provisioning creates exactly one organization and membership    | `AuthorizationTests.Operator_provisioning_creates_exactly_one_organization_and_one_membership`                                                                                                                              |
| Only an Approver may perform Approver-only actions              | `AuthorizationTests.A_member_cannot_perform_an_approver_only_action`, `An_approver_can_perform_an_approver_only_action`                                                                                                     |
| Both roles together work                                        | `AuthorizationTests.An_account_holding_both_roles_can_do_both`                                                                                                                                                              |
| Cross-organization reads are 404, and indistinguishable         | `AuthorizationTests.A_cross_organization_read_is_not_found_rather_than_forbidden`, `An_existing_other_organization_and_a_nonexistent_one_look_the_same`                                                                     |
| One tenant cannot read another's rows                           | `AuthorizationTests.Organization_a_cannot_read_organization_bs_rows`                                                                                                                                                        |
| Client-supplied organization and role values are ignored        | `AuthorizationTests.A_client_supplied_organization_is_ignored`, `A_client_supplied_role_cannot_reach_an_approver_only_action`                                                                                               |
| A **real global Identity role** named `Approver` grants nothing | `AuthorizationTests.A_global_identity_role_named_Approver_grants_nothing`                                                                                                                                                   |
| Changes take effect on the next request                         | `AuthorizationTests.Granting_the_approver_role_takes_effect_on_the_next_request`, `Revoking_a_membership_removes_access_on_the_next_request`                                                                                |
| The database refuses invalid and duplicate memberships          | Six `AuthorizationTests.The_database_refuses_*` cases                                                                                                                                                                       |
| Every organization-scoped query path is guarded                 | `OrganizationScopeGuardTests` (3 tests over the real EF model)                                                                                                                                                              |
| No HTTP route can grant a membership                            | `AuthorizationTests.No_http_endpoint_can_grant_a_membership`                                                                                                                                                                |
| The API host is never system-scoped                             | `AuthorizationTests.The_api_host_is_never_system_scoped`                                                                                                                                                                    |
| A password is never accepted as an argument                     | `ProvisioningCommandLineTests.A_password_passed_as_an_argument_is_refused`                                                                                                                                                  |
| Aggregate invariants                                            | `OrganizationMembershipTests`                                                                                                                                                                                               |
| **No tenant-owned root leaks across organizations** (A1)        | `AuthorizationTests.No_tenant_owned_aggregate_leaks_across_organizations` — all seven roots, list and by-id                                                                                                                 |
| A closed scope sees no tenant row of any kind (A1)              | `AuthorizationTests.A_closed_scope_sees_no_tenant_row_of_any_kind`                                                                                                                                                          |
| Every entity is filtered, owned, or exempt (A1)                 | `OrganizationScopeGuardTests.Every_entity_in_the_model_is_filtered_owned_or_explicitly_exempt`                                                                                                                              |
| Every tenant-owned root carries the key (A1)                    | `OrganizationScopeGuardTests.Every_tenant_owned_aggregate_root_carries_the_tenant_key`                                                                                                                                      |
| **Every privileged change is attributed** (A2)                  | `AuthorizationTests.The_persisted_audit_chain_names_the_operator_behind_every_change`, `The_member_roster_reports_who_last_changed_the_roles`, `OrganizationMembershipTests.Every_change_appends_an_attributed_audit_entry` |
| An unattributed role change is refused (A2)                     | `OrganizationMembershipTests.An_unattributed_role_change_is_refused`, `AuthorizationTests.The_database_refuses_a_role_change_with_half_an_audit_record`                                                                     |

The guard was verified by deliberately removing the filter from
**`DesignContract`** — a root that is tenant-owned _indirectly_, which is the
case the original guard could not see. Three guard tests failed
(`["DesignContract", "DesignContractVersion (owned by DesignContract)"]`) and so
did both two-tenant integration tests; all passed again on restore.

## Amendments

### A1 — Tenancy is transitive, so the tenant key is carried directly (2026-11-03)

- **Status:** ✅ Accepted — correction of a defect in the original implementation
- **Trigger:** W3-001, M01 Wave 3 independent review

**What was wrong.** The decision above says every organization-scoped query is
structurally filtered. As first implemented only `DesignProject`,
`AssetReference` and `JobRecord` were — the three roots that happened to carry
`OrganizationId` directly since M00. `DesignContract`, `DesignSystem`, `Screen`
and `FeatureInventory` are tenant-owned **through `ProjectId`**, had no filter,
and could be read by primary key across tenants. No shipping endpoint exposed
that path because none of those endpoints exist yet — but M01-05 and M01-06 are
precisely the tasks that add them.

**The guard did not catch it, and that is the more important half.** The
architecture test discovered the set to check by looking for a direct
`OrganizationId` property, then asserted every member of that set had a filter.
It passed while four roots were unprotected: **a guard that chooses its own
population can only confirm the author's model of what exists.**

**Amended position.**

1. **Every tenant-owned aggregate root carries `OrganizationId` directly** and
   has a global query filter — seven roots, one uniform rule. The key is
   denormalized from the project rather than filtered through a correlated
   subquery on every read, and it cannot drift because a project's organization
   is immutable. This follows the shape `AssetReference` and `JobRecord` already
   had.
2. **The guard partitions the whole EF model** instead of discovering a subset.
   Every entity type must be filtered, owned by something filtered, or on a
   by-value exemption list with a stated reason — and being unclassified is
   itself a failure, so a table added later cannot be missed.
3. **Owned collections need no filter of their own.** They have no independent
   query path, so their isolation is their root's. Asserted, not assumed.

### A2 — Every role change is attributed, not just the first grant (2026-11-03)

- **Status:** ✅ Accepted — correction of a defect in the original implementation
- **Trigger:** W3-002, M01 Wave 3 independent review

**What was wrong.** The decision says access is operator-controlled and
auditable. Re-provisioning called `ChangeRoles` without an actor, so when
operator A granted Member and operator B later granted **Approver**, the stored
membership — and the API's member roster — still named A. A log line named B. A
log is not an audit record: nobody designed its retention, integrity or
correlation, and the authoritative row said something false about a privileged
change.

**Amended position.** Every state change on a membership records who made it:

- `auth.organization_membership_events` is an **append-only chain** — one entry
  per provision, role change and revocation, each with the actor, the role
  transition, and the organization and user denormalized so the entry is
  self-contained. Written by the aggregate in the same operation as the change it
  records, so "a role changed and nothing recorded it" is unreachable.
- `roles_changed_by` / `roles_changed_at` on the membership answer "who last
  changed what this person may do" without a join, and **without overwriting**
  `provisioned_by`. The two questions are different.
- The member roster returns both.

**The limit, stated:** the chain is append-only by construction of the aggregate,
not by database privilege. A writer with table rights could still delete a row.
S-22 stays **partial** for that reason.

### A3 — Two corrections to A1 and A2 (2026-11-05)

- **Status:** ✅ Accepted — Product Owner, 2026-11-05
- **Trigger:** W3-RR-001 and W3-RR-002, M01 Wave 3 independent re-review
- **Corrects:** A1's "cannot drift" reasoning; A2's silence about upgrade

A1 and A2 are not edited. Each stands as the record of what was decided then;
this records what was wrong with it.

#### A3.1 — A denormalized tenant key needs a constraint, not a comment

**What A1 got wrong.** A1 denormalized `organization_id` onto the four
project-owned roots and justified not constraining it like this: _"Safe to
denormalize because a project's organization is immutable… so the two values
cannot drift."_

That is true of **mutation** and false of **creation**. Nothing stopped an
inconsistent pair being written in the first place, and the re-review proved it:

```sql
INSERT INTO app.design_contracts (id, project_id, organization_id, created_at)
VALUES (…, <organization-B-project>, <organization-A>, now());
-- INSERT 0 1
```

The query filter then classifies that row as organization A's data while the
project relationship says B. **A global filter cannot repair contradictory
ownership data after it is written.** Denormalizing created a second ownership
assertion, and the database has to guarantee the two agree.

**Amended position.**

1. `app.projects` exposes **`(organization_id, id)` as an alternate key**.
2. All five project-owned roots — `design_contracts`, `design_systems`,
   `screens`, `feature_inventories`, `assets` — carry a **composite foreign key**
   `(organization_id, project_id) → projects (organization_id, id)`, named
   `fk_<table>_project_tenant`.
3. The aggregates take a single **`ProjectOwnership`** value instead of two
   loose ids, so the ordinary call — `DesignContract.Create(project.Ownership,
clock)` — cannot express a mismatch. This is a shape, not a guarantee; the
   guarantee is the constraint.
4. The guard asserts the **relationship**, not only the filter, and asserts the
   project-owned set by value so a sixth root must be classified deliberately.

**This requires a narrow exception to a documented convention.** DATA_MODEL says
cross-module links are plain `uuid` columns with no foreign key, to preserve
module boundaries and future extraction. These five foreign keys cross from the
DesignContracts, DesignSystems, Screens, Redesign and Assets modules into
Projects.

**The exception is approved for exactly this purpose and no other** (Product
Owner, 2026-11-05): enforcing the tenant-ownership invariant between
`app.projects (organization_id, id)` and those five tables. Tenant isolation is
a security invariant; module extractability is a hypothetical future
convenience. **This is not permission for arbitrary cross-module foreign keys.**

#### A3.2 — The upgrade needs an audit baseline, and it must not fabricate history

**What A2 got wrong.** A2 created the append-only chain and said nothing about
rows that already existed. The re-review stopped a database at the prior
migration, seeded an active membership, applied the correction, and found
`count(*) = 0` events for it. Auditors would see different history depending
only on whether a grant predated the migration, and a later role change would
become the first entry in a chain with no recorded origin.

**Amended position.** A new event type, `MigrationBaseline`, written **only by
the upgrade migration** — the aggregate can never produce it, which
`OrganizationMembershipTests.The_aggregate_never_produces_a_migration_baseline`
asserts.

It states **what the migration observed when it ran**, and nothing more:

| Field            | Value                     | Why                                                  |
| :--------------- | :------------------------ | :--------------------------------------------------- |
| `actor`          | `system:migration/<name>` | The migration made this observation, not an operator |
| `occurred_at`    | migration time            | When it was observed, not when the grant happened    |
| `previous_roles` | **NULL**                  | What came before is not recoverable                  |
| `new_roles`      | the role set as found     | The one thing that was observable                    |

**The original attribution is not moved into it.** `provisioned_by` and
`created_at` stay on the membership row, where they were already recorded
separately. Combining those with the current role set into one entry — which the
first design did — would assert that the person held _these_ roles at _that_
time, and that is unknowable for any membership whose roles changed before the
upgrade. The Product Owner's correction removed that fabrication.

A **revoked** legacy membership also receives a reconstructed `Revoked` entry
using its recorded `revoked_by`, `revoked_at` and role set, because those three
fields directly support that statement.

**Two consequences worth stating rather than discovering:**

- **The chain is not chronological across the upgrade boundary.** A legacy
  revoked membership's reconstructed `Revoked` entry carries its real
  `revoked_at`, which precedes the baseline's migration-time stamp. Read the
  chain as "events at their recorded times, plus one observation made at
  migration time" — not as a timeline.
- **A `Revoked` entry on a membership that also has a `MigrationBaseline` was
  reconstructed**, not observed. The baseline's presence is the signal.

**Idempotency is per event type.** The first design guarded both inserts with a
single `NOT EXISTS (any event for this membership)`, which would have inserted
the baseline and then suppressed the revocation. Each insert now tests for its
own type, and the reconstruction additionally requires a baseline to exist — so
re-running the migration changes nothing.

#### What A3 does not change

Options A (ADR-0008) and the decision above stand. Proofing is still trusted
operator provisioning, registration still grants nothing, one active membership
per user, and nothing client-supplied establishes membership. S-22 remains
**partial**: the chain is append-only by construction of the aggregate, not by
database privilege, and pre-migration history is unknowable.

### A4 — Which memberships get a baseline, and what a rollback means (2026-11-05)

- **Status:** ✅ Accepted — Product Owner, 2026-11-05
- **Trigger:** W3-RR2-001 and W3-RR2-003, M01 Wave 3 re-review round 2
- **Relates to:** A3.2, whose decision text stands; this records a defect in its
  implementation and decides a question A3.2 left open

A3's decision — a baseline states what the migration observed — was right and is
unchanged. Two things were wrong underneath it.

#### A4.1 — A baseline belongs only to a membership with no audit events at all

A3.2 says _"memberships that predate the event table receive one entry"_. The
implementation asked a different question: it excluded only a membership that
already had a `MigrationBaseline`.

Those are not the same set. **Migration 5 created the event table**, so a
deployment running at migration 5 writes genuine `Provisioned` entries through
the aggregate. Every such membership was given a baseline too — which labels an
observed chain as reconstructed and destroys the only signal that tells them
apart. The re-review reproduced it:

```text
Provisioned       | observed-operator
MigrationBaseline | system:migration/EnforceProjectTenantOwnershipAndBaselineAudit
```

**The predicate is now "no audit events at all."** A membership that has any
event does not predate the chain, by definition. The reconstruction of a
revocation is additionally conditioned on a baseline existing, so a membership
revoked through the aggregate is excluded twice over.

This is a correction to an implementation, not to the decision — but it is
recorded here because A3.2's own sentence is what the implementation was supposed
to say, and a reader comparing the two deserves to know which one was true of
the shipped code.

#### A4.2 — Rolling this migration back removes what it wrote, and nothing else

A3 said nothing about rollback, and the migration's `Down()` restored the old
event-type check constraint while `MigrationBaseline` rows were still present.
PostgreSQL answered `23514`, deterministically, for any database holding a single
membership: **an emergency rollback failed on data the migration itself had
created.**

**Decided:** this migration is **reversible**, with these semantics.

| On rollback                                  | Why                                                                                                                                                |
| :------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MigrationBaseline` rows are **deleted**     | The type does not exist in the older schema, and they were this migration's own output                                                             |
| Reconstructed `Revoked` rows are **deleted** | Also this migration's output. Identifiable only while their baseline exists, so deleted first                                                      |
| **Observed** events are **kept**             | They predate the migration. A rollback that emptied the table would destroy real audit history                                                     |
| Repaired tenant keys are **not restored**    | Those values were wrong and the project is the authoritative owner. Putting an incorrect tenant key back is the one thing a rollback should refuse |

The last row is the limit worth naming: a downgrade returns the **schema** and
this migration's **audit rows**, not the pre-repair data. Anyone rolling back to
investigate a mismatch will not find the mismatch.

#### Verification

| Claim                                                  | Test                                                                                                                                              |
| :----------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------ |
| An observed membership is not baselined (A4.1)         | `MigrationUpgradeTests.A_membership_with_an_observed_event_is_not_baselined_by_the_upgrade`                                                       |
| …nor is one revoked through the aggregate              | same test — no baseline and no duplicate revocation                                                                                               |
| …while a genuinely pre-chain membership still is       | same test — the narrower predicate did not disable the feature                                                                                    |
| The backfill statements are idempotent (A3.2)          | `MigrationUpgradeTests.Re_running_the_backfill_statements_changes_nothing` — runs the real SQL three times, asserting per membership and per type |
| The migration rolls back with baselines present (A4.2) | `MigrationUpgradeTests.The_migration_rolls_back_with_baselined_memberships_present`                                                               |
| …and the rollback keeps observed events                | same test                                                                                                                                         |
| …and the forward path still works afterwards           | same test — a rollback is not a one-way door                                                                                                      |

The backfill and rollback statements live in `MembershipAuditBaselineSql` so the
idempotency test runs **exactly the statements the migration runs**. Asserting it
by re-migrating an already-current database proved nothing, because EF skips an
applied migration — which is how the first idempotency claim came to be a no-op.
That file carries a "never edit this SQL" warning: an applied migration must keep
meaning what it meant when it was applied.

## Not chosen

**Confirmed email.** The obvious answer, and it is blocked: confirmation needs a
mail transport, which is **D-8** and unresolved. Enabling
`RequireConfirmedEmail` with no sender would lock out every account including the
first one. It remains the right MVP-plus answer and should be reconsidered the
moment D-8 lands.

**An external identity provider doing its own proofing.** The independent
architecture recommendation at D-7, and rejected there by the Product Owner for
reasons recorded in ADR-0008: it requires authorizing an external dependency and
pulls D-5 forward. Nothing about D-9 changes that calculus, and re-litigating it
here would be deciding D-7 again by the back door.

**An admin HTTP endpoint, or a first-run bootstrap route.** Rejected because the
control would be worth less than it appears. An admin API is reachable by anyone
who reaches the network, so it needs its own authorization — which is the
problem it was supposed to solve. A bootstrap route is reachable by _everyone_
until it has been used once, and "we will remove it after setup" is not a
control. A console tool requires shell access to a host holding the connection
string, which is a materially higher bar and one the deployment already enforces.

**Self-service sign-up with an approval queue.** A real option, and more work
than M01-03 is authorized to do: it needs a notification channel to tell an
approver that a request is waiting (D-8 again), plus a request aggregate, plus
endpoints. It is the natural successor once D-8 exists.

**An organization id on the user row, rather than a membership aggregate.** It
would express the single-active-membership rule just as well and with less code.
Rejected because of what it would cost later: supporting several organizations
would mean moving organization and role data out of the user table, i.e. a
migration of the authorization boundary itself, performed under pressure when
the first multi-tenant customer arrives. A filtered unique index gives the same
guarantee today and relaxing it later drops an index.

**An `Admin` role.** Explicitly out of scope for the MVP (D-3 resolved to two
roles). Provisioning is the operator's job and it is not done over HTTP, so no
role is needed to perform it.
