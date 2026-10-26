# System Architecture

**Milestone:** M00 (foundation). **Status:** awaiting architecture acceptance.

This document describes what exists in the repository today. Items not yet
built are marked **(planned)** with their milestone.

---

## 1. Shape

A modular monolith (ADR-0001). Two host processes over one codebase and one
database.

```
┌──────────────────┐        ┌───────────────────────────────────────┐
│  Next.js studio  │  HTTP  │  AIDesignStudio.Api                   │
│  (frontend/)     │ ─────► │  minimal APIs, health, problem details│
└──────────────────┘        └──────────────────┬────────────────────┘
                                               │
                            ┌──────────────────▼────────────────────┐
                            │  Application  (ports, capabilities)   │
                            │  Domain       (7 modules, invariants) │
                            │  SharedKernel (Entity, IClock)        │
                            └──────────────────┬────────────────────┘
                                               │
                            ┌──────────────────▼────────────────────┐
                            │  Infrastructure                       │
                            │  EF Core · storage · provider adapters│
                            └────┬──────────────┬───────────────┬───┘
                                 │              │               │
                        ┌────────▼───┐  ┌───────▼──────┐  ┌─────▼──────────┐
                        │ PostgreSQL │  │ Blob storage │  │ @google/stitch │
                        │ app schema │  │ local / Azure│  │ CLI subprocess │
                        │ + job queue│  └──────────────┘  └────────────────┘
                        └────────▲───┘
                                 │  claim with FOR UPDATE SKIP LOCKED
                   ┌─────────────┴──────────────┐
                   │  AIDesignStudio.Worker     │
                   │  JobDispatcher, lease/retry│
                   └────────────────────────────┘
```

The API and Worker are separate processes for operational reasons only: provider
calls take minutes and must not hold request threads, and the two scale on
different signals. They are not separate services — same schema, same migration
history, same transaction boundary.

---

## 2. Layers

Dependencies point inward. Enforced by `AIDesignStudio.ArchitectureTests`, not
by convention.

| Layer          | Project                         | May depend on  | Contains                                                                                                                          |
| :------------- | :------------------------------ | :------------- | :-------------------------------------------------------------------------------------------------------------------------------- |
| Shared kernel  | `AIDesignStudio.SharedKernel`   | nothing        | `Entity`, `AggregateRoot`, `IClock`, `DomainException`                                                                            |
| Domain         | `AIDesignStudio.Domain`         | SharedKernel   | Aggregates and invariants. No EF, no ASP.NET, no provider names.                                                                  |
| Application    | `AIDesignStudio.Application`    | Domain         | Ports (`IAssetStorage`, `IJobQueue`, `IDesignProvider`, `IDesignArtifactParser`), capability contracts, normalized artifact model |
| Infrastructure | `AIDesignStudio.Infrastructure` | Application    | EF Core, storage adapters, provider adapters, artifact parsers                                                                    |
| Hosts          | `.Api`, `.Worker`               | Infrastructure | Composition, HTTP surface, job dispatch                                                                                           |

### Enforced rules

Each is a test in `ModuleBoundaryTests`:

- `SharedKernel` references nothing else in the solution.
- `Domain` does not reference Application, Infrastructure or hosts.
- `Application` does not reference Infrastructure or hosts.
- `Domain` has no dependency on EF Core, Npgsql, Dapper or `System.Data`.
- `Domain` and `Application` have no dependency on ASP.NET Core, Azure SDKs.
- **No provider name** (`Stitch`, `Gemini`, `Figma`) appears as a type name in
  SharedKernel, Domain or Application. This holds ADR-0002 in place
  mechanically.
- Every `IDesignProvider` implementation lives under
  `AIDesignStudio.Infrastructure.Providers`.
- Capability contracts are declared in Application, never beside an adapter.
- Aggregate roots are `sealed` — an inheritable aggregate can have its invariant
  checks overridden.
- No domain entity exposes a public setter. Every state change goes through a
  method that can enforce the relevant invariant.

---

## 3. Domain modules

Seven modules. Cross-module references are **by aggregate id only** — asserted
by `Domain_modules_do_not_reference_each_others_types`. That is also the
precondition for extracting a module later without a rewrite.

