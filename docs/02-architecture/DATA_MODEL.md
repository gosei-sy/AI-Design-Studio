# Data Model

Schema: `app`. Migration history: `app.__ef_migrations_history`.
Migrations: `20261020053356_InitialSchema`, then
`20261020094048_ReviewCorrections` (the M00 independent review's schema
corrections). Both verified applied to PostgreSQL 15.19.

All names are `snake_case` (via `EFCore.NamingConventions`). All ids are
`uuid` holding UUID v7 generated in the domain.

---

## 1. Entity relationships

```
   auth.users ──────────────┐           (ASP.NET Core Identity, ADR-0008)
        │                   │
        │  FK               │ FK
        ▼                   ▼
   auth.organization_memberships ──► auth.organizations
        roles: Member | Approver (flags)      │
        one ACTIVE row per user               │
                                              │  (by id only — no FK)
                 ┌────────────────────────────┘
                 ▼
          ┌──────────────┐
          │   projects    │  kind: NewProduct | Redesign (immutable)
          └──────┬────────┘
                 │  (by id only — no FK across module boundaries)
   ┌─────────────┼──────────────┬───────────────┬──────────────┐
   ▼             ▼              ▼               ▼              ▼
design_      design_         screens      feature_         assets
contracts    systems            │          inventories         │
   │             │              │               │              │
   ▼             ▼              ▼               ▼              │
design_      design_        screen_       feature_             │
contract_    system_        versions      inventory_           │
versions     versions          │            items              │
   │                            │                              │
   └──── hash + id recorded ───┘                               │
         on each screen version                                │
                                                               │
          ┌──────────────┐                                     │
          │     jobs     │  durable queue (ADR-0005)           │
          └──────────────┘                                     │
                                                    blob storage (keyed, not FK)
```

Version tables are **EF owned collections**: part of their aggregate, never
loaded or mutated independently.

Cross-module links are plain `uuid` columns with **no foreign key**. That is
deliberate: a database-level FK between modules would be a dependency the
architecture tests forbid at the code level, and would block extracting a module
later. Referential integrity across modules is an application invariant.

**The two foreign keys in the diagram are the exception, and both live inside the
`auth` schema** — membership → organization and membership → user. They exist
because a membership row is a grant of access: one pointing at a user id that was
never issued would hand organization access to whoever received that id later, and
one pointing at a nonexistent organization is unresolvable. Neither crosses a
module boundary (organizations and memberships are one module), and neither is the
kind of convenience FK the rule above is about.

`app.projects.organization_id` has **no** FK to `auth.organizations`, following the
cross-module rule. What protects that column is the query filter in §9a, not
referential integrity.

**One further exception, narrowly approved.** The five composite tenant-ownership
foreign keys in §10b cross from the DesignContracts, DesignSystems, Screens,
Redesign and Assets modules into Projects. They were approved by the Product
Owner on 2026-11-05 **for that purpose and no other** (ADR-0009 A3.1): tenant
isolation is a security invariant, and module extractability is a hypothetical
future convenience. This is **not** permission for arbitrary cross-module foreign
keys.

---

## 2. Projects module

### `app.projects`

| Column            | Type                  | Notes                                               |
| :---------------- | :-------------------- | :-------------------------------------------------- |
| `id`              | uuid PK               |                                                     |
| `organization_id` | uuid, indexed         | Tenant. Leading segment of every asset storage key. |
| `name`            | varchar(200) NOT NULL |                                                     |
| `kind`            | varchar(32) NOT NULL  | `NewProduct` \| `Redesign`, with a check constraint |
| `created_at`      | timestamptz NOT NULL  |                                                     |
| `updated_at`      | timestamptz NULL      |                                                     |

```sql
CONSTRAINT ck_projects_kind CHECK (kind IN ('NewProduct', 'Redesign'))
```

`kind` is immutable after creation: the two workflows have different required
inputs and different completion criteria, so switching would invalidate gate
state. Because it is immutable _and_ selects the workflow, an out-of-range
value would be permanently wrong — so the domain rejects undefined enum members
and the check constraint stops anything that bypasses the domain.

---

## 3. DesignContracts module — the source of truth

