# AI Design Studio

Helps developers and product teams design Web and Mobile applications
consistently using AI tools such as Google Stitch.

**Core principle:** the Design Contract and the approved Design System are the
source of truth — not the output of any individual AI provider.

**Status:** M00 (foundation) ✅ **complete** — independently re-accepted
(APPROVED, 2026-10-21). **M01 is in progress.** **M01-02 (authentication) was
independently accepted on 2026-11-03** — APPROVED with non-blocking findings:
password-reset delivery (**D-8**) is unresolved and is a fail-closed
pre-production gate, and MFA recovery codes remain a recorded gap. **M01-03
(organization-scoped authorization) is implemented; three independent review
rounds returned CHANGES REQUIRED and all nine findings are corrected, so it
awaits independent re-acceptance** — it is not accepted yet.

There is no product UI yet; the single page reports environment status. The
Playwright E2E suite runs — **9/9 specs pass** against both services, including
the full browser auth flow. `docker-compose.yml` remains **unverified** because
Docker is not installed here (M01-12). See
[docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md).

---

## Local setup

### Prerequisites

| Tool             | Version | Notes                                                                  |
| :--------------- | :------ | :--------------------------------------------------------------------- |
| .NET SDK         | 10.0+   | Pinned by `global.json`                                                |
| Node.js          | 20+     | Verified on 26.7.0                                                     |
| PostgreSQL       | 15+     | Via Docker, or installed natively                                      |
| `dotnet-ef`      | 10.x    | `dotnet tool install --global dotnet-ef`                               |
| `@google/stitch` | 0.11.1+ | Optional. Without it, provider readiness reports `isInstalled: false`. |

### 1. Start PostgreSQL

**With Docker** — convenient, but ⚠ this compose file has **not** been executed
(Docker was unavailable in the M00 environment; see accepted risk R-4):

```bash
docker compose up -d postgres
docker compose ps          # wait for "healthy"
```

**Natively** — this is the path that was verified:

```bash
# Debian/Ubuntu
sudo apt-get install -y postgresql-15
sudo pg_ctlcluster 15 main start

sudo -u postgres psql -c "CREATE ROLE aids WITH LOGIN PASSWORD 'aids_dev_only' CREATEDB"
sudo -u postgres psql -c "CREATE DATABASE aidesignstudio OWNER aids"

pg_isready -h localhost -p 5432
```

`aids_dev_only` is a development-only password. It grants access to nothing
outside your machine and is intentionally in the repository. Real credentials
come from the environment — see [.env.example](.env.example).

### 2. Configure

```bash
cp .env.example .env
```

Defaults work for local development. Leave `STITCH_API_KEY` empty to run without
provider access: the adapter reports itself unusable and nothing silently
degrades.

### 3. Apply migrations

```bash
export PATH="$PATH:$HOME/.dotnet/tools"

dotnet-ef database update \
  --project backend/src/AIDesignStudio.Infrastructure \
  --startup-project backend/src/AIDesignStudio.Api
```

### 4. Install dependencies and build

```bash
npm install          # npm workspaces: frontend + packages/*
dotnet build         # warnings are errors
```

### 5. Run

Three processes, in separate terminals:

```bash
# API → https://localhost:7080 and http://localhost:5080
# Swagger at /swagger (Development only, and requires sign-in).
dotnet run --project backend/src/AIDesignStudio.Api

# Worker (job dispatcher; no handlers registered in M00)
dotnet run --project backend/src/AIDesignStudio.Worker

# Studio → https://localhost:3000   ← HTTPS, and this is the only origin you open
npm run dev:https --workspace frontend
```

**Open the studio over HTTPS, and only the studio.** The frontend proxies
`/api/*` and `/health*` to the API server-side (`next.config.ts`), so the
browser talks to one origin and the session cookie is same-origin.

That is not a style preference. The session cookie is `Secure` and
`SameSite=Strict`, and `SameSite` is compared **schemefully** — an
`http://localhost:3000` page and an `https://localhost:7080` API are _different
sites_, so the cookie is never sent. Reviewers reproduced it twice in Chromium:
a token was issued, registration returned `400 auth.antiforgery_failed`, and the
cookie jar stayed empty (M01-W2-006, M01-W2-RR-003).

