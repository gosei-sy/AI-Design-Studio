# Technical Spikes — M00

Six spikes. The purpose of this index is to separate **what we verified by
execution** from **what we are assuming**, so no later milestone builds on a
belief that was never checked.

**Environment of record:** .NET SDK 10.0.400 · Node 26.7.0 · PostgreSQL 15.19 ·
`@google/stitch` 0.11.1 · 2026-10-20.

---

## Summary

| Spike                                          | Subject                                              | Outcome                                                                                                        |
| :--------------------------------------------- | :--------------------------------------------------- | :------------------------------------------------------------------------------------------------------------- |
| [01](SPIKE-01-stitch-auth-and-capabilities.md) | Stitch SDK authentication and capabilities           | ◐ **Partially verified** — real CLI execution, from a downloaded tarball rather than an installed package      |
| [02](SPIKE-02-stitch-screen-retrieval.md)      | Generating or retrieving a screen through Stitch     | ⛔ **Blocked** — no credentials. Test plan provided.                                                           |
| [03](SPIKE-03-screenshot-import.md)            | Importing a screenshot with metadata                 | ◐ **Partially verified** — local path and fidelity limits verified; the provider upload envelope is unexecuted |
| [04](SPIKE-04-artifact-normalization.md)       | Parsing an artifact into a normalized representation | ✅ **Verified**                                                                                                |
| [05](SPIKE-05-versioned-design-contracts.md)   | Storing and retrieving versioned Design Contracts    | ✅ **Verified** — against real PostgreSQL; review qualifications CX-004/006 fixed                              |
| [06](SPIKE-06-durable-background-jobs.md)      | Executing a durable background job                   | ✅ **Verified** — against real PostgreSQL; review qualifications CX-001/002/003 fixed                          |

These classifications follow the independent reviews, which found the original
"5 verified, 1 unverifiable" summary too broad. The current split is **3 verified, 2 partially verified, 1 blocked**, counted
from the table above. Any summary elsewhere that disagrees with this table is
wrong; this table is the source. Spikes 05 and 06 were upgraded to
Verified once the defects behind their qualifications were fixed and tested;
spikes 01 and 03 remain qualified for reasons that need an external prerequisite,
not more code.

---

## Verified capabilities

Each was demonstrated by running code, and each has an automated test that fails
if it regresses.

| Capability                                                              | How verified                                                                     |
| :---------------------------------------------------------------------- | :------------------------------------------------------------------------------- |
| The Stitch CLI exists, runs offline, and reports auth state             | `stitch status --json` executed with no credentials                              |
| The CLI exits **0 even on failure**; success lives in the JSON envelope | `stitch get screen … --json` returned `{"success":false,…}` with exit 0          |
| Errors carry a stable machine-readable code                             | `MISSING_CANVAS_CREDENTIALS` observed with remediation text                      |
| Stitch requires **two independent** credentials                         | `canvas.credential: STITCH_API_KEY`, separate from OAuth                         |
| `--dry-run` plans a generation with no credentials and no network       | Returned the ordered tool calls `create_project`, `generate_screen_from_text`    |
| `--schema` exposes JSON Schema for every command                        | Used to derive the theme constraint set                                          |
| Stitch's theme model is substantially constrained                       | 67 fonts, one `colorMode`, 5 roundness steps, no elevation/motion/breakpoints    |
| All four of our target devices exist provider-side                      | `MOBILE`, `DESKTOP`, `TABLET`, `AGNOSTIC` in the schema                          |
| A screenshot yields visual fidelity only                                | Parser returns `Visual`, null tree, empty token lists                            |
| A DOM snapshot yields a semantic tree and token candidates              | 8 parser tests over a realistic captured page                                    |
| Versioned contracts persist with approval metadata and hashing          | Round-trip against PostgreSQL 15.19                                              |
| "At most one approved version" holds **in the database**                | Direct SQL insert rejected by `ux_contract_single_approved`                      |
| Durable jobs survive worker crash via lease expiry                      | Simulated crash, reclaimed by a second worker                                    |
| Concurrent workers take disjoint jobs                                   | Two workers, `FOR UPDATE SKIP LOCKED`, zero overlap                              |
| Duplicate submissions cannot double-charge                              | Unique `(organization_id, kind, idempotency_key)` rejected a second insert       |
| Idempotency is organization-scoped, so a key cannot cross a tenant      | `(organization_id, kind, idempotency_key)` unique index plus a cross-tenant test |
| Every worker write is fenced by lease ownership                         | A stale worker's complete and fail are both rejected, against PostgreSQL         |
| A crash-looping job reaches `Dead`                                      | Full attempt budget walked against PostgreSQL, then dead-lettered                |
| One contract and one design system per project                          | Second insert rejected by a unique index                                         |
| The design system lifecycle matches the contract's                      | 20 unit plus 3 persistence tests                                                 |
| No aggregate hands out a mutable collection                             | Architecture test, verified to fail on the regression                            |
| Migrations apply cleanly to an empty database                           | Every integration run provisions a fresh database and migrates it                |
| The API serves health against a real database                           | `curl` against a running host: all three endpoints `200 Healthy`                 |
| The adapter detects the real CLI through the API                        | `readiness` returned `isInstalled: true` with both credentials named missing     |