### `app.design_contracts`

| Column                     | Type             | Notes                    |
| :------------------------- | :--------------- | :----------------------- |
| `id`                       | uuid PK          |                          |
| `project_id`               | uuid, **UNIQUE** | One contract per project |
| `created_at`, `updated_at` | timestamptz      |                          |

`project_id` is unique because the API is singular
(`/projects/{id}/contract`) and the product treats this as _the_ source of
truth. Two contracts per project could each hold an approved version, leaving
"which one is in force?" with no answer. The filtered approval index below
cannot express this: it allows one approved version per contract, not one
contract per project.

### `app.design_contract_versions`

| Column           | Type                 | Notes                                           |
| :--------------- | :------------------- | :---------------------------------------------- |
| `id`             | uuid PK              |                                                 |
| `contract_id`    | uuid NOT NULL        | Owner FK                                        |
| `version_number` | int NOT NULL         | Dense, monotonic from 1                         |
| `status`         | varchar(32) NOT NULL | `Draft` \| `Approved` \| `Superseded`           |
| `payload_json`   | **jsonb** NOT NULL   | Validated against `design-contract.schema.json` |
| `payload_hash`   | varchar(64) NULL     | SHA-256, set at approval                        |
| `approved_by`    | varchar(256) NULL    |                                                 |
| `approved_at`    | timestamptz NULL     |                                                 |
| `created_at`     | timestamptz NOT NULL |                                                 |

**Indexes:**

```sql
UNIQUE (contract_id, version_number)

CREATE UNIQUE INDEX ux_contract_single_approved
  ON app.design_contract_versions (contract_id)
  WHERE status = 'Approved';
```

The filtered unique index is load-bearing, not belt-and-braces. The aggregate
cannot enforce "at most one approved version" under concurrency: two
transactions can each read a state with no approved version and each approve a
different one. The index is what actually holds the invariant. Verified by
`PersistenceTests.The_database_rejects_a_second_approved_contract_version`,
which bypasses the aggregate and asserts the constraint name.

`payload_json` is real `jsonb`, not text — verified by
`A_payload_column_is_stored_as_jsonb_and_is_queryable_as_json`, which uses the
`->>` operator (it would fail on a text column).

---

## 4. DesignSystems module

### `app.design_systems` / `app.design_system_versions`

Same structure and the same three-state model as contracts, with `tokens_json`
(jsonb) in place of `payload_json`, validated against
`design-system.schema.json`, and `tokens_hash` (varchar(64)) holding the
SHA-256 fixed at approval — the counterpart of the contract's `payload_hash`.

`design_systems.project_id` is **UNIQUE**, for the same reason as contracts.

```sql
UNIQUE (project_id)                       -- on app.design_systems
UNIQUE (design_system_id, version_number)

CREATE UNIQUE INDEX ux_design_system_single_approved
  ON app.design_system_versions (design_system_id)
  WHERE status = 'Approved';
```

The token set is intentionally richer than any provider's theme model
(ADR-0004). Nothing here is constrained to what a provider can represent.

---

## 5. Screens module

### `app.screens`

| Column       | Type                  | Notes                |
| :----------- | :-------------------- | :------------------- |
| `id`         | uuid PK               |                      |
| `project_id` | uuid                  |                      |
| `key`        | varchar(256) NOT NULL | Stable slug or route |
| `title`      | varchar(256) NOT NULL |                      |

```sql
UNIQUE (project_id, key)
```

`key` is how a redesign matches proposed screens to the existing application, so
it must be stable across versions.

### `app.screen_versions`