In deployment TLS terminates at a reverse proxy in front of both, which is the
same shape.

### 6. Verify

```bash
# Through the studio origin, which is how a browser reaches the API.
# Health probes are anonymous.
curl -sk https://localhost:3000/health                             # {"status":"ok"}
curl -sk https://localhost:3000/health/ready | python3 -m json.tool # database Healthy

# Everything else requires a session (S-19). Register, sign in keeping cookies,
# then call it. State-changing requests need the antiforgery token echoed back.
#
# Registration is mapped in DEVELOPMENT ONLY (D-9, ADR-0009). Outside
# Development the route does not exist, and accounts come from the provisioning
# tool below. A registered account is authenticated and authorized for nothing:
# it has no organization and no role until an operator provisions it.
TOKEN=$(curl -sk -c jar https://localhost:3000/api/v1/auth/antiforgery-token | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')
curl -sk -b jar -c jar -X POST https://localhost:3000/api/v1/auth/register \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $TOKEN" \
  -d '{"email":"you@example.test","password":"Correct-Horse-9-Battery!"}'
curl -sk -b jar -c jar -X POST https://localhost:3000/api/v1/auth/login \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $TOKEN" \
  -d '{"email":"you@example.test","password":"Correct-Horse-9-Battery!"}'
curl -sk -b jar https://localhost:3000/api/v1/providers | python3 -m json.tool

# Organization endpoints need a membership, which registration does not grant.
# Before provisioning, this is a 403 with code authz.no_organization_membership:
curl -sk -b jar https://localhost:3000/api/v1/organizations/current | python3 -m json.tool

# -k because both local development certificates are self-signed.
```

### 7. Grant yourself access — `aids-provision`

A membership and its roles come **only** from the operator provisioning tool
(D-9, [ADR-0009](docs/02-architecture/decisions/ADR-0009-account-proofing-is-trusted-operator-provisioning.md)).
There is no HTTP route that grants access, in any environment.

**Run it interactively.** It prompts for the new account's password and does not
echo it:

```bash
export AIDS_ConnectionStrings__Default="Host=localhost;Port=5432;Database=aidesignstudio;Username=aids;Password=aids_dev_only"
export DOTNET_ENVIRONMENT=Development

# --actor is required — it records who asserts this person was verified.
dotnet run --project backend/src/AIDesignStudio.Provisioning -- \
  provision --email you@example.test --organization "Acme Design" \
  --role Member --role Approver --actor "$USER@$(hostname)"
# → Initial password for the new account (not echoed):

# Withdraw it again (no password involved):
dotnet run --project backend/src/AIDesignStudio.Provisioning -- \
  revoke --email you@example.test --actor "$USER@$(hostname)"
```

> ### The password, and where it must not go
>
> **Never `echo 'secret' | aids-provision`.** W3-004: the tool refuses
> `--password` because argv is world-readable through `/proc/<pid>/cmdline` and
> lands in shell history — and piping a literal puts it in exactly the same two
> places, just via the producer process instead. **Reading from stdin is only as
> safe as whatever writes to it.**
>
> For automation, keep the literal out of both the command line and the history:
>
> ```bash
> read -rs INITIAL_PASSWORD        # prompts, echoes nothing, not a command
> printf '%s' "$INITIAL_PASSWORD" | dotnet run --project backend/src/AIDesignStudio.Provisioning -- \
>   provision --email you@example.test --organization "Acme Design" \
>   --role Member --actor "$USER@$(hostname)"
> unset INITIAL_PASSWORD
> ```
>
> `printf` is a shell builtin, so no process is created carrying the value, and
> the history records only `"$INITIAL_PASSWORD"`. A secret file read with
> `< /path/to/secret` is equally acceptable if the file's permissions and
> lifetime are managed. What is never acceptable is the literal appearing in a
> command.

