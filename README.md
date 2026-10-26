# AI Design Studio

Helps developers and product teams design Web and Mobile applications
consistently using AI tools such as Google Stitch.

**Core principle:** the Design Contract and the approved Design System are the
source of truth — not the output of any individual AI provider.

**Status:** M00 (foundation) ✅ **complete** — independently re-accepted
(APPROVED, 2026-10-21) after four correction rounds. **M01 is in progress**,
authorized 2026-10-21, currently Wave 1.

There is no product UI yet; the single page reports environment status. The
Playwright E2E suite now runs — 4/4 specs pass against a reachable API (M01-11).
`docker-compose.yml` remains **unverified** because Docker is not installed here
(M01-12). See
[docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md)
and [the Wave 1 report](docs/05-delivery/M01_WAVE1_REPORT.md).

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
# API → http://localhost:5080  (Swagger at /swagger)
dotnet run --project backend/src/AIDesignStudio.Api

# Worker (job dispatcher; no handlers registered in M00)
dotnet run --project backend/src/AIDesignStudio.Worker

# Frontend → http://localhost:3000
npm run dev
```

### 6. Verify

```bash
curl -s localhost:5080/health                                   # {"status":"ok"}
curl -s localhost:5080/health/ready | python3 -m json.tool       # database Healthy
curl -s localhost:5080/api/v1/providers/ | python3 -m json.tool   # google-stitch + capabilities
curl -s localhost:5080/api/v1/providers/google-stitch/readiness | python3 -m json.tool
```

Then open <http://localhost:3000>.

---

## Tests

```bash
dotnet test                 # 258: unit + architecture + integration
npm test                    # 178: 14 frontend + 164 contract schema
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
npm run test:e2e --workspace frontend     # 4 passed
```

With the API down, three pass and one skips by design — the provider-listing
spec cannot assert what it cannot reach. CI starts the API, so CI runs all four.

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
  AIDesignStudio.Domain            7 modules; invariants; no infrastructure
  AIDesignStudio.Application       ports, provider capability contracts
  AIDesignStudio.Infrastructure    EF Core, storage, provider adapters, parsers
  AIDesignStudio.Api               HTTP host
  AIDesignStudio.Worker            durable job dispatcher
backend/tests/
  AIDesignStudio.UnitTests         185 tests
  AIDesignStudio.IntegrationTests   57 tests (needs PostgreSQL)
  AIDesignStudio.ArchitectureTests  16 tests (module boundaries)
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
