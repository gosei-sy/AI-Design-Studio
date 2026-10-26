# API Contracts

Base URL in development: `http://localhost:5080`.
OpenAPI document (Development only): `GET /swagger/v1/swagger.json`.

Endpoints marked **(planned)** do not exist yet; their milestone is given. Only
the endpoints under §2 and §3 are implemented in M00.

---

## 1. Conventions

### Versioning

Resource routes are prefixed `/api/v1`. Health endpoints are unversioned
because orchestrators probe them and must not be tied to an API generation.

A breaking change means a new prefix (`/api/v2`), not a mutation of v1. The
payload schemas carry their own `schemaVersion` (currently `1`), pinned by a
test so a bump is deliberate.

### Error format

Failures use RFC 9457 problem details. Domain invariant violations map from
`DomainException.Code`:

```json
{
  "type": "https://aidesignstudio.dev/problems/screen.contract_drift",
  "title": "Domain invariant violated",
  "status": 409,
  "detail": "This screen version was produced against a superseded design contract. Regenerate it against the approved contract before approving.",
  "code": "screen.contract_drift",
  "traceId": "0HN7M2K9PQRST:00000001"
}
```

**Clients must branch on `code`, never on `detail`.** Codes are a contract;
messages are for humans and may change without notice.

| Status    | When                                                                           |
| :-------- | :----------------------------------------------------------------------------- |
| 400       | Payload fails JSON Schema validation                                           |
| 401 / 403 | **(planned, M01)**                                                             |
| 404       | Resource or provider not found                                                 |
| 409       | Domain invariant violated (`DomainException`)                                  |
| 422       | Semantically invalid but well-formed (e.g. flow references an unlisted screen) |
| 429       | **(planned, M02)** rate or quota limit                                         |
| 503       | Dependency unavailable                                                         |

### Domain error codes

The full set currently raised, by module:

| Code                                                                                              | Meaning                                                  |
| :------------------------------------------------------------------------------------------------ | :------------------------------------------------------- |
| `project.organization_required`, `project.name_required`                                          |                                                          |
| `project.invalid_kind`                                                                            | Kind outside `NewProduct` / `Redesign`                   |
| `contract.project_required`, `contract.payload_required`                                          |                                                          |
| `contract.draft_already_open`                                                                     | A draft is already open                                  |
| `contract.no_draft`, `contract.approver_required`                                                 |                                                          |
| `contract.version_immutable`                                                                      | Attempt to edit an approved or superseded version        |
| `contract.not_draft`, `contract.not_approved`                                                     | Illegal state transition                                 |
| `design_system.*`                                                                                 | Same shape as `contract.*`                               |
| `screen.key_required`, `screen.project_required`                                                  |                                                          |
| `screen.contract_version_required`                                                                | Version must record its contract                         |
| `screen.contract_drift`                                                                           | **Produced against a superseded contract**               |
| `screen.not_in_review`, `screen.not_submittable`                                                  |                                                          |
| `screen.rejection_reason_required`, `screen.approver_required`                                    |                                                          |
| `screen.rejector_required`                                                                        | A rejection must record the reviewer                     |
| `screen.version_not_found`                                                                        |                                                          |
| `inventory.key_required`, `inventory.duplicate_key`                                               |                                                          |
| `inventory.covering_screen_required`                                                              | Coverage must reference a real screen                    |
| `inventory.waiver_reason_required`, `inventory.waiver_owner_required`                             |                                                          |
| `inventory.uncovered_features`                                                                    | **Handoff blocked by undecided features**                |
| `asset.organization_required`, `asset.storage_key_required`, `asset.empty`, `asset.hash_required` |                                                          |
| `job.kind_required`, `job.idempotency_key_required`, `job.invalid_max_attempts`                   |                                                          |
| `job.not_running`, `job.lease_not_held`, `job.already_terminal`, `job.not_found`                  |                                                          |
| `job.attempts_remain`                                                                             | Lease expiry cannot dead-letter a job with attempts left |

### Provider error codes

Surfaced as `ProviderException.Code`, taken from the provider where one exists:

| Code                                                                                 | Retryable | Source         |
| :----------------------------------------------------------------------------------- | :-------- | :------------- |
| `MISSING_CANVAS_CREDENTIALS`                                                         | No        | Stitch CLI     |
| `PERMISSION_DENIED`, `INVALID_ARGUMENT`                                              | No        | Stitch CLI     |
| `RESOURCE_EXHAUSTED`, `UNAVAILABLE`                                                  | Yes       | Stitch CLI     |
| `BILLABLE_OPERATIONS_DISABLED`                                                       | No        | Our cost guard |
| `CLI_NOT_FOUND`, `CLI_UNPARSEABLE_OUTPUT`, `CLI_NO_OUTPUT`, `CLI_MALFORMED_RESPONSE` | No        | Our adapter    |
| `CLI_TIMEOUT`                                                                        | Yes       | Our adapter    |

