# CLAUDE.md — working rules for AI Design Studio

Instructions for any AI coding agent working in this repository. Read this
before changing code.

---

## Mandatory rules

These are not suggestions.

### 1. Read the documentation before modifying code

Before touching anything, read what governs it:

| Changing             | Read first                                                                                                                                                     |
| :------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Anything             | [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md), [docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md)                       |
| Domain or invariants | [docs/02-architecture/SYSTEM_ARCHITECTURE.md](docs/02-architecture/SYSTEM_ARCHITECTURE.md), the relevant ADR                                                   |
| Persistence          | [docs/02-architecture/DATA_MODEL.md](docs/02-architecture/DATA_MODEL.md)                                                                                       |
| HTTP surface         | [docs/02-architecture/API_CONTRACTS.md](docs/02-architecture/API_CONTRACTS.md)                                                                                 |
| Provider adapters    | [ADR-0003](docs/02-architecture/decisions/ADR-0003-capability-based-provider-adapters.md), [SPIKE-01](docs/06-spikes/SPIKE-01-stitch-auth-and-capabilities.md) |
| Product behaviour    | [docs/01-product/PRD.md](docs/01-product/PRD.md), [docs/01-product/MVP_SCOPE.md](docs/01-product/MVP_SCOPE.md)                                                 |
| Tests                | [docs/04-engineering/TEST_STRATEGY.md](docs/04-engineering/TEST_STRATEGY.md)                                                                                   |

The ADRs record _why_. A change that contradicts one without saying so is a
defect, regardless of whether it compiles.

### 2. Never silently change an approved architectural decision

The seven ADRs are accepted. To change one:

1. Write a new ADR that supersedes it, with context and consequences.
2. Update the old ADR's status line to `Superseded by ADR-NNNN`.
3. Get it approved **before** implementing.

Never edit an accepted ADR's decision in place. The record of what was decided is
the artifact.

### 3. Keep implementation aligned with the current milestone

The current milestone is in
[docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md).
Work outside it needs explicit authorization — including work that looks like an
obvious improvement.

### 4. Every important business invariant has an automated test

An invariant without a test is not implemented. The test must fail if the
invariant is removed.

Invariants currently under test: contract and design system version
immutability, single-approved-version (aggregate **and** database), screen
contract-drift, feature-coverage handoff gate, job lease and retry semantics,
idempotency, storage path confinement, artifact fidelity limits, projection
lossiness.

### 5. Do not claim a feature works without verification

The distinction between **verified** and **assumed** is a product requirement
here, not a nicety.

- "Verified" means you ran it and there is a test that fails on regression.
- Anything else is **assumed** and must be labelled as such.
- A capability that has not been verified against a live provider must be absent
  from `Capabilities`, not present and hopeful.

`docs/06-spikes/README.md` holds the verified/unverified split. Keep it current.

### 6. Document blockers and external dependencies

A blocker goes in
[docs/01-product/OPEN_ASSUMPTIONS.md](docs/01-product/OPEN_ASSUMPTIONS.md) with
its impact and how to resolve it. Do not work around a blocker silently —
A-08 (server-side Stitch credentials) can change the architecture, and a
workaround would hide that.

### 7. Never mark a milestone complete while acceptance criteria are unmet

Every criterion must be demonstrable with evidence: a command, its output, a
test name. "Should work" is not evidence.

### 8. Prefer simple, maintainable solutions

No microservices, no event buses, no Kubernetes, no caching layer, no new
infrastructure without an ADR. When two designs work, choose the one that is
easier to delete.

### 9. Do not automatically start the next milestone

Stop at the milestone boundary and report. Wait for acceptance.

### 10. Do not commit or deploy without explicit authorization

No `git commit`, no `git push`, no deployment, and no action that spends money
with an external provider, unless asked. `BillableOperationsEnabled` stays
`false` by default for this reason.

---

## Verify before you report

Run these and paste the real output. Never report a result you did not observe.

```bash
dotnet build                                   # warnings are errors
dotnet test                                    # 258 tests
dotnet format --verify-no-changes              # style gate
npm test                                       # 14 frontend + 164 contract tests
npm run typecheck && npm run lint
npm run format:check
npm run build --workspace frontend
```

