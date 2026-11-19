# SPIKE-05: Storing and retrieving versioned Design Contracts

**Status:** ✅ **Verified against PostgreSQL 15.19**
**Date:** 2026-10-20

---

## Question

Can versioned Design Contracts be stored and retrieved with approval
immutability that actually holds — including under concurrency?

This is the system's load-bearing mechanism (ADR-0002). If an approved contract
can change, every downstream approval becomes meaningless.

---

## Verified: the aggregate

`DesignContract` with 14 unit tests against a controllable clock.

| Invariant                                         | Error code                    | Verified |
| :------------------------------------------------ | :---------------------------- | :------- |
| Version numbers dense and monotonic from 1        | —                             | ✅       |
| One draft open at a time                          | `contract.draft_already_open` | ✅       |
| Approving with no draft fails                     | `contract.no_draft`           | ✅       |
| Approver identity required                        | `contract.approver_required`  | ✅       |
| **Approved version cannot be edited**             | `contract.version_immutable`  | ✅       |
| **Superseded version cannot be edited**           | `contract.version_immutable`  | ✅       |
| A draft _can_ be edited                           | —                             | ✅       |
| Approval supersedes the previous approved version | —                             | ✅       |
| Exactly one `Approved` at a time                  | —                             | ✅       |
| Approval records approver, timestamp, SHA-256     | —                             | ✅       |
| Discarding a draft frees its version number       | —                             | ✅       |
| Empty payload rejected                            | `contract.payload_required`   | ✅       |
| Hash is stable and differs per payload            | —                             | ✅       |

The hash is fixed **at approval**, not at creation. A draft has
`PayloadHash == null`, verified explicitly — hashing a mutable document would be
meaningless.

---

## Verified: persistence

Against real PostgreSQL. Each integration run provisions a throwaway database
and applies migrations, so "migrations apply cleanly from empty" is verified
continuously rather than assumed.

| Behaviour                                                        | Verified |
| :--------------------------------------------------------------- | :------- |
| All 11 tables created by migration                               | ✅       |
| A versioned contract round-trips with approval metadata and hash | ✅       |
| Supersession persists: 2 versions, 1 approved, 1 superseded      | ✅       |
| Enums stored as readable text (`'Redesign'`, not `2`)            | ✅       |
| `payload_json` is real `jsonb`, queryable with `->>`             | ✅       |

The `jsonb` check is not decorative: the test uses the `->>` operator, which
fails outright on a text column. That is the only way to prove the column type
rather than trust the mapping.

---

## Verified: the database holds what the aggregate cannot

This is the most important result in the spike.

The aggregate **cannot** enforce "at most one approved version" under
concurrency. Two transactions can each load a contract with no approved version,
each approve a different draft, and each commit — both believing they were
first.

A filtered unique index is what actually holds it:

```sql
CREATE UNIQUE INDEX ux_contract_single_approved
  ON app.design_contract_versions (contract_id)
  WHERE status = 'Approved';
```

Verified by bypassing the aggregate entirely and inserting the illegal row
directly:

```csharp
// INSERT a second 'Approved' row via raw SQL
var ex = await Should.ThrowAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(ct));

ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
ex.ConstraintName.ShouldBe("ux_contract_single_approved");
```

Asserting the **constraint name** matters: it proves the specific index is doing
the work, not some incidental uniqueness.

The same pattern protects design systems (`ux_design_system_single_approved`) and
screens (`ux_screen_single_approved`), both confirmed present in the live
database:

```
ux_contract_single_approved       ON app.design_contract_versions (contract_id)
                                  WHERE ((status)::text = 'Approved'::text)
ux_design_system_single_approved  ON app.design_system_versions (design_system_id)
                                  WHERE ((status)::text = 'Approved'::text)
ux_screen_single_approved         ON app.screen_versions (screen_id)
                                  WHERE ((review_state)::text = 'Approved'::text)
```

---

## A real bug found by this spike

`Superseding_a_contract_version_persists_both_versions_with_one_approved`
failed on first run:

```
DbUpdateConcurrencyException: The database operation was expected to affect
1 row(s), but actually affected 0 row(s).
```

SQL logging showed EF emitting an **`UPDATE`** for the newly created version 2
instead of an `INSERT`:

```sql
UPDATE app.design_contract_versions SET status = @p0 WHERE id = @p1;          -- v1 → Superseded, correct
UPDATE app.design_contract_versions SET approved_at = @p2, ... WHERE id = @p10; -- v2, WRONG: should be INSERT
```

**Root cause.** `Entity` assigns a UUID v7 on construction. EF's convention for a
`Guid` key is "generated on add", and EF uses a non-default key value to infer
that an entity **already exists**. A version newly added to an owned collection
was therefore tracked as `Modified`.

**Fix.** Declare the keys as domain-generated:

```csharp
private static void ConfigureDomainGeneratedKeys(ModelBuilder b)
{
    foreach (var entityType in b.Model.GetEntityTypes())
        if (entityType.FindProperty(nameof(Entity.Id)) is { } id)
            id.ValueGenerated = ValueGenerated.Never;
}
```

**Why it is worth recording.** This bug is invisible to unit tests — the
aggregate behaves perfectly in memory. It only appears when a second version is
added to an aggregate loaded from the database, which is exactly the supersession
path. Without an integration test that reloads and supersedes, it would have
shipped and broken the core workflow.

The failing test is now the regression test.

---

## Why owned collections

`DesignContract.Versions` is an EF owned collection, so a version cannot be
loaded or mutated independently of its aggregate. Independent mutation would
bypass the invariants that make approval meaningful — the ability to update a
single `design_contract_versions` row through the ORM is exactly what
`contract.version_immutable` exists to prevent.

---

## Not verified

| Item                                                          | Milestone                                                                  |
| :------------------------------------------------------------ | :------------------------------------------------------------------------- |
| Contract CRUD endpoints                                       | ✅ **Verified in M01-05** — `ContractEndpointsTests` (not yet reviewed)    |
| JSON Schema validation at the API boundary                    | ✅ **Verified in M01-05** — and pinned to Ajv by a 157-case corpus         |
| Referential validation: flow steps referencing listed screens | ✅ **Verified in M01-05** — `422 contract.unknown_screen_reference`        |
| Concurrent approval under real contention                     | Index verified by direct violation; a load test is post-MVP                |
| Payload size limits                                           | M02. Note: `jsonb` has a 255 MB ceiling, far above any realistic contract. |

---

## Tests

`backend/tests/AIDesignStudio.UnitTests/DesignContractTests.cs` — 14 tests
`backend/tests/AIDesignStudio.IntegrationTests/PersistenceTests.cs` — 8 tests