Then `GET /api/v1/organizations/current` returns the organization and roles, and
`GET /api/v1/organizations/current/members` works for an Approver. Changes take
effect on the next request — no sign-out needed.

**The tool cannot run in Production today.** It starts the same host as the API,
which refuses to start without a password-reset transport (**D-8**, ADR-0008 A2).
That is the fail-closed gate working, not a defect.

Then open <https://localhost:3000>.

---

## Tests

```bash
dotnet test                 # 477: unit + architecture + integration
npm test                    # 183: 19 frontend + 164 contract schema
```

Integration tests provision a throwaway database per run and migrate it from
empty — so "migrations apply cleanly" is verified continuously.

Without PostgreSQL they **skip with a reason** rather than failing. If you see
skips, report them as skips.

```bash
# Point at a different server
export AIDS_TEST_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=aids;Password=aids_dev_only"

# Per-test failure detail instead of a summary
dotnet test -p:TestingPlatformShowTestsFailure=true

# SQL logging when diagnosing a persistence failure
AIDS_TEST_SQL_LOG=1 dotnet test --project backend/tests/AIDesignStudio.IntegrationTests/AIDesignStudio.IntegrationTests.csproj
```

### Other gates

```bash
dotnet format --verify-no-changes
npm run typecheck && npm run lint && npm run format:check
npm run build --workspace frontend
npm run audit:prod

dotnet-ef migrations has-pending-model-changes \
  --project backend/src/AIDesignStudio.Infrastructure \
  --startup-project backend/src/AIDesignStudio.Api
```

End-to-end tests run as of M01-11. They need the API running, because Playwright
starts only the frontend — deliberately, since the two are separately deployed
processes:

```bash
# Install the browser build matching the lockfile. Use the workspace binary:
# `npx playwright` may resolve a different version, whose browser build the
# test run then cannot find.
(cd frontend && ./node_modules/.bin/playwright install chromium)

dotnet run --project backend/src/AIDesignStudio.Api &
npm run test:e2e --workspace frontend     # 9 passed
```

Playwright starts the frontend itself over HTTPS and proxies to the API, so the
specs exercise the real cookie policy rather than a relaxed one. Three of the
seven drive the full browser auth flow — token issuance, registration, login,
authenticated call, logout, and antiforgery rejection — because the previous two
rounds both shipped an auth API that worked in `TestServer` and failed in a
browser.

---

## Google Stitch (optional)

The product works without it; provider features simply report themselves
unavailable.

```bash
npm install -g @google/stitch
stitch login              # interactive: browser + loopback port
export STITCH_API_KEY=... # separate from OAuth; required for canvas operations
stitch status --json
```

Stitch needs **two independent credentials**, and being signed in is not
sufficient — canvas operations (projects, screens, design systems) require the
API key. See
[SPIKE-01](docs/06-spikes/SPIKE-01-stitch-auth-and-capabilities.md).

### Spending money

`Providers:Stitch:BillableOperationsEnabled` defaults to **`false`**. While it is
off, the adapter refuses every billable call and serves only readiness probes and
free dry-run planning. Enable it deliberately or not at all.

---

## Repository layout

```
backend/src/
  AIDesignStudio.SharedKernel      Entity, IClock, DomainException
  AIDesignStudio.Domain            8 modules; invariants; no infrastructure
  AIDesignStudio.Application       ports, provider capability contracts
  AIDesignStudio.Infrastructure    EF Core, storage, provider adapters, parsers
  AIDesignStudio.Api               HTTP host
  AIDesignStudio.Worker            durable job dispatcher
  AIDesignStudio.Provisioning      aids-provision — operator provisioning CLI (ADR-0009)
backend/tests/
  AIDesignStudio.UnitTests         262 tests
  AIDesignStudio.IntegrationTests  190 tests (needs PostgreSQL)
  AIDesignStudio.ArchitectureTests  25 tests (boundary + organization-scope guards)
frontend/                          Next.js studio
packages/contracts/                shared JSON Schemas + TypeScript types
docs/                              see below
```