| Module            | Aggregate root     | Responsibility                                                                  |
| :---------------- | :----------------- | :------------------------------------------------------------------------------ |
| `Projects`        | `DesignProject`    | Tenancy and workflow kind (`NewProduct` / `Redesign`, immutable after creation) |
| `DesignContracts` | `DesignContract`   | The source of truth. Versioning, approval, payload hashing                      |
| `DesignSystems`   | `DesignSystem`     | Approved token set, versioned on the same model                                 |
| `Screens`         | `Screen`           | Screen version history, provenance, review workflow, contract-drift guard       |
| `Redesign`        | `FeatureInventory` | Existing-capability catalogue and the no-functional-loss handoff gate           |
| `Assets`          | `AssetReference`   | Blob metadata, integrity hashes, opaque storage keys                            |
| `Jobs`            | `JobRecord`        | Durable job state machine: lease, retry, backoff, terminal states               |

### Dependency boundaries in practice

**Cardinality.** A project has **at most one** design contract, one design
system and one feature inventory, enforced by unique indexes on `project_id`.
The singular API (`/projects/{id}/contract`) and the "source of truth" model
both require it: two contracts per project would make "which one is in force?"
unanswerable, and the per-aggregate approval index cannot express it.

`Screen` holds a `ContractVersionId` and `ContractPayloadHash`, not a reference
to `DesignContractVersion`. The caller passes the contract currently in force
into `Screen.ApproveVersion(...)`, which compares and throws
`screen.contract_drift` on mismatch. The invariant spans two modules without
either module depending on the other's types.

---

## 4. Design approval workflow

Both the Design Contract and the Design System use the same three-state model,
for the same reason: an approval is only meaningful if what was approved cannot
subsequently change.

```
          StartDraft                ApproveDraft
   (none) ───────────► Draft ──────────────────────► Approved
                         │                              │
                         │ DiscardDraft                 │ next ApproveDraft
                         ▼                              ▼
                      (removed)                     Superseded
```

Rules:

- Version numbers are dense and monotonic from 1.
- **Only a `Draft` is mutable.** `UpdatePayload` on an `Approved` or
  `Superseded` version throws `contract.version_immutable`.
- **At most one `Draft`** at a time, so reviewers always have a single candidate.
- **At most one `Approved`** at a time — enforced in the aggregate _and_ by the
  filtered unique index `ux_contract_single_approved`. The aggregate cannot hold
  this under concurrency: two transactions can each read a state with no
  approved version. The index is what actually holds it.
- Approval records approver identity, timestamp, and a SHA-256 of the payload.

### Screen review

```
   AddVersion          SubmitForReview         ApproveVersion
       ──► Draft ──────────────► InReview ─────────────────► Approved
              ▲                      │                           │
              └──────── Reject ──────┘                           ▼
                     (reason required)                      Superseded
```

`ApproveVersion` is where provider output gets held to the contract. It requires
both the `ContractVersionId` and the `ContractPayloadHash` recorded at
production time to match the contract currently in force. Matching on id alone
would let edited contract content through under the same id.

---

## 5. Background job lifecycle

See ADR-0005. States:

```
                  ┌──── Fail(retryable, attempts remain) ────┐
                  │            backoff via run_after          │
                  ▼                                           │
  Enqueue ──► Pending ──── TryClaim ────► Running ────────────┘
                  │       (takes lease)      │
                  │                          ├─ Succeed ──► Succeeded
                  │                          ├─ Fail(not retryable
                  │                          │    or attempts spent) ──► Dead
                  │                          └─ lease expires ──► claimable again
                  │
                  └─ Cancel ──► Cancelled
```

- **Claim is a lease.** A crashed worker's job becomes claimable once
  `lease_expires_at` passes. No broker, no operator action.
- **Every worker write is fenced by lease ownership, at the database.**
  `Succeed`, `Fail` and `Heartbeat` are single conditional `UPDATE`s whose
  `WHERE` clause re-checks the state, the owner, and expiry resolved with
  `clock_timestamp()` _while the statement executes_. The domain raises
  `job.lease_not_held` as a fast path, but the database is the authority:
  validating in the application and then saving by primary key leaves a
  time-of-check/time-of-use gap (ADR-0005 amendments A5, A6). Without this, a worker whose lease lapsed
  mid-execution could report a result for an attempt another worker now owns —
  and for billable work, hide that the provider was called twice.