| Column                       | Type                 | Notes                                                                         |
| :--------------------------- | :------------------- | :---------------------------------------------------------------------------- |
| `id`                         | uuid PK              |                                                                               |
| `screen_id`                  | uuid NOT NULL        | Owner FK                                                                      |
| `version_number`             | int NOT NULL         |                                                                               |
| `origin`                     | varchar(32) NOT NULL | `ProviderGenerated` \| `ImportedScreenshot` \| `CapturedMarkup` \| `Authored` |
| `review_state`               | varchar(32) NOT NULL | `Draft` \| `InReview` \| `Approved` \| `Rejected` \| `Superseded`             |
| `contract_version_id`        | uuid NOT NULL        | Contract this was produced against                                            |
| `contract_payload_hash`      | varchar(64) NOT NULL | Contract bytes at production time                                             |
| `provider_id`                | varchar(64) NULL     | e.g. `google-stitch`                                                          |
| `provider_artifact_id`       | varchar(256) NULL    | Provider-side screen id                                                       |
| `approved_by`, `approved_at` |                      |                                                                               |
| `rejection_reason`           | varchar(4000) NULL   | Reviewer feedback, retained                                                   |
| `rejected_by`                | varchar(256) NULL    |                                                                               |
| `rejected_at`                | timestamptz NULL     |                                                                               |

```sql
UNIQUE (screen_id, version_number)

CREATE UNIQUE INDEX ux_screen_single_approved
  ON app.screen_versions (screen_id)
  WHERE review_state = 'Approved';
```

`contract_version_id` **and** `contract_payload_hash` together are the
anti-drift mechanism (ADR-0002). Storing only the id would allow an edited
contract payload to pass under the same id.

`origin` distinguishes `ImportedScreenshot` from `CapturedMarkup` because they
carry fundamentally different fidelity (ADR-0006). Provenance is queryable, not
inferred.

---

## 6. Redesign module

### `app.feature_inventories`

| Column       | Type             | Notes                     |
| :----------- | :--------------- | :------------------------ |
| `id`         | uuid PK          |                           |
| `project_id` | uuid, **UNIQUE** | One inventory per project |

### `app.feature_inventory_items`

| Column                | Type                  | Notes                                  |
| :-------------------- | :-------------------- | :------------------------------------- |
| `id`                  | uuid PK               |                                        |
| `inventory_id`        | uuid NOT NULL         | Owner FK                               |
| `key`                 | varchar(256) NOT NULL |                                        |
| `description`         | varchar(2000)         |                                        |
| `coverage`            | varchar(32) NOT NULL  | `Unaddressed` \| `Covered` \| `Waived` |
| `covering_screen_ids` | **uuid[]**            | Non-empty only when `Covered`          |
| `waiver_reason`       | varchar(2000) NULL    | Required when `Waived`                 |
| `waived_by`           | varchar(256) NULL     | Required when `Waived`                 |

```sql
UNIQUE (inventory_id, key)
```

`covering_screen_ids` is a native PostgreSQL `uuid[]`, mapped through a private
backing field. The covering screens are only ever read as a set belonging to
one item — never joined or queried independently — so a join table would add a
relation with no query to serve.

The field is private because the public property must be a read-only wrapper: a
caller able to clear the live list after `MarkCovered` validated it would leave
an item `Covered` with nothing covering it, and `EnsureReadyForHandoff` — which
only blocks on `Unaddressed` — would pass. That is the no-functional-loss gate
defeated with one method call.

`coverage` defaults to `Unaddressed`, which blocks handoff (ADR-0007). Silence
is never a decision.

---

## 7. Assets module

### `app.assets`

| Column               | Type                              | Notes                                                                  |
| :------------------- | :-------------------------------- | :--------------------------------------------------------------------- |
| `id`                 | uuid PK                           |                                                                        |
| `organization_id`    | uuid                              | Indexed with `project_id`                                              |
| `project_id`         | uuid                              |                                                                        |
| `kind`               | varchar(32) NOT NULL              | `Screenshot` \| `MarkupSnapshot` \| `ScreenRender` \| `DesignDocument` |
| `storage_key`        | varchar(512) NOT NULL, **UNIQUE** | Server-generated, opaque                                               |
| `content_type`       | varchar(128) NOT NULL             |                                                                        |
| `size_bytes`         | bigint NOT NULL                   | > 0 enforced in the domain                                             |
| `content_hash`       | varchar(64) NOT NULL              | SHA-256 of stored bytes                                                |
| `original_file_name` | varchar(512)                      |                                                                        |

Bytes live in the storage backend; this table holds metadata only.
`storage_key` is never client-supplied.

---

## 8. Jobs module

### `app.jobs`