Dependencies point inward only, enforced by the architecture tests:
`SharedKernel ← Domain ← Application ← Infrastructure ← hosts`.

---

## Documentation

**Start here:** [CLAUDE.md](CLAUDE.md) / [AGENTS.md](AGENTS.md) — working rules ·
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) — milestones and M01 tasks

| Area         |                                                                                                                                                                                                                          |
| :----------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Product      | [PRD](docs/01-product/PRD.md) · [User stories](docs/01-product/USER_STORIES.md) · [MVP scope](docs/01-product/MVP_SCOPE.md) · [Open assumptions](docs/01-product/OPEN_ASSUMPTIONS.md)                                    |
| Architecture | [System architecture](docs/02-architecture/SYSTEM_ARCHITECTURE.md) · [Data model](docs/02-architecture/DATA_MODEL.md) · [API contracts](docs/02-architecture/API_CONTRACTS.md) · [ADRs](docs/02-architecture/decisions/) |
| Design       | [UX workflow](docs/03-design/UX_WORKFLOW.md) · [Screen inventory](docs/03-design/SCREEN_INVENTORY.md)                                                                                                                    |
| Engineering  | [Coding standards](docs/04-engineering/CODING_STANDARDS.md) · [Test strategy](docs/04-engineering/TEST_STRATEGY.md) · [Security](docs/04-engineering/SECURITY_REQUIREMENTS.md)                                           |
| Delivery     | [Status](docs/05-delivery/IMPLEMENTATION_STATUS.md)                                                                                                                                                                      |
| Spikes       | [Verified vs assumed](docs/06-spikes/README.md)                                                                                                                                                                          |

### The seven architectural decisions

1. [Modular monolith, not microservices](docs/02-architecture/decisions/ADR-0001-modular-monolith.md)
2. [The Design Contract is the source of truth](docs/02-architecture/decisions/ADR-0002-design-contract-is-source-of-truth.md)
3. [Capability-based provider adapters](docs/02-architecture/decisions/ADR-0003-capability-based-provider-adapters.md)
4. [Provider theme projection is lossy, and every loss is reported](docs/02-architecture/decisions/ADR-0004-provider-theme-projection-is-lossy.md)
5. [Durable background jobs in PostgreSQL](docs/02-architecture/decisions/ADR-0005-durable-jobs-in-postgresql.md)
6. [A screenshot import is a visual reference, not an editable design](docs/02-architecture/decisions/ADR-0006-screenshot-import-is-visual-only.md)
7. [Dropping a feature in a redesign requires an explicit waiver](docs/02-architecture/decisions/ADR-0007-functional-loss-requires-an-explicit-waiver.md)

---

## Troubleshooting

**`dotnet test` fails with "Testing with VSTest target is no longer supported"**
`global.json` must contain the `test.runner` section. Tests are xUnit v3 on
Microsoft.Testing.Platform.

**Integration tests all skip**
PostgreSQL is not reachable. The skip message names the host, port and the
variable to set.

**`DbUpdateConcurrencyException: expected to affect 1 row(s), but actually
affected 0`**
`AppDbContext.ConfigureDomainGeneratedKeys` has probably been removed or
bypassed. See [DATA_MODEL §9](docs/02-architecture/DATA_MODEL.md) — this is a
known trap, not a race.

**Provider readiness says `isInstalled: false`**
The `stitch` CLI is not on PATH. Either install it or set
`Providers__Stitch__ExecutablePath`.

**`BILLABLE_OPERATIONS_DISABLED`**
Working as intended. Billable calls are off by default.

**A port is already in use**
API 5080, frontend 3000, PostgreSQL 5432. Change the API port in
`backend/src/AIDesignStudio.Api/Properties/launchSettings.json`.

---

## Contributing

Read [CLAUDE.md](CLAUDE.md) first. The ten rules are binding, in particular:

- Read the documentation before changing code.
- Never silently change an approved ADR.
- Every important business invariant needs an automated test.
- Do not claim a feature works without verification.
- Do not commit, deploy, or spend money without explicit authorization.