- **Reclaiming counts as a new attempt, and the budget is enforced on claim.** A
  job whose attempts are spent is not claimable; the claim sweep dead-letters a
  row whose lease lapsed with nothing left (`FailExhaustedLease`). That sweep is
  the only place it can happen, because a dead process never calls `Fail`. So a
  job that reliably kills its worker reaches `Dead` rather than looping.
- **The dispatcher renews the lease while a handler runs, and fails closed.**
  Losing the lease _or_ being unable to prove it still holds — a heartbeat that
  throws — cancels the handler, and nothing is recorded once ownership is
  uncertain (ADR-0005 amendment A7). `LeaseDuration`
  therefore bounds how long a _crashed_ worker's job stays unavailable, not how
  long a handler may take. `BatchSize` defaults to **1**: jobs in a batch run
  sequentially, so a larger batch leases work that may expire before it starts.
- **Idempotency key** is unique per `(organization_id, kind, key)`, so a
  duplicate submission cannot produce a second billable provider call. Scoped by
  organization because keys are client-supplied — a global scope would let one
  tenant's key return another tenant's job.
- **Backoff:** 2s, 4s, 8s … capped at 5 minutes.
- **Handlers must be idempotent.** A lease can expire mid-flight, so the same
  payload can legitimately be delivered twice.

M00 ships the dispatcher and its mechanics with **no handlers registered**. The
mechanism is what needed proving; a handler without a verified provider call
would be untested code. An unknown job kind fails terminally
(`retryable: false`) so it surfaces rather than looping.

---

## 6. AI provider abstraction

See ADR-0003. Capabilities are discovered, never assumed.

```csharp
var generation = provider.GetCapability<IScreenGenerationCapability>();
if (generation is null) { /* this provider cannot generate screens */ }
```

**Registered adapter:** `StitchDesignProvider` (`google-stitch`), driving the
`@google/stitch` CLI as a subprocess.

| Capability                          | Declared | Why                                                                                                         |
| :---------------------------------- | :------- | :---------------------------------------------------------------------------------------------------------- |
| `IScreenGenerationCapability`       | Yes      | Request shaping verified via `--dry-run`                                                                    |
| `IScreenImportCapability`           | Yes      | `upload screen` accepts HTML or image                                                                       |
| `IScreenRetrievalCapability`        | Yes      | `get screen --json`                                                                                         |
| `IDesignSystemProjectionCapability` | Yes      | Projection verified against the provider's own schema                                                       |
| `IRouteCaptureCapability`           | Yes      | `capture <url>`                                                                                             |
| `IScreenVariantCapability`          | **No**   | CLI supports it, but no verified response mapping without credentials. Claiming it would be a lie. → M01-07 |

Three behaviours of the CLI shape the adapter, all verified in SPIKE-01:

1. **It exits 0 even on failure.** Success is read from the JSON envelope's
   `success` field. Branching on exit code would treat every auth failure as a
   success.
2. **Errors are structured**, with a stable `error.code`. Retry policy keys off
   that code, not message text.
3. **`--dry-run` works with no credentials and no network**, so request shaping
   is testable in CI at zero cost.

Credentials are **never passed as process arguments** (`/proc` is
world-readable); they are inherited from the environment only. Log output
redacts `--api-key` and `--access-token`.

### Cost control

`Providers:Stitch:BillableOperationsEnabled` defaults to `false`. Every billable
method calls `EnsureBillableAllowed` and throws
`BILLABLE_OPERATIONS_DISABLED` while it is off. Readiness probes and dry-run
planning remain available, so the system is fully developable and testable
without spending anything.

---

## 7. Asset management

`IAssetStorage` is the port. `LocalFileSystemAssetStorage` is the development
implementation; the Azure Blob adapter replaces the registration per environment
with no change above Infrastructure. **(Azure adapter: planned, M02)**

Rules:

- **Storage keys are server-generated, never client-supplied.** A client-supplied
  key is a path-traversal and cross-tenant read primitive.
  `IAssetStorage.BuildKey` produces
  `org/{orgId:N}/project/{projectId:N}/asset/{assetId:N}.{ext}`.