| Column             | Type                  | Notes                                                          |
| :----------------- | :-------------------- | :------------------------------------------------------------- |
| `id`               | uuid PK               |                                                                |
| `organization_id`  | uuid                  | For future quota accounting                                    |
| `kind`             | varchar(64) NOT NULL  | e.g. `screen.generate`                                         |
| `payload_json`     | **jsonb** NOT NULL    |                                                                |
| `idempotency_key`  | varchar(256) NOT NULL |                                                                |
| `state`            | varchar(32) NOT NULL  | `Pending` \| `Running` \| `Succeeded` \| `Dead` \| `Cancelled` |
| `attempt`          | int NOT NULL          | Incremented on each claim                                      |
| `max_attempts`     | int NOT NULL          | Default 3                                                      |
| `run_after`        | timestamptz NOT NULL  | Scheduling **and** retry backoff                               |
| `lease_owner`      | varchar(128) NULL     | `machine:pid:guid`                                             |
| `lease_expires_at` | timestamptz NULL      | Crash-recovery boundary                                        |
| `last_error`       | varchar(4000) NULL    | Truncated on write                                             |
| `completed_at`     | timestamptz NULL      |                                                                |

**Indexes:**

```sql
UNIQUE (organization_id, kind, idempotency_key)  -- duplicate billable submissions
INDEX (organization_id)                          -- organization-scoped job queries
INDEX (state, run_after)                         -- serves the claim query
```

`kind` is a string constant rather than an enum member because the value is
persisted and must survive code changes that reorder members.

Idempotency is **scoped by organization**. Keys are client-supplied, so a global
unique index would let one tenant's key collide with another's — and the
pre-insert lookup would then return a job belonging to a different
organization, a cross-tenant disclosure.

Uniqueness is enforced by the index, not only by the pre-insert lookup, so an
API race cannot create two billable jobs. Under a true race the loser's commit
fails and it recovers the winner's job via `FindByIdempotencyKeyAsync`; see
ADR-0005 amendment A4 for why `EnqueueAsync` cannot do that itself.

Verified by `The_database_rejects_a_duplicate_key_within_one_organization`,
`The_same_idempotency_key_in_two_organizations_creates_two_jobs` (the
cross-tenant case), `The_same_idempotency_key_is_allowed_for_a_different_job_kind`,
and `Losing_an_enqueue_race_resolves_to_the_winning_job`.

---

## 9. Key generation: a correctness note

`AppDbContext.ConfigureDomainGeneratedKeys` sets every `Entity.Id` to
`ValueGenerated.Never`.

This is a bug fix, not a preference. `Entity` assigns a UUID v7 on construction,
but EF's convention for a `Guid` key is "generated on add", and EF then uses a
non-default key value to infer that the entity **already exists**. A version
newly added to an aggregate's owned collection was therefore tracked as
`Modified`, producing an `UPDATE` that matched no row —
`DbUpdateConcurrencyException: expected to affect 1 row(s), but actually
affected 0`.

Found during M00 by
`PersistenceTests.Superseding_a_contract_version_persists_both_versions_with_one_approved`,
which is the regression test. Removing the configuration reintroduces the
failure.

UUID v7 is used over v4 because it is time-ordered, which keeps index inserts
sequential and makes `ORDER BY id` approximate creation order.

---

## 10. Conventions

| Convention                                 | Rationale                                                                                                                         |
| :----------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------------- |
| `snake_case` names                         | PostgreSQL idiom; avoids quoting every identifier                                                                                 |
| Enums as `varchar`                         | Legible in SQL; reordering an enum cannot change stored meaning                                                                   |
| `timestamptz` everywhere                   | No naive local times                                                                                                              |
| `jsonb` for documents                      | Queryable and indexable, unlike text                                                                                              |
| Owned collections for versions             | Versions are part of their aggregate; independent mutation would bypass invariants                                                |
| No FK across modules, bar tenant ownership | Preserves module boundaries and future extraction. The only approved exception is the five composite keys in §10b (ADR-0009 A3.1) |
| Filtered unique indexes for approval       | The only mechanism that holds under concurrency                                                                                   |
| Query filter on every `organization_id`    | Tenant isolation as a model property, so one missing `WHERE` cannot leak                                                          |

