# API Contracts

Base URL in development: **`https://localhost:7080`** (the `https` launch
profile, which is the default).

HTTPS is required, not preferred: the session and antiforgery cookies are
`Secure`, so the API refuses to emit them over plain HTTP. The previously
documented `http://localhost:5080` made every authenticated call fail with a 500
(M01-W2-006). The HTTP endpoint still exists for health probes behind a
TLS-terminating proxy.
OpenAPI document (Development only, **and requires an authenticated session**): `GET /swagger/v1/swagger.json`. It was anonymous until M01-W2-009; the API contract is not public.

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

| Status | When                                                                                                |
| :----- | :-------------------------------------------------------------------------------------------------- |
| 400    | Payload fails JSON Schema validation                                                                |
| 401    | No session. Every non-health endpoint, by the fallback policy (M01-02)                              |
| 403    | Signed in, but no active membership (`authz.no_organization_membership`) or a missing role (M01-03) |
| 404    | Resource or provider not found                                                                      |
| 409    | Domain invariant violated (`DomainException`)                                                       |
| 413    | A design contract larger than 1 MiB — `contract.payload_too_large`                                  |
| 415    | A design contract not sent as `application/json` — `request.unsupported_media_type`                 |
| 422    | Semantically invalid but well-formed (e.g. flow references an unlisted screen)                      |
| 429    | **(planned, M02)** rate or quota limit                                                              |
| 503    | Dependency unavailable                                                                              |

### Domain error codes

The full set currently raised, by module:

| Code                                                                                              | Meaning                                                   |
| :------------------------------------------------------------------------------------------------ | :-------------------------------------------------------- |
| `project.organization_required`, `project.name_required`                                          |                                                           |
| `organization.name_required`, `organization.name_too_long`                                        |                                                           |
| `membership.organization_required`, `membership.user_required`                                    |                                                           |
| `membership.role_required`                                                                        | No roles, or an attempt to remove the last one            |
| `membership.invalid_role`                                                                         | A role this application does not define                   |
| `membership.provisioner_required`, `membership.revoker_required`                                  | An unattributed grant or withdrawal of access             |
| `membership.revoked`, `membership.already_revoked`                                                | Illegal state transition                                  |
| `authz.approver_required`                                                                         | Only an Approver may approve, reject or waive             |
| `authz.no_organization_membership`                                                                | Authenticated, but provisioned by nobody                  |
| `organization.not_found`                                                                          | Includes any organization that is not the caller's own    |
| `project.invalid_kind`                                                                            | Kind outside `NewProduct` / `Redesign`                    |
| `project.name_too_long`                                                                           | More than 200 characters after trimming (M01-04)          |
| `project.not_found`                                                                               | Includes any project that is not the caller's own         |
| `authz.member_required`                                                                           | Authoring needs Member; the caller is Approver-only       |
| `contract.project_required`, `contract.payload_required`                                          |                                                           |
| `contract.draft_already_open`                                                                     | A draft is already open                                   |
| `contract.no_draft`, `contract.approver_required`                                                 |                                                           |
| `contract.version_immutable`                                                                      | Attempt to edit an approved or superseded version         |
| `contract.not_draft`, `contract.not_approved`                                                     | Illegal state transition                                  |
| `contract.payload_invalid`, `contract.unknown_screen_reference`, `contract.payload_too_large`     | A draft body refused before the domain (§3c); 400/422/413 |
| `authz.approver_required`                                                                         | Also returned by the Approver policy, as a coded 403      |
| `design_system.*`                                                                                 | Same shape as `contract.*`                                |
| `screen.key_required`, `screen.project_required`                                                  |                                                           |
| `screen.contract_version_required`                                                                | Version must record its contract                          |
| `screen.contract_drift`                                                                           | **Produced against a superseded contract**                |
| `screen.not_in_review`, `screen.not_submittable`                                                  |                                                           |
| `screen.rejection_reason_required`, `screen.approver_required`                                    |                                                           |
| `screen.rejector_required`                                                                        | A rejection must record the reviewer                      |
| `screen.version_not_found`                                                                        |                                                           |
| `inventory.key_required`, `inventory.duplicate_key`                                               |                                                           |
| `inventory.covering_screen_required`                                                              | Coverage must reference a real screen                     |
| `inventory.waiver_reason_required`, `inventory.waiver_owner_required`                             |                                                           |
| `inventory.uncovered_features`                                                                    | **Handoff blocked by undecided features**                 |
| `asset.organization_required`, `asset.storage_key_required`, `asset.empty`, `asset.hash_required` |                                                           |
| `job.kind_required`, `job.idempotency_key_required`, `job.invalid_max_attempts`                   |                                                           |
| `job.not_running`, `job.lease_not_held`, `job.already_terminal`, `job.not_found`                  |                                                           |
| `job.attempts_remain`                                                                             | Lease expiry cannot dead-letter a job with attempts left  |

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
them aligned, and a fourth keeps the API's validator aligned with the studio's
(169 tests total):

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
- `conformance.test.ts` guards `conformance/design-contract.cases.json`: 157
  documents with the verdict **Ajv** gives each, checked in. It fails if the file
  is not exactly what Ajv produces for the current schema, or if any validation
  keyword in the schema never fails in it. The API's .NET validator is tested
  against the same file (`SchemaConformanceTests`), which is what makes "the API
  and the studio enforce one definition" a tested statement (ADR-0010).

