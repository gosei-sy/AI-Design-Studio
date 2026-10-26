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
          organizations (external identity provider — not a table in M00)
                 │
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

| Convention                           | Rationale                                                                          |
| :----------------------------------- | :--------------------------------------------------------------------------------- |
| `snake_case` names                   | PostgreSQL idiom; avoids quoting every identifier                                  |
| Enums as `varchar`                   | Legible in SQL; reordering an enum cannot change stored meaning                    |
| `timestamptz` everywhere             | No naive local times                                                               |
| `jsonb` for documents                | Queryable and indexable, unlike text                                               |
| Owned collections for versions       | Versions are part of their aggregate; independent mutation would bypass invariants |
| No FK across modules                 | Preserves module boundaries and future extraction                                  |
| Filtered unique indexes for approval | The only mechanism that holds under concurrency                                    |

## 11. Not yet modelled

- `organizations` / `users` — awaiting the authorization decision (M01).
- Review comments and annotations (M02).
- Handoff artifacts and export manifests (M03).
- Job retention and archival (M02). Dead jobs currently accumulate.
- Audit log table. Approval metadata is on the aggregates; a dedicated
  append-only audit trail is deferred.