### Shared payload schemas

Normative definitions, used by both the API and the studio UI:

- `packages/contracts/schemas/design-contract.schema.json`
- `packages/contracts/schemas/design-system.schema.json`

TypeScript mirrors live in `packages/contracts/src/index.ts`. Three suites keep
them aligned (164 tests total):

- `contracts.test.ts` validates hand-written fixtures against the schemas.
- `schema-alignment.test.ts` is a **bidirectional structural check**: declared
  key lists are pinned to the TypeScript types at compile time and compared to
  the schemas at runtime. A schema change fails the test run; a TypeScript
  change fails `tsc`.
- `schema-coverage.test.ts` walks **every object node** in both schemas and
  generates a case per rule: each `required` field is omitted and must be
  rejected, each optional field omitted and must be accepted, and each
  `additionalProperties` contract is probed with a conforming and a
  non-conforming value. A newly nested object fails the coverage assertion, so
  coverage cannot silently shrink.

**The alignment is structural, not semantic.** All three suites compare shape:
which properties exist, which are required, and what an `additionalProperties`
map admits. TypeScript cannot express a `pattern`, a `minLength` or
`uniqueItems`, and these types do not try to — `HexColor` is `string`, so
`{ primary: "red" }` compiles and the schema rejects it. The same applies to CSS
lengths and font-family bounds.

The practical consequence: **the schema is the only authority on value format**,
and the API must validate every payload against it rather than trusting that a
typed client sent something well-formed. Closing the gap would mean generating
these types from the schemas, which is deliberately out of M00 scope; nothing in
`packages/contracts/src` is generated today.

### Idempotency

Any endpoint that enqueues billable work accepts an `Idempotency-Key` header.
It becomes `JobRecord.IdempotencyKey`, unique per
`(organization_id, kind, key)`, so a retried submission returns the existing
job rather than creating a second billable call.

Scoped by organization because the key is client-supplied: a global scope would
let one tenant's key return a job belonging to another. Under a true
simultaneous race the loser receives `409` and must re-read; see ADR-0005
amendment A4.

---

## 2. Health

### `GET /health`

Minimal unauthenticated probe.

```json
{ "status": "ok" }
```

### `GET /health/live`

Liveness. **Excludes the database by design** — a database outage must not cause
an orchestrator to restart an otherwise healthy process.

```json
{
  "status": "Healthy",
  "totalDurationMs": 5.0494,
  "checks": [{ "name": "self", "status": "Healthy", "durationMs": 1.063, "description": null }]
}
```

### `GET /health/ready`

Readiness. Includes a real database connection check.

```json
{
  "status": "Healthy",
  "totalDurationMs": 1108.8497,
  "checks": [
    { "name": "database", "status": "Healthy", "durationMs": 963.4438, "description": null }
  ]
}
```

`200` when healthy, `503` otherwise.

The response deliberately omits exception text. Health endpoints are typically
unauthenticated, and exception text leaks connection strings and host names;
`ApiEndpointTests.The_readiness_probe_does_not_leak_connection_details` asserts
this.

---

## 3. Provider diagnostics

### `GET /api/v1/providers/`

Registered adapters and the capabilities each **declares**. A capability absent
from this list is one the adapter does not implement — the UI must gate on it
rather than assume.

```json
[
  {
    "providerId": "google-stitch",
    "capabilities": [
      "IDesignSystemProjectionCapability",
      "IRouteCaptureCapability",
      "IScreenGenerationCapability",
      "IScreenImportCapability",
      "IScreenRetrievalCapability"
    ]
  }
]
```

### `GET /api/v1/providers/{providerId}/readiness`

Probes whether a provider is installed and authenticated. **Never billable**, so
it is safe to call on page load.

```json
{
  "providerId": "google-stitch",
  "isInstalled": true,
  "isAuthenticated": false,
  "isUsable": false,
  "detail": "Not signed in and no canvas credential.",
  "missingCredentialNames": ["google-oauth", "STITCH_API_KEY"]
}
```

`isInstalled` and `isAuthenticated` are reported separately because Stitch needs
two independent credentials and being signed in is **not** sufficient for canvas
operations.

`missingCredentialNames` contains credential **names only**. A value must never
appear here — it would reach the browser.

`404` when `providerId` is not registered. A missing CLI is reported as data
(`isInstalled: false`), not as a server error: an unconfigured provider is an
expected state.

---

## 4. Planned endpoints

Listed so M01 has a target, and marked unimplemented so no reader mistakes this
for a description of the current system.

### Projects (M01)

| Method | Path                    | Notes                                                |
| :----- | :---------------------- | :--------------------------------------------------- |
| `POST` | `/api/v1/projects`      | Body: `{ name, kind }`. `kind` immutable thereafter. |
| `GET`  | `/api/v1/projects`      | Organization-scoped                                  |
| `GET`  | `/api/v1/projects/{id}` |                                                      |