**The alignment is structural, not semantic.** All three suites compare shape:
which properties exist, which are required, and what an `additionalProperties`
map admits. TypeScript cannot express a `pattern`, a `minLength` or
`uniqueItems`, and these types do not try to — `HexColor` is `string`, so
`{ primary: "red" }` compiles and the schema rejects it. The same applies to CSS
lengths and font-family bounds.

The practical consequence: **the schema is the only authority on value format**,
and the API must validate every payload against it rather than trusting that a
typed client sent something well-formed. **Since M01-05 it does, for the design
contract** (§3c); the design system follows in M01-06. Closing the gap would mean generating
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

## 3a. Organizations — implemented (M01-03)

Organization-scoped authorization. Every response here is computed from
**server-side membership state**; no header, query-string parameter, request-body
field or role claim is read (ADR-0009).

### `GET /api/v1/organizations/current`

The caller's organization and their roles within it.

```json
{
  "organizationId": "01932f...",
  "name": "Acme Design",
  "roles": ["Member", "Approver"]
}
```

| Status | When                                                                         |
| :----- | :--------------------------------------------------------------------------- |
| `200`  | The caller has an active membership                                          |
| `401`  | No session                                                                   |
| `403`  | Authenticated, but no active membership — `authz.no_organization_membership` |

A `403` here is the normal state of a self-registered Development account.
Registering grants nothing; access is granted by an operator (D-9).

### `GET /api/v1/organizations/{organizationId}`

Same body as above, for one organization by id.

| Status | When                                                                            |
| :----- | :------------------------------------------------------------------------------ |
| `200`  | `organizationId` is the caller's own                                            |
| `401`  | No session                                                                      |
| `403`  | No active membership                                                            |
| `404`  | **Any other organization**, whether or not it exists — `organization.not_found` |

**`404`, not `403`, for another tenant's organization.** A `403` would confirm
the organization exists and that the caller merely is not in it, which makes the
endpoint a tenant-existence oracle — the same class of leak as the
account-enumeration oracles corrected in W2-002. The response is byte-identical
for an organization that exists and one that does not, which is asserted by
`AuthorizationTests.An_existing_other_organization_and_a_nonexistent_one_look_the_same`.

### `GET /api/v1/organizations/current/members`

Active memberships of the caller's own organization. **Approver only.**

```json
{
  "organizationId": "01932f...",
  "members": [
    {
      "userId": "01932f...",
      "roles": ["Member", "Approver"],
      "provisionedBy": "sy.vo@example.test via aids-provision@host",
      "provisionedAt": "2026-11-03T04:19:45.000Z"
    }
  ]
}
```

| Status | When                                              |
| :----- | :------------------------------------------------ |
| `200`  | The caller holds `Approver`                       |
| `401`  | No session                                        |
| `403`  | No membership, or a membership without `Approver` |

Ids and roles, **not email addresses**. An address is self-asserted (W2-005), so
it would be misleading as an identity, and an approver does not need it to know
who holds approval authority.

### Not an endpoint: granting access

There is no HTTP route that creates an organization, a membership or a role, in
any environment. Provisioning is `aids-provision`, a console tool (ADR-0009), and
`AuthorizationTests.No_http_endpoint_can_grant_a_membership` enumerates the real
route table to keep it that way.

`POST /api/v1/auth/register` is **mapped in Development only**. Outside
Development the route does not exist: an anonymous call gets `401` from the
fallback policy, and an authenticated one gets `404` from the router.

---

## 3b. Projects — implemented (M01-04)

**Implemented on 2026-11-19 under explicit authorization, before M01-03's
re-acceptance** — it builds on M01-03's authorization model, so a change there
can require a change here. Not yet independently reviewed.

The organization is always the caller's membership. No request field names one,
and one supplied anyway is ignored
(`ProjectEndpointsTests.A_created_project_belongs_to_the_callers_organization_whatever_the_body_says`).
Reads have no organization predicate: the global query filter on `projects`
supplies it once the request's scope is opened.

