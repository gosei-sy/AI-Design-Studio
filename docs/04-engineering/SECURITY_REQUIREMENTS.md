# Security Requirements

What is implemented and verified, what is deliberately absent in M00, and the
known accepted risks.

---

## 1. Secrets

### Requirements

| #   | Requirement                                                                                | Status               |
| :-- | :----------------------------------------------------------------------------------------- | :------------------- |
| S-1 | No credential, API key or connection string for a non-development environment is committed | **Met**              |
| S-2 | Secrets come from the environment, never a committed file                                  | **Met**              |
| S-3 | No credential value is ever logged                                                         | **Met and verified** |
| S-4 | No credential value is ever returned by the API                                            | **Met and verified** |
| S-5 | No credential is passed as a process argument                                              | **Met**              |
| S-6 | `.env` and provider credential stores are git-ignored                                      | **Met**              |

### Implementation

**Credential names, never values.** `ProviderReadiness.MissingCredentialNames`
holds `["google-oauth", "STITCH_API_KEY"]` — names only. The type's own
documentation says so, because this is the field most likely to be "improved"
into leaking a value.

**Process arguments are world-readable.** `/proc/<pid>/cmdline` is readable by
any local process, so the Stitch runner passes credentials only through the
inherited environment and redacts them from log output:

```csharp
if (arg is "--api-key" or "--access-token") { yield return arg; yield return "***"; i++; }
```

**Health endpoints omit exception text.** They are typically unauthenticated, and
exception text contains connection strings and host names. The response carries
check names, statuses and durations only. Asserted:

```csharp
body.ShouldNotContain("Password", Case.Insensitive);
body.ShouldNotContain("aids_dev_only");
body.ShouldNotContain("Host=");
```

**An E2E spec asserts no credential reaches the browser**, including a check for
Google API key patterns (`AIza[0-9A-Za-z_-]{10,}`). Note: the E2E suite is
**unverified** in this environment (see TEST_STRATEGY §2).

**Git-ignored:** `.env*`, `*.pfx`, `*.p12`, and `.stitch/` plus `.stitch.json`,
where the Stitch CLI stores OAuth tokens.

### Development credentials

`docker-compose.yml`, `appsettings.json` and `.env.example` contain
`aids_dev_only`. This is intentional and documented in each file: it grants
access to nothing outside a local container. Deployed environments get their
connection string from the platform.

---

## 2. Input validation and injection

| #    | Requirement                                                   | Status                            |
| :--- | :------------------------------------------------------------ | :-------------------------------- |
| S-7  | All SQL is parameterised                                      | **Met**                           |
| S-8  | Uploaded artifacts are treated as fully untrusted             | **Met and verified**              |
| S-9  | Provider output is parsed, never evaluated                    | **Met**                           |
| S-10 | Payloads validate against a schema before reaching the domain | Schema exists; API wiring **M01** |

**SQL.** EF Core throughout. The one raw query uses `FromSql` with interpolation,
which produces parameters rather than string concatenation:

```csharp
.FromSql($"""SELECT * FROM app.jobs WHERE run_after <= {now} ... FOR UPDATE SKIP LOCKED""")
```

**Uploads.** Parsers must never throw on malformed input — they degrade fidelity
and warn. That is a robustness requirement _and_ a security one: a parser that
throws on crafted input is a denial-of-service vector.

**Provider output.** Parsed as JSON. Never evaluated, never interpolated into a
command.

**Subprocess.** Arguments go through `ProcessStartInfo.ArgumentList`, so there is
no shell and no shell-injection surface. `UseShellExecute = false`.

---

## 3. Path traversal and tenant isolation

| #    | Requirement                                              | Status               |
| :--- | :------------------------------------------------------- | :------------------- |
| S-11 | Storage keys are server-generated, never client-supplied | **Met**              |
| S-12 | A key cannot escape its storage root                     | **Met and verified** |
| S-13 | Keys are organization-prefixed                           | **Met**              |

Storage keys are the only client-influenced value reaching the filesystem, and
therefore the one place a tenant boundary could be crossed.

```
org/{organizationId:N}/project/{projectId:N}/asset/{assetId:N}.{ext}
```

Two layers, deliberately:

1. Shape validation — reject leading `/`, `..`, `\`, `:`, control characters,
   empty or whitespace.
2. The **resolved absolute path** is re-checked against the configured root.

Layer 2 exists so the guard survives a future weakening of layer 1. Seven
rejected inputs are covered by test cases, including
`org/x/../../../../etc/passwd` and a NUL byte.

The original filename is never part of the key — it is a traversal vector and a
collision source.

---

## 4. Cost and abuse

| #    | Requirement                                                      | Status               |
| :--- | :--------------------------------------------------------------- | :------------------- |
| S-14 | Billable provider calls are disabled by default                  | **Met and verified** |
| S-15 | A duplicate submission cannot double-charge                      | **Met and verified** |
| S-16 | A non-retryable error is not retried against a billable endpoint | **Met and verified** |
| S-17 | A hung provider call cannot hold a worker indefinitely           | **Met**              |
| S-18 | Rate limiting and per-organization quota                         | **M02**              |

Spend is a security property here, not just a cost one: an attacker who can
trigger generation can spend the account's money.

- `BillableOperationsEnabled` defaults to `false`; every billable method calls
  `EnsureBillableAllowed`.
- `(organization_id, kind, idempotency_key)` is unique **in the database**, so
  an API race cannot create two billable jobs — and a client-supplied key
  cannot cross a tenant boundary. The scope was global until the M00 review
  (CX-003), which would have returned one tenant's job to another once enqueue
  endpoints existed.
- Every worker-initiated job transition is fenced by lease ownership (CX-001),
  so two workers cannot each believe they own the same billable call.
- `MISSING_CANVAS_CREDENTIALS` and `PERMISSION_DENIED` are classified
  non-retryable — retrying them wastes attempts and money.
- The CLI runs with a timeout and a process-tree kill.

---

## 5. Authentication and authorization — **absent in M00**

| #    | Requirement                                      | Status                           |
| :--- | :----------------------------------------------- | :------------------------------- |
| S-19 | Authentication on all non-health endpoints       | **NOT IMPLEMENTED — M01**        |
| S-20 | Organization-scoped authorization on every query | **NOT IMPLEMENTED — M01**        |
| S-21 | Approval actions attributable to a real identity | **NOT IMPLEMENTED — M01**        |
| S-22 | Audit trail for approvals and waivers            | Partial — metadata on aggregates |

**Stated plainly: no endpoint is authenticated in M00.** The two implemented
endpoint groups are health and read-only provider diagnostics, neither of which
exposes project data — but the API must not be exposed to an untrusted network
before M01.

The data model is ready: `OrganizationId` exists and is indexed on projects,
assets and jobs — `ix_jobs_organization_id` was added during the review
corrections, having been claimed here before it existed. Job idempotency is
organization-scoped, storage keys are organization-prefixed, and `ApprovedBy` and
`WaivedBy` are non-null where a decision is recorded.

Blocked on assumption A-01 (whether the four roles reflect real team structure).

---

## 6. Transport and browser

| #    | Requirement                                          | Status                                 |
| :--- | :--------------------------------------------------- | :------------------------------------- |
| S-23 | HTTPS in deployed environments                       | Deployment concern — **M02**           |
| S-24 | CORS restricted to known origins                     | **Met** — allow-list, no wildcard      |
| S-25 | Security headers (CSP, HSTS, X-Content-Type-Options) | **M01**                                |
| S-26 | No secret in a `NEXT_PUBLIC_` variable               | **Met** — documented in `.env.example` |

`NEXT_PUBLIC_API_BASE_URL` is the only `NEXT_PUBLIC_` variable, and
`.env.example` states explicitly that anything with that prefix is visible in
the browser.

---

## 7. Dependency security

| #    | Requirement                                       | Status            |
| :--- | :------------------------------------------------ | :---------------- |
| S-27 | No high/critical advisory in runtime dependencies | **Met and gated** |
| S-28 | NuGet advisories fail CI                          | **Gated**         |
| S-29 | Dev-only advisories tracked, not ignored          | **Met** — §8      |
| S-30 | Licences checked before adding a dependency       | **Met**           |

```bash
npm run audit:prod     # npm audit --omit=dev --audit-level=high  →  0 vulnerabilities
```

CI also fails on a high or critical NuGet advisory via
`dotnet list package --vulnerable --include-transitive`.

**Why runtime-only for npm.** Some dev-tool advisories have no upstream fix. A
gate that is permanently red is a gate that gets ignored, so dev advisories are
tracked explicitly in §8 instead.

**A real finding from M00.** `shadcn init` added the `shadcn` CLI to
`dependencies`, pulling `@shadcn/registry`, `ts-morph` and their trees into the
runtime graph — 7 high advisories. Moving it to `devDependencies` took the
runtime audit to zero. A code generator does not belong in the production
dependency graph.

**Licence check.** FluentAssertions was removed during M00: v8 moved to a paid
commercial licence for non-OSS use. Shouldly (BSD) replaced it.

---

## 8. Accepted risks

Recorded rather than silently carried.

### R-1 — `braces` stack-exhaustion DoS, dev-only

- **Advisory:** `braces` ≤ 3.0.3, stack exhaustion via deeply nested patterns.
- **Path:** `eslint-config-next` → `@next/eslint-plugin-next` → `fast-glob` →
  `micromatch` → `braces@3.0.3`.
- **Why accepted:** 3.0.3 is the latest published version and is still flagged —
  there is no upstream fix. The only "fix" npm offers is downgrading
  `eslint-config-next` to 14.2.35, a major downgrade incompatible with Next 16.
- **Exposure:** lint-time only, over our own source. Not in the runtime graph;
  `npm audit --omit=dev` is clean.
- **Review:** when `eslint-config-next` updates its `fast-glob` dependency.

### R-2 — `@google/stitch` is pre-1.0

- **Risk:** 0.11.1, published 2026-10-01, two releases in eight days. The README
  states Loop may change "without a major version bump".
- **Why accepted:** it is the provider the product is specified around.
- **Mitigation:** the capability abstraction (ADR-0003) isolates the blast
  radius; the font enum count is asserted so a schema change fails the build;
  `--schema` lets constraints be re-derived mechanically.
- **Tracked as:** assumption A-07.

### R-3 — No authentication in M00

- **Risk:** the API is unauthenticated.
- **Why accepted:** M00 is a foundation milestone; the two endpoint groups expose
  no project data.
- **Mitigation:** do not expose the API to an untrusted network before M01.
- **Resolves:** M01.

### R-4 — `docker-compose.yml` is unverified

- **Risk:** Docker was unavailable in the M00 environment, so the compose file
  has never been executed.
- **Why accepted:** PostgreSQL was installed natively and all integration tests
  ran against it, so the _dependency_ is verified even though the _convenience
  wrapper_ is not.
- **Resolves:** first run in a Docker-capable environment.

---

## 9. Trust boundaries

| Boundary                | Trust                                | Controls                                                              |
| :---------------------- | :----------------------------------- | :-------------------------------------------------------------------- |
| Browser → API           | Untrusted                            | Schema validation (M01), CORS allow-list, problem details, auth (M01) |
| API → PostgreSQL        | Trusted network, untrusted input     | Parameterised queries, least-privilege role                           |
| API/Worker → Stitch CLI | **Untrusted output, trusted binary** | Envelope parsing, no shell, timeout + tree kill, env-only credentials |
| Worker → blob storage   | Trusted                              | Server-generated keys, path confinement re-checked after resolution   |
| Uploads → parsers       | **Fully untrusted**                  | Never throw; degrade and warn                                         |
| Health endpoints        | Unauthenticated                      | Names and statuses only, never exception text                         |

---

## 10. Pre-deployment checklist

Not yet applicable — nothing is deployed, and deployment requires explicit
authorization (rule 10).

- [ ] Authentication and authorization implemented (M01)
- [ ] HTTPS enforced, HSTS set
- [ ] Security headers configured
- [ ] Secrets from a managed store, not environment files
- [ ] Database role is least-privilege, not owner
- [ ] CORS origins set for the real frontend origin
- [ ] Rate limiting and per-organization quota (M02)
- [ ] `BillableOperationsEnabled` decided deliberately per environment
- [ ] Log sink confirmed free of credentials
- [ ] Dependency audit clean for runtime dependencies
- [ ] Backup and restore tested, including the job table

---

## 11. Gates on surfaces not yet built

Raised by the M00 independent review. Each is a precondition on the milestone
that introduces the surface, recorded now so it is not rediscovered later.

| Gate                                                                                                                                                                                                                      | Surface                               | Milestone |
| :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | :------------------------------------ | :-------- |
| Review identity comes from the **authenticated principal**, never a client-supplied `approvedBy` / `waivedBy` / `rejectedBy`                                                                                              | Approval, waiver, rejection endpoints | M01-02    |
| API job queries are **organization-scoped**. `IJobQueue.GetAsync` is deliberately unfiltered for worker-internal use; reusing it at the HTTP boundary would be a cross-tenant read                                        | `GET /jobs/{id}`, cancel              | M01-09    |
| Request and body **size limits**; content verified by **sniffing**, not by trusting the declared MIME type or extension; decompression and resource limits; a malware-handling policy appropriate to the deployment       | Asset upload                          | M02-03    |
| **SSRF controls** on route capture: scheme allow-list, resolved-address range checks, redirect policy, DNS-rebinding mitigation. Output and auth-state paths must be **server-generated and confined**, like storage keys | Route capture                         | M02-03    |
| The billing path is not publicly reachable without authorization, in addition to the existing default-off guard                                                                                                           | Generation endpoints                  | M01-07    |
| Rate limits and per-organization spend quotas                                                                                                                                                                             | All billable endpoints                | M02-08    |

Route capture deserves particular care: it takes a URL and fetches it
server-side, which is a textbook SSRF primitive. The capability exists in the
adapter but no endpoint exposes it, and none should until these controls are in
place.