- **Organization id leads the key**, so a mis-scoped query cannot silently cross
  tenants.
- Keys are validated _and_ the resolved absolute path is re-checked against the
  configured root — defence in depth, so the guard survives a weakened
  validator.
- Bytes are hashed (SHA-256) while streaming, so the stream is read once and
  large or non-seekable uploads work.
- The database stores metadata only: key, content type, size, hash, original
  filename.

---

## 8. Authorization model

**(Planned, M01.)** Nothing is implemented in M00; no endpoint is authenticated.
This is a deliberate M00 scope boundary, stated plainly rather than implied.

The data model is built for it:

- `DesignProject.OrganizationId`, `AssetReference.OrganizationId` and
  `JobRecord.OrganizationId` exist and are indexed.
- `AssetReference.StorageKey` is organization-prefixed.
- Job idempotency is organization-scoped, so a key cannot cross a tenant
  boundary.

**M01 must also scope job queries.** `IJobQueue.GetAsync` is deliberately
unfiltered for worker-internal use; the API-facing job status and cancel
endpoints need their own organization-scoped query. Flagged here because reusing
the worker's lookup at the HTTP boundary would be a cross-tenant read.

Intended model:

| Role        | Can                                                                         |
| :---------- | :-------------------------------------------------------------------------- |
| Viewer      | Read projects, contracts, design systems, screens                           |
| Contributor | Author drafts, request generation, upload assets, submit for review         |
| Reviewer    | Approve or reject screen versions                                           |
| Owner       | Approve contract and design system versions, waive features, manage members |

Approval is intentionally separated from authoring: `ApprovedBy` / `WaivedBy`
are non-null and audited precisely so accountability is attributable to a real
identity.

---

## 9. Security and trust boundaries

| Boundary                | Trust                                | Controls                                                                                                   |
| :---------------------- | :----------------------------------- | :--------------------------------------------------------------------------------------------------------- |
| Browser → API           | Untrusted                            | Validate against the shared JSON Schemas; CORS allow-list; RFC 9457 problem details; auth **(M01)**        |
| API → PostgreSQL        | Trusted network, untrusted input     | Parameterised queries only (EF Core / `FromSql` interpolation); least-privilege role                       |
| API/Worker → Stitch CLI | **Untrusted output, trusted binary** | Parse the JSON envelope; never `eval`; timeout and kill the process tree; credentials via environment only |
| Worker → blob storage   | Trusted                              | Server-generated keys; path confinement re-checked after resolution                                        |
| Uploads → parsers       | **Fully untrusted**                  | Parsers must never throw on malformed input; degrade fidelity and warn instead                             |
| Health endpoints        | Unauthenticated                      | Report check names and statuses only — never exception text, which leaks connection strings and host names |

Detail in [../04-engineering/SECURITY_REQUIREMENTS.md](../04-engineering/SECURITY_REQUIREMENTS.md).

---

## 10. Cross-cutting conventions

- **Time comes from `IClock`.** Never `DateTimeOffset.UtcNow` in domain or
  application code — lease expiry and retry backoff must be testable in
  milliseconds, not by sleeping.
- **Ids are UUID v7**, generated in the domain. `AppDbContext` declares them
  `ValueGenerated.Never`; this is correctness, not tuning (see DATA_MODEL.md §6).
- **Enums persist as text**, so data stays legible in SQL and reordering an enum
  cannot change stored meaning.
- **Errors carry stable codes.** `DomainException.Code` (e.g.
  `screen.contract_drift`) becomes the problem-details `type`, so clients branch
  on a contract rather than on prose.
- **Warnings are not errors.** `ProjectionWarning` and artifact warnings are
  normal output, surfaced to users rather than logged and forgotten.

---

## 11. What M00 does not include

Stated explicitly so no reader infers more than exists:

- No authentication or authorization (M01).
- No job handlers — the dispatcher runs with none registered (M01).
- No Azure Blob Storage adapter (M02).
- No verified live Stitch calls. Generation, variants, import and retrieval are
  **unverified** against a real account; see
  [../06-spikes/README.md](../06-spikes/README.md).
- No product UI. The single page reports environment status only.
- No rate limiting, quota accounting or spend tracking (M02).
