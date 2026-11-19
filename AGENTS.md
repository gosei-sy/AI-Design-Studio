# AGENTS.md

Agent-agnostic working rules for AI Design Studio. Applies to any coding agent:
Claude Code, Codex, Gemini CLI, Cursor, OpenCode, or a human following the same
discipline.

[CLAUDE.md](CLAUDE.md) holds the same rules with more examples. This file is the
short form plus the conventions specific to running an agent here.

---

## Before you start

1. Read [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) and
   [docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md)
   to find the current milestone.
2. Read the ADRs in
   [docs/02-architecture/decisions/](docs/02-architecture/decisions/). Seven
   decisions are accepted and binding.
3. Read [docs/06-spikes/README.md](docs/06-spikes/README.md) to learn what is
   **verified** and what is **assumed**. Do not build on an assumption as if it
   were a finding.

---

## The ten rules

1. Read the project documentation before modifying code.
2. Never silently change an approved architectural decision — supersede the ADR
   and get approval first.
3. Keep implementation aligned with the current milestone.
4. Every important business invariant must have an automated test.
5. Do not claim a feature works without verification.
6. Document blockers and external dependencies.
7. Never mark a milestone complete while its acceptance criteria are unmet.
8. Prefer simple, maintainable solutions.
9. Do not automatically start the next milestone.
10. Do not commit or deploy without explicit authorization.

---

## Hard constraints

### Do not spend money

`Providers:Stitch:BillableOperationsEnabled` defaults to `false`. Do not set it
to `true` without explicit authorization. Planning (`--dry-run`) and readiness
probes are free and sufficient for development.

### Do not commit or push

No `git commit`, `git push`, `gh pr create` or deployment unless asked. Leave
the working tree for a human to review.

### Do not weaken a guard to make a test pass

If a test fails because a guard rejects something, the usual cause is that the
test is wrong. Guards under no circumstances to be loosened casually:

| Guard                                | Why                                          |
| :----------------------------------- | :------------------------------------------- |
| `contract.version_immutable`         | Approval must mean something (ADR-0002)      |
| `screen.contract_drift`              | Stops stale output being approved (ADR-0002) |
| `inventory.uncovered_features`       | The no-functional-loss gate (ADR-0007)       |
| `inventory.waiver_*_required`        | Makes a decision different from an omission  |
| `InvalidStorageKeyException`         | Path traversal and cross-tenant reads        |
| `BILLABLE_OPERATIONS_DISABLED`       | Accidental spend                             |
| `ArtifactFidelity.Visual` for images | Prevents overstating a screenshot (ADR-0006) |

### Do not add infrastructure

No message broker, no Redis, no Kubernetes, no second database, no new external
service without an ADR. The durable job queue lives in PostgreSQL (ADR-0005) and
PostgreSQL is the only runtime dependency.

---

## Verification commands

Run these and report their **actual** output.

```bash
# Backend
dotnet build                         # warnings are errors
dotnet test                                    # 477 tests
dotnet format --verify-no-changes

# Frontend and shared contracts
npm test                             # 14 + 164 tests
npm run typecheck
npm run lint
npm run format:check
npm run build --workspace frontend

# Migration sync
dotnet-ef migrations has-pending-model-changes \
  --project backend/src/AIDesignStudio.Infrastructure \
  --startup-project backend/src/AIDesignStudio.Api
```

Integration tests need PostgreSQL on `localhost:5432`, or
`AIDS_TEST_CONNECTION` pointing elsewhere. Without it those tests **skip with a
reason** rather than failing — if you see skips, say so; do not report 477 passed
when 57 skipped.

---

## Environment notes

| Tool             | Version verified              | Notes                                                         |
| :--------------- | :---------------------------- | :------------------------------------------------------------ |
| .NET SDK         | 10.0.400                      | `global.json` pins 10.x with `latestFeature`                  |
| Node             | 26.7.0                        | Next 16 requires ≥ 20                                         |
| PostgreSQL       | 15.19 local, 17 in compose/CI | No version-specific features used                             |
| `dotnet-ef`      | 10.0.12                       | `dotnet tool install --global dotnet-ef`                      |
| `@google/stitch` | 0.11.1                        | Optional. Without it, readiness reports `isInstalled: false`. |

### Known environment gaps

| Gap                                | Consequence                                                                                                 |
| :--------------------------------- | :---------------------------------------------------------------------------------------------------------- |
| Docker may be unavailable          | `docker-compose.yml` is **unverified**. Install PostgreSQL natively instead.                                |
| No Stitch credentials              | Live generation unverifiable. See SPIKE-02 for the test plan.                                               |
| Playwright E2E needs both services | **7/7 pass locally** (M01-11); the CI run is still outstanding. Needs the API and the HTTPS studio running. |

Report a gap; do not paper over it.

---

## Testing conventions

- **xUnit v3** on Microsoft.Testing.Platform. `dotnet test` works because
  `global.json` has a `test.runner` section and test projects are `Exe`.
- **Shouldly**, not FluentAssertions. FluentAssertions v8 moved to a paid
  commercial licence; do not reintroduce it.
- Test names are sentences describing the invariant:
  `An_approved_version_cannot_be_modified`.
- Use `TestClock` / `MutableClock` for anything time-dependent. Never
  `Thread.Sleep`.
- `TestContext.Current.CancellationToken` in integration tests;
  `CancellationToken.None` is acceptable in unit tests.
- Integration tests skip with `Assert.SkipUnless(...)` when no database is
  reachable, and the skip reason says how to fix it.

---

## Commit message convention

For when a human authorizes a commit:

```
<type>(<scope>): <summary>

<why, not what — the diff shows what>

Refs: <ADR / task id>
```

Types: `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `adr`.
Scopes: `domain`, `api`, `worker`, `infra`, `frontend`, `contracts`, `ci`, `docs`.

---

## Reporting template

```
## Completed
<what, with file paths>

## Commands run
<command>
<actual output>

## Test results
Backend:   N passed, N failed, N skipped
Frontend:  N passed
Contracts: N passed

## Verified
<demonstrated by execution, with the test that holds it>

## Assumed
<not verified, and what would verify it>

## Blockers
<what is blocked, and what unblocks it>

## Not done
<in scope but incomplete, and why>
```

A failing test reported as passing is worse than a failing test. If something
does not work, say so and include the output.