### Design contracts (M01)

| Method   | Path                                                    | Notes                                                  |
| :------- | :------------------------------------------------------ | :----------------------------------------------------- |
| `GET`    | `/api/v1/projects/{id}/contract`                        | Current approved version plus any open draft           |
| `POST`   | `/api/v1/projects/{id}/contract/drafts`                 | 409 `contract.draft_already_open`                      |
| `PUT`    | `/api/v1/projects/{id}/contract/drafts/current`         | 409 `contract.version_immutable` if not a draft        |
| `POST`   | `/api/v1/projects/{id}/contract/drafts/current/approve` | Supersedes the previous approved version; records hash |
| `DELETE` | `/api/v1/projects/{id}/contract/drafts/current`         |                                                        |
| `GET`    | `/api/v1/projects/{id}/contract/versions`               | History                                                |

### Design systems (M01)

Same shape under `/api/v1/projects/{id}/design-system`, plus:

| Method | Path                                                           | Notes                                                                     |
| :----- | :------------------------------------------------------------- | :------------------------------------------------------------------------ |
| `POST` | `/api/v1/projects/{id}/design-system/projections/{providerId}` | Returns `ThemeProjection` with its `warnings[]`. Free — no provider call. |

### Screens (M01–M02)

| Method | Path                                                | Notes                                                                                                                             |
| :----- | :-------------------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------------- |
| `GET`  | `/api/v1/projects/{id}/screens`                     |                                                                                                                                   |
| `POST` | `/api/v1/projects/{id}/screens`                     | Declare a screen against a contract obligation                                                                                    |
| `POST` | `/api/v1/screens/{id}/generate`                     | Enqueues a job; `202 Accepted` + job id. Requires `Idempotency-Key`.                                                              |
| `POST` | `/api/v1/screens/{id}/generate/plan`                | **Free dry-run.** Returns `GenerationPlan`; no provider cost.                                                                     |
| `POST` | `/api/v1/screens/{id}/versions/{versionId}/submit`  |                                                                                                                                   |
| `POST` | `/api/v1/screens/{id}/versions/{versionId}/approve` | 409 `screen.contract_drift`                                                                                                       |
| `POST` | `/api/v1/screens/{id}/versions/{versionId}/reject`  | Body: `{ reason }`. Persists reason, reviewer and timestamp; the reviewer comes from the authenticated principal, never the body. |

### Assets and import (M01–M02)

| Method | Path                            | Notes                                                       |
| :----- | :------------------------------ | :---------------------------------------------------------- |
| `POST` | `/api/v1/projects/{id}/assets`  | Multipart. Server generates the storage key.                |
| `GET`  | `/api/v1/assets/{id}/content`   | Streams bytes                                               |
| `POST` | `/api/v1/projects/{id}/imports` | Normalizes an asset. Response **always** states `fidelity`. |

The import response carries `fidelity` as a first-class field precisely so a
screenshot (`Visual`) can never be mistaken for a structural import
(ADR-0006).

### Redesign inventory (M02)

| Method | Path                                                   | Notes                                           |
| :----- | :----------------------------------------------------- | :---------------------------------------------- |
| `GET`  | `/api/v1/projects/{id}/inventory`                      | Includes `blockingItems[]`                      |
| `POST` | `/api/v1/projects/{id}/inventory/items`                |                                                 |
| `POST` | `/api/v1/projects/{id}/inventory/items/{itemId}/cover` | Body: `{ screenIds[] }`                         |
| `POST` | `/api/v1/projects/{id}/inventory/items/{itemId}/waive` | Body: `{ reason }`; owner from identity         |
| `GET`  | `/api/v1/projects/{id}/handoff/readiness`              | 409 `inventory.uncovered_features` when blocked |

### Jobs (M01)

| Method | Path                       | Notes                       |
| :----- | :------------------------- | :-------------------------- |
| `GET`  | `/api/v1/jobs/{id}`        | State, attempt, `lastError` |
| `POST` | `/api/v1/jobs/{id}/cancel` | 409 `job.already_terminal`  |

Both must use an **organization-scoped** query. `IJobQueue.GetAsync` is
deliberately unfiltered for worker-internal use, and reusing it at the HTTP
boundary would be a cross-tenant read.

---

## 5. Stability

| Surface                                    | Stability                                                                 |
| :----------------------------------------- | :------------------------------------------------------------------------ |
| `/health`, `/health/live`, `/health/ready` | **Stable.** Probed by orchestrators.                                      |
| `/api/v1/providers/*`                      | **Stable** shape; the capability list grows as capabilities are verified. |
| `DomainException` codes                    | **Stable.** Clients branch on them. Renaming one is a breaking change.    |
| Problem-details `detail` text              | **Unstable.** Never parse it.                                             |
| `schemaVersion` in payloads                | Pinned; a bump is a breaking change gated by a test.                      |
| Everything in §4                           | **Unimplemented.** Subject to change until built.                         |