## 9a. Organization scoping — tenant isolation is a model property

Every entity carrying an `organization_id` has a **global EF Core query filter**:

```csharp
e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);
```

**All seven tenant-owned aggregate roots have one:** `DesignProject`,
`DesignContract`, `DesignSystem`, `Screen`, `FeatureInventory`,
`AssetReference`, `JobRecord`. The scope comes from `IOrganizationScope`,
resolved per request from the caller's membership and from nothing the caller
sends (ADR-0009).

**Why contracts, design systems, screens and inventories carry
`organization_id` even though they also carry `project_id`.** Tenancy is
transitive — they belong to a project, which belongs to an organization — and a
transitive key cannot be a global filter without a correlated subquery on every
read of every table. Carrying the key directly keeps one uniform rule. W3-001
established this: the first implementation filtered only the three roots that
already had the column, leaving the other four readable by primary key across
tenants (ADR-0009 A1).

**Denormalizing creates a second ownership assertion, and the database makes the
two agree.** The claim that the values "cannot drift" was true of mutation — a
project's organization is immutable — and **false of creation**: nothing stopped
an inconsistent pair being written, and the re-review proved PostgreSQL accepted
organization A's key on organization B's project (W3-RR-001). A query filter
cannot repair contradictory ownership data after it is written. See §10b.

Three states, and the default is the closed one:

| Scope            | Rows visible             | Who gets it                                                   |
| :--------------- | :----------------------- | :------------------------------------------------------------ |
| **None**         | none                     | the default — any context built without a scope               |
| **Organization** | that organization's only | an HTTP request with a resolved membership                    |
| **System**       | all                      | the job worker and `aids-provision`, by explicit registration |

Two things to know before writing a query:

1. **A forgotten authorization check produces an empty result set, not a
   cross-tenant read.** That is the whole point of defaulting to None.
2. **Raw SQL is not filtered.** The job claim statement
   (`FOR UPDATE SKIP LOCKED`) is raw ADO and sees every tenant's queue — correct
   for a dispatcher, and it means the filter is not what protects that path.
   Running only in the worker is.

`auth.organization_memberships` is deliberately **not** filtered: it is what
produces the scope, so filtering it would be circular. Every query against it
carries its own organization predicate.

`OrganizationScopeGuardTests` **partitions the whole EF model** rather than
discovering a subset: every entity type must be filtered, owned by something
filtered, or on a by-value exemption list with a stated reason. Being
unclassified is itself a failure, so a table added in a later milestone cannot be
missed. That shape is the W3-001 correction — the previous guard chose its own
population by looking for an `organization_id` property, so it could only ever
confirm the author's model of what existed.

## 10b. Tenant ownership is referential, not only filtered

`app.projects` exposes **`(organization_id, id)`** as an alternate key
(`ak_projects_organization_id_id`). Every project-owned root references that
pair:

| Table                     | Constraint                              |
| :------------------------ | :-------------------------------------- |
| `app.design_contracts`    | `fk_design_contracts_project_tenant`    |
| `app.design_systems`      | `fk_design_systems_project_tenant`      |
| `app.screens`             | `fk_screens_project_tenant`             |
| `app.feature_inventories` | `fk_feature_inventories_project_tenant` |
| `app.assets`              | `fk_assets_project_tenant`              |

Each is `(organization_id, project_id) → projects (organization_id, id)` with
`ON DELETE RESTRICT`: no delete path exists yet, and a future one should decide
what happens to children rather than inherit a silent cascade.

**What this stops.** A row claiming one organization while sitting on another's
project. Before the constraint PostgreSQL accepted exactly that, and the query
filter then served the row to the wrong tenant (W3-RR-001). It is reproduced
permanently by
`MigrationUpgradeTests.A_mismatched_tenant_and_project_is_accepted_before_the_constraint_and_refused_after`,
which asserts the insert succeeds before the migration and is refused after it.

**Index consequences.** The alternate key's unique index leads with
`organization_id`, so it subsumes the old `ix_projects_organization_id`, which was
dropped. Each foreign key brings its own `(organization_id, project_id)` index,
which likewise subsumes the standalone `organization_id` index on those four
tables — also dropped. `app.jobs` keeps its own, having no project.