Migration sync — catches a model change committed without its migration:

```bash
dotnet-ef migrations has-pending-model-changes \
  --project backend/src/AIDesignStudio.Infrastructure \
  --startup-project backend/src/AIDesignStudio.Api
```

Integration tests need PostgreSQL. See [README.md](README.md) for setup.

---

## Code conventions

Full detail in
[docs/04-engineering/CODING_STANDARDS.md](docs/04-engineering/CODING_STANDARDS.md).
The ones most easily got wrong:

### Time

```csharp
// WRONG — untestable
var now = DateTimeOffset.UtcNow;

// RIGHT
public void Approve(string approvedBy, IClock clock) { ApprovedAt = clock.UtcNow; }
```

Lease expiry and retry backoff must be verifiable in milliseconds, not by
sleeping.

### Domain errors

```csharp
// WRONG — the API cannot map this, and clients would parse prose
throw new InvalidOperationException("Can't edit this");

// RIGHT — stable code becomes the problem-details type
throw new DomainException("contract.version_immutable",
    $"Version {VersionNumber} is {Status} and cannot be modified. Start a new draft instead.");
```

Codes are a public contract. Renaming one is a breaking change.

### Provider capabilities

```csharp
// WRONG — assumes support
await provider.GenerateScreenAsync(request, ct);

// RIGHT — asks first
var generation = provider.GetCapability<IScreenGenerationCapability>();
if (generation is null) { /* this provider cannot generate */ }
```

### Provider names

A type whose name contains `Stitch`, `Gemini` or `Figma` may exist **only** in
`AIDesignStudio.Infrastructure.Providers`. Asserted by an architecture test.
This holds ADR-0002 in place.

### Never log or return a credential

```csharp
// RIGHT — names only
MissingCredentialNames = ["STITCH_API_KEY"]
```

Credentials are never process arguments either: `/proc/<pid>/cmdline` is
world-readable.

---

## Repository layout

```
backend/src/
  AIDesignStudio.SharedKernel      Entity, IClock, DomainException
  AIDesignStudio.Domain            7 modules, invariants, no infrastructure
  AIDesignStudio.Application       ports, capability contracts
  AIDesignStudio.Infrastructure    EF Core, storage, provider adapters, parsers
  AIDesignStudio.Api               HTTP host
  AIDesignStudio.Worker            job dispatcher
backend/tests/
  AIDesignStudio.UnitTests         185 tests
  AIDesignStudio.IntegrationTests   57 tests (needs PostgreSQL)
  AIDesignStudio.ArchitectureTests  16 tests (boundary enforcement)
frontend/                          Next.js studio
packages/contracts/                shared JSON Schemas + TS types
docs/                              product, architecture, design, engineering, spikes
```

Dependencies point inward only: `SharedKernel ← Domain ← Application ←
Infrastructure ← hosts`.

---

## Where things will trip you up

1. **EF and domain-generated ids.** `Entity.Id` is a UUID v7 set in the
   constructor, and `AppDbContext.ConfigureDomainGeneratedKeys` declares it
   `ValueGenerated.Never`. Remove that and EF treats new aggregate children as
   `Modified`, emitting an `UPDATE` that matches no row. See DATA_MODEL.md §9.

2. **The Stitch CLI exits 0 on failure.** Read `success` from the JSON envelope,
   never the exit code.

3. **Projection warnings are normal output.** A non-empty
   `ThemeProjection.Warnings` is the expected case, not an error.

4. **`Unaddressed` is a blocker, not a default.** Feature coverage starts
   blocking handoff on purpose.

5. **Tests are xUnit v3 on Microsoft.Testing.Platform.** `dotnet test` needs the
   `test.runner` section in `global.json`, and test projects must be `Exe`.

6. **A screenshot gives `Visual` fidelity and nothing more.** Do not add OCR or
   layout inference. See ADR-0006.

---

## Reporting

When reporting work, state:

- **What was done**, with file paths.
- **Commands run**, with their actual output.
- **Test results**, with real counts.
- **What was verified** versus **what is assumed**.
- **Blockers**, and what unblocks them.
- **Anything incomplete**, and why.

If something does not work, say so with the output. A failing test reported as
passing is worse than a failing test.