---

## Unverified — and the risk each carries

| Assumption                                                        | Why unverified                                                    | Risk if wrong                                                                                                                          |
| :---------------------------------------------------------------- | :---------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------- |
| **A server-side Stitch credential exists**                        | No Stitch account; OAuth is interactive (browser + loopback port) | **Highest.** M01 generation may need per-user delegated tokens — an architecture change needing re-approval. Tracked as A-08 / M01-01. |
| `generate screen` returns `id`, `project`, `canvasUrl` on success | Only the `--dry-run` envelope was observed                        | Response mapping is wrong; one job fails and the mapping is corrected                                                                  |
| `get screen` exposes markup under `html` or `markup`              | Field names inferred, not observed                                | Retrieval returns null markup; field names corrected                                                                                   |
| `upload screen` returns the same envelope shape                   | Not executed                                                      | Import mapping wrong; corrected on first live run                                                                                      |
| `generate variants` response shape                                | Not executed                                                      | **None today** — the capability is deliberately not declared (M01-07)                                                                  |
| Per-generation cost                                               | No pricing data                                                   | Quota design (M02) may be mis-sized. Tracked as A-09.                                                                                  |
| Rate limits                                                       | Not documented per-endpoint                                       | Backoff tuning may be wrong; the retry mechanism itself is verified                                                                    |
| Azure Blob behaves like the local adapter                         | Adapter not written                                               | Port is defined; divergence surfaces in M02                                                                                            |

**The adapter's design assumes these are unverified.** Capabilities not
verified are either absent from `Capabilities` (variants) or isolated behind the
envelope parser, which is itself verified against recorded real output. A wrong
field name costs one failed job, not a redesign.

---

## Infrastructure limits in this environment

Reported because they bound what "verified" means here.

| Limit                                     | Impact                                                                                                                                                | Mitigation                                                                                                              |
| :---------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------- |
| **Docker unavailable**                    | `docker-compose.yml` was not executed; Testcontainers could not be used                                                                               | PostgreSQL 15.19 installed natively and used for all integration tests. The compose file is written but **unverified**. |
| **No Stitch account**                     | SPIKE-02 unverifiable                                                                                                                                 | Documented reproducible test plan; adapter tests use recorded real CLI output                                           |
| ~~Playwright Chromium not installed~~     | ✅ **Resolved in M01-11** — the browser build matching the lockfile is installed and the suite passes **7/7** locally; only the CI run is outstanding | Was: E2E not executed during M00                                                                                        |
| PostgreSQL 15 locally vs 17 in compose/CI | Minor                                                                                                                                                 | No version-specific features used. `FOR UPDATE SKIP LOCKED` (9.5+) and filtered indexes are long-standing.              |

---

## How to re-verify

```bash
# Verified spikes — all of these run in this environment
dotnet test                                   # 258 backend tests
npm test                                      # 14 frontend + 164 contract tests

# Spike 01, re-derive provider constraints directly
npx @google/stitch status --json
npx @google/stitch generate screen --schema
npx @google/stitch edit design-system --schema

# Spike 02 — needs credentials; see SPIKE-02 for the full plan
export STITCH_API_KEY=...
npx @google/stitch login
```