**The application side.** The aggregates take a single `ProjectOwnership` value
rather than two loose `Guid`s, so `DesignContract.Create(project.Ownership,
clock)` cannot pair a project with a foreign organization. That is a shape which
makes the mistake conspicuous; the guarantee is the constraint.

**The upgrade.** The migration resolves a mismatched row to its project's
organization — the project is the authoritative owner, so the value is knowable —
and **fails** on an orphaned `project_id`, because nothing can determine the right
tenant and deleting a tenant's row is not a migration's decision.

## 10a. `auth.organizations` and `auth.organization_memberships`

### `auth.organizations`

| Column       | Type           | Notes                                |
| :----------- | :------------- | :----------------------------------- |
| `id`         | `uuid`         | PK, UUID v7 from the domain          |
| `name`       | `varchar(200)` | **unique** (`ux_organizations_name`) |
| `created_at` | `timestamptz`  |                                      |
| `updated_at` | `timestamptz`  | nullable                             |

The name is unique because operator provisioning selects an organization by it,
and an ambiguous `--organization Acme` would mean the operator does not know
which tenant they just granted access to.

### `auth.organization_memberships`

| Column             | Type           | Notes                                              |
| :----------------- | :------------- | :------------------------------------------------- |
| `id`               | `uuid`         | PK                                                 |
| `organization_id`  | `uuid`         | FK → `auth.organizations` (`RESTRICT`)             |
| `user_id`          | `uuid`         | FK → `auth.users` (`CASCADE`)                      |
| `roles`            | `integer`      | flags: `Member = 1`, `Approver = 2`                |
| `status`           | `varchar(32)`  | `Active` or `Revoked`                              |
| `provisioned_by`   | `varchar(256)` | **required** — who first granted access            |
| `roles_changed_by` | `varchar(256)` | nullable — who **last changed the roles** (W3-002) |
| `roles_changed_at` | `timestamptz`  | nullable                                           |
| `revoked_at`       | `timestamptz`  | nullable                                           |
| `revoked_by`       | `varchar(256)` | nullable                                           |
| `created_at`       | `timestamptz`  |                                                    |

**Constraints, and what each one stops:**

| Constraint                                                                                | Stops                                                                                        |
| :---------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------- |
| `ux_organization_memberships_active_user` — unique on `user_id` where `status = 'Active'` | A second active membership. The MVP's one-organization-per-user rule, held under concurrency |
| `ck_organization_memberships_roles` — `roles > 0 AND roles <= 3`                          | A membership with no roles, or a role this build does not define                             |
| `ck_organization_memberships_status`                                                      | Any status but `Active` or `Revoked`                                                         |
| `ck_organization_memberships_revoked_audit`                                               | A revocation with no record of who or when                                                   |
| `ck_organization_memberships_active_audit`                                                | An active row claiming it was revoked                                                        |
| `ck_organization_memberships_role_change_audit`                                           | A role change recording a time but no actor, or the reverse                                  |
| `ck_organization_membership_events_type`                                                  | An audit entry with an unknown event type                                                    |
| `fk_organization_memberships_user`                                                        | A grant of access to a user id that does not exist                                           |
| `fk_organization_memberships_organization`                                                | A grant of access to an organization that does not exist                                     |

`roles` is an **integer flags column**, breaking this schema's "enums as
`varchar`" convention. A flags value has no single valid string form
("Member, Approver" would have to be parsed), and the integer is what lets "at
least one role" and "no unknown roles" be check constraints rather than prose.

Revoked rows are kept rather than deleted: they are the record of who had access
and who withdrew it. The filtered index constrains active rows only, so a user
may accumulate any amount of history.

`roles_changed_by` does **not** overwrite `provisioned_by`: "who first granted
this person access" and "who last changed what they may do" are different
questions, and a promotion that replaced the first answer would destroy it
(W3-002, ADR-0009 A2).

### `auth.organization_membership_events` — the append-only audit chain