**Roles.** Reading needs any active membership. Creating needs **Member** —
`OrganizationRoles.Member` is "may read and author", and an Approver-only
membership is legal. That reading is assumption **A-16**, open for the Product
Owner.

### `POST /api/v1/projects`

Requires an antiforgery token (`X-CSRF-TOKEN`).

```json
{ "name": "Storefront", "kind": "NewProduct" }
```

`kind` is `NewProduct` or `Redesign`, **by exact name** — not a number and not
another casing, so no client depends on the enum's declaration order. It cannot
be changed afterwards: there is no update endpoint, and
`ProjectEndpointsTests.Only_project_creation_binds_a_kind` walks the route table
so no later endpoint under `/api/v1/projects` can quietly accept one. `name` is
trimmed and at most 200 characters.

`201 Created`, `Location: /api/v1/projects/{id}`:

```json
{
  "id": "019a8c...",
  "name": "Storefront",
  "kind": "NewProduct",
  "createdAt": "2026-11-19T10:00:00+00:00",
  "updatedAt": null
}
```

| Status | When                                                                                       |
| :----- | :----------------------------------------------------------------------------------------- |
| `201`  | Created in the caller's organization                                                       |
| `400`  | `request.invalid` — `errors` names `Name` or `Kind`; `auth.antiforgery_failed`; not JSON   |
| `401`  | No session                                                                                 |
| `403`  | `authz.no_organization_membership`; or `authz.member_required` for an Approver-only member |

### `GET /api/v1/projects`

The caller's organization's projects, **newest first**, as `{ "projects": [ … ] }`
— an object rather than a bare array, so pagination can be added without a
breaking change. Each item has the shape above. `200`, `401`, or `403`
(`authz.no_organization_membership`).

### `GET /api/v1/projects/{id}`

One project. `200`, `401`, `403` (`authz.no_organization_membership`), or **`404`
`project.not_found` — for another organization's project exactly as for an id
that never existed** (same status, code and detail), so the endpoint cannot be
used to learn that someone else's project exists.

## 3c. Design contract — implemented (M01-05)

**Implemented on 2026-11-19 under delegated authorization, stacked on M01-04 and
therefore ahead of both M01-03's and M01-04's acceptance.** Not independently
reviewed. The validation mechanism is **ADR-0010 (proposed)**.

A project has **one** contract. The resource is singular and always addressable:
before the first draft it reads as empty rather than missing. Every route is
under `/api/v1/projects/{projectId}/contract`, so tenancy is the project's —
another organization's contract is the same `404 project.not_found` as a project
that does not exist, on **every** route
(`ContractEndpointsTests.Another_organizations_contract_is_indistinguishable_from_a_missing_project`).

| Method   | Path suffix               | Role         | Success                |
| :------- | :------------------------ | :----------- | :--------------------- |
| `GET`    | _(none)_                  | any member   | `200` approved + draft |
| `GET`    | `/versions`               | any member   | `200` history          |
| `POST`   | `/drafts`                 | **Member**   | `201` + `Location`     |
| `PUT`    | `/drafts/current`         | **Member**   | `200` the draft        |
| `DELETE` | `/drafts/current`         | **Member**   | `204`                  |
| `POST`   | `/drafts/current/approve` | **Approver** | `200` the approved one |

Roles are **policies on the routes**, and
`ContractEndpointsTests.Every_route_that_changes_something_requires_an_organization_role`
walks the whole route table so no write route can be added without one. A policy
refusal is a coded 403 — `authz.no_organization_membership`,
`authz.member_required` or `authz.approver_required`. The four write routes
require an antiforgery token.

### A version

```json
{
  "id": "019a8c...",
  "versionNumber": 2,
  "status": "Approved",
  "payload": { "schemaVersion": 1, "product": { "name": "Acme", "platforms": ["web"] } },
  "payloadHash": "9f2c… (64 hex characters)",
  "approvedBy": "019a8b… (the approver's user id)",
  "approvedAt": "2026-11-19T10:00:00+00:00",
  "createdAt": "2026-11-19T09:58:00+00:00"
}
```

`status` is `Draft`, `Approved` or `Superseded`. `payloadHash`, `approvedBy` and
`approvedAt` are null on a draft. **`approvedBy` is the approver's immutable user
id**, taken from the session — never from the request, and never an email
address (W2-005).

- `GET …/contract` → `{ "projectId", "approved": <version or null>, "draft": <version or null> }`
- `GET …/contract/versions` → `{ "projectId", "versions": [ … ] }`, newest first,
  **each with its payload**, so a superseded version stays readable (S-07).

### The body of a draft

`POST …/drafts` and `PUT …/drafts/current` take **the contract document itself**
as `application/json` — not a wrapper around it. It is checked before anything
is stored, and the first check that finds something ends the run:

| Refusal                                       | Status | `code`                              |
| :-------------------------------------------- | :----- | :---------------------------------- |
| Not declared as JSON                          | `415`  | `request.unsupported_media_type`    |
| Larger than 1 MiB (provisional; M02)          | `413`  | `contract.payload_too_large`        |
| Not one JSON document                         | `400`  | `contract.payload_invalid`          |
| Cannot be stored as what it says              | `400`  | `contract.payload_invalid`          |
| Breaks `design-contract.schema.json` (AC-2.6) | `400`  | `contract.payload_invalid`          |
| A flow step names an unlisted screen (AC-2.7) | `422`  | `contract.unknown_screen_reference` |

The 400 and 422 carry the failures:

```json
{
  "code": "contract.payload_invalid",
  "errors": [
    {
      "pointer": "/product/name",
      "keyword": "minLength",
      "message": "must NOT have fewer than 1 characters"
    },
    {
      "pointer": "",
      "keyword": "additionalProperties",
      "message": "must NOT have additional property 'owner'"
    }
  ],
  "truncated": false
}
```

`pointer` is an RFC 6901 JSON Pointer to the instance that failed and `keyword`
is the schema rule — **the same pair the studio's validator reports**, so a
client can treat a local refusal and an API one alike. For `required` and
`additionalProperties` the pointer is the object missing or carrying the
property, as Ajv has it. Branch on `keyword`, never on `message`. At most 100
errors are returned; `truncated` says when there were more.

**"Cannot be stored as what it says"** is the API being stricter than the schema
on purpose (ADR-0010). PostgreSQL `jsonb` keeps one member per name, refuses
U+0000 and unpaired surrogates, and holds numbers as `numeric`; a duplicated
property (`duplicateProperty`), such text (`encoding`) or a number that cannot be
held exactly (`number`) is refused by pointer rather than becoming a 500 or
being stored as a different value from the one validated.

### What "stored" means

The contract is held as `jsonb`. **It comes back as the same JSON value, not the
same bytes**: keys reordered, whitespace gone, `1.0` as `1`. The schema's
description used to say the payload is stored "verbatim"; it is not, and now says
so. Consequently:

- `payloadHash` is the SHA-256 of **the stored text** — PostgreSQL's rendering of
  the `jsonb` value. It identifies a version's content and is what a screen
  records to detect contract drift. It is **not** recomputable from the `payload`
  in a response, which is the same value re-serialized.
- `ContractEndpointsTests` audits this in SQL —
  `sha256(payload_json::text) = payload_hash` for every non-draft version — so
  the check does not share code with the thing it checks.

### Transitions and refusals

| Request                                     | Result                                               |
| :------------------------------------------ | :--------------------------------------------------- |
| Open a draft while one is open              | `409 contract.draft_already_open`                    |
| Edit, with an approved version and no draft | `409 contract.version_immutable` — nothing changes   |
| Edit, discard or approve with no draft      | `409 contract.no_draft` — and no row is created      |
| Approve                                     | Draft → `Approved`; previous approved → `Superseded` |
| Discard                                     | The draft is deleted; its number is reused           |

**Writes to one project's contract are serialized** by a row lock on the project
(`ProjectWriteLock`). Without it, an approval and an edit of the same draft
arriving together both succeed — they write different columns — and leave an
_approved_ version whose payload is not the one that was hashed. No constraint
catches that.
`An_edit_racing_an_approval_cannot_leave_an_approved_version_that_disagrees_with_its_hash`
forces the interleaving and asserts the outcome.

### Not done, deliberately

- **Duplicate screen keys and duplicate flow keys are accepted.** They are
  schema-valid, and whether to refuse them is a product rule nobody has stated.
  The conformance corpus pins the current verdict.
- **Two people editing one draft are not told about each other.** The lock makes
  the two edits happen in order; the later one wins. Detecting that needs a
  version token in the request — a contract change for M01-10, when there is an
  editor to send it.
- **No endpoint for a single historical version.** History carries the payloads.

---

## 4. Planned endpoints

Listed so M01 has a target, and marked unimplemented so no reader mistakes this
for a description of the current system.

### Projects (M01)

**Implemented** — see §3b.

### Design contracts (M01)

**Implemented** — see §3c.

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

Both must use an **organization-scoped** query. `IJobQueue.GetAsync` reads
through `db.Jobs`, which carries the organization query filter since W3-001, so
in the API host it returns only the caller's organization's jobs — **once
`IOrganizationContext` has opened the request's scope**; before that it returns
nothing, because the scope starts closed. In the worker the same call is
system-scoped and sees every tenant. The claim is raw SQL and unfiltered, and is
protected by running only in the worker. (This note used to say `GetAsync` was
unfiltered; that stopped being true when W3-001 filtered all seven roots.)

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