| Column            | Type           | Notes                                                           |
| :---------------- | :------------- | :-------------------------------------------------------------- |
| `id`              | `uuid`         | PK                                                              |
| `membership_id`   | `uuid`         | FK → `auth.organization_memberships` (owned)                    |
| `organization_id` | `uuid`         | denormalized, so an entry is self-contained                     |
| `user_id`         | `uuid`         | denormalized, same reason                                       |
| `type`            | `varchar(32)`  | `Provisioned`, `RolesChanged`, `Revoked` or `MigrationBaseline` |
| `previous_roles`  | `integer`      | nullable — null for `Provisioned`                               |
| `new_roles`       | `integer`      | nullable — null for `Revoked`                                   |
| `actor`           | `varchar(256)` | **required** for every entry                                    |
| `occurred_at`     | `timestamptz`  |                                                                 |

An **owned collection** of the membership, so the aggregate writes an entry in
the same `SaveChanges` as the change it records — "a role changed and nothing
recorded it" is not a reachable state. Deliberately **not** `AutoInclude`d: the
authorization path reads a membership on every request and never needs its
history, and loading the chain each time would make the hottest query in the
application grow without bound.

**The limit, stated:** append-only by construction of the aggregate, not by
database privilege. A writer with table rights could still delete a row, which is
why S-22 remains partial.

### `MigrationBaseline` — what the upgrade observed

Memberships that predate the event table receive one entry of type
`MigrationBaseline`, written **only by the upgrade migration** (W3-RR-002,
ADR-0009 A3.2). It records an observation, not a reconstructed grant:

| Field            | Value                     | Why                                        |
| :--------------- | :------------------------ | :----------------------------------------- |
| `actor`          | `system:migration/<name>` | The migration observed it, not an operator |
| `occurred_at`    | migration time            | When it was observed                       |
| `previous_roles` | **NULL**                  | Not recoverable                            |
| `new_roles`      | the role set as found     | The only observable fact                   |

`provisioned_by` and `created_at` stay on the membership row as the separately
recorded original attribution; they are deliberately **not** folded into the
event, because that would assert the person held _these_ roles at _that_ time —
unknowable for any membership whose roles changed before the upgrade.

A **revoked** legacy membership also gets a reconstructed `Revoked` entry from
its recorded `revoked_by` / `revoked_at` / role set.

**Two things to know when reading a chain:**

- **It is not chronological across the upgrade boundary.** A reconstructed
  `Revoked` entry carries its real timestamp, which precedes the baseline's
  migration-time stamp.
- **A `Revoked` entry on a membership that also has a `MigrationBaseline` was
  reconstructed**, not observed.

**"Predates the event table" means "has no audit events at all"** — not "has no
baseline yet" (W3-RR2-001, ADR-0009 A4.1). Migration 5 created the event table,
so a deployment running at migration 5 writes genuine `Provisioned` entries; the
first predicate baselined those too, labelling an observed chain as
reconstructed. A membership with any event does not predate the chain.

The reconstruction of a revocation is guarded **per event type and** conditioned
on a baseline existing, so a membership revoked through the aggregate is excluded
twice over. A single "any event exists" guard across both inserts would have
written the baseline and then suppressed the revocation.

**Rollback** (ADR-0009 A4.2). Reversing the migration deletes the baselines and
the reconstructed revocations it wrote — in that order, since a reconstruction is
identifiable only while its baseline exists — and **keeps observed events**, which
predate it. It does **not** restore a tenant key the migration repaired: that
value was wrong, and putting it back is the one thing a rollback should refuse.
Without the deletion the old event-type constraint came back while baseline rows
were present, failing every downgrade with `23514`.

Both statement sets live in `MembershipAuditBaselineSql` so the idempotency test
runs exactly what the migration runs. That file must never be edited: an applied
migration has to keep meaning what it meant when it was applied.

## 11. Not yet modelled

- A second active organization membership per user — one active membership is the
  MVP rule (ADR-0009). The table shape already allows several; relaxing it means
  dropping `ux_organization_memberships_active_user`.
- Review comments and annotations (M02).
- Handoff artifacts and export manifests (M03).
- Job retention and archival (M02). Dead jobs currently accumulate.
- Audit log table. Approval metadata is on the aggregates; a dedicated
  append-only audit trail is deferred.
