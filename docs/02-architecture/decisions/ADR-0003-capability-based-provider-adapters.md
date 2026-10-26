# ADR-0003: Capability-based provider adapters

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Solution Architect, Lead Developer

## Context

We integrate AI design providers whose surfaces differ substantially and move
between releases. The obvious abstraction is a single wide interface —
`IDesignProvider` with `GenerateScreen`, `GenerateVariants`, `ImportScreenshot`,
`ApplyDesignSystem`, `CaptureRoute` — implemented by each provider, throwing
`NotSupportedException` for whatever it cannot do.

That shape has a specific failure mode: "this provider cannot do X" becomes a
runtime exception discovered in production, usually inside a background job that
has already consumed a retry attempt and possibly spent money.

It is also empirically wrong about the surface. `@google/stitch` 0.11.1 exposes
`generate screen`, `generate variants`, `edit screen`, `capture`,
`upload screen`, `upload design`, `find`, `get`, `create`, `delete`,
`serve` and an experimental Loop workspace API — with `generate variants` and
Loop on different stability footings from the rest.

## Decision

Capabilities are separate interfaces, discovered at runtime.

```csharp
public interface IDesignProvider
{
    string ProviderId { get; }
    IReadOnlySet<Type> Capabilities { get; }
    TCapability? GetCapability<TCapability>() where TCapability : class, IProviderCapability;
    Task<ProviderReadiness> CheckReadinessAsync(CancellationToken ct);
}
```

Capability contracts live in `AIDesignStudio.Application.Providers`:
`IScreenGenerationCapability`, `IScreenVariantCapability`,
`IScreenImportCapability`, `IScreenRetrievalCapability`,
`IDesignSystemProjectionCapability`, `IRouteCaptureCapability`.

An adapter implements only what it actually supports, and declares only what has
been verified. `StitchDesignProvider` deliberately omits
`IScreenVariantCapability`: the CLI supports `generate variants`, but with no
credentials during M00 there is no verified response mapping, and claiming it
would be a lie. Tracked as M01-07.

Callers ask before acting:

```csharp
var generation = provider.GetCapability<IScreenGenerationCapability>()
    ?? throw new DomainException("provider.capability_unsupported", ...);
```

### Supporting rules

- **`CheckReadinessAsync` must never throw and must never be billable.** Stitch
  needs two independent credentials — a Google OAuth sign-in and a separate
  `STITCH_API_KEY` for canvas operations — and readiness reports them
  separately, because being signed in is not sufficient. It reports credential
  _names_ only, never values.
- **Every billable operation is guarded.** `Providers:Stitch:BillableOperationsEnabled`
  defaults to `false`, so a misconfigured local or CI run cannot spend against
  the account. Readiness and dry-run planning stay available.
- **Planning is free and therefore testable.** `PlanScreenAsync` uses the CLI's
  `--dry-run`, verified in SPIKE-01 to work with no credentials and no network.
  Request shaping is covered in CI at zero cost.
- **Errors carry the provider's own code.** `ProviderException.Code` holds e.g.
  `MISSING_CANVAS_CREDENTIALS`, and `IsRetryable` is derived from it, so retry
  policy branches on a contract rather than on message text.

## Consequences

**Accepted:**

- More interfaces than a single wide one.
- Callers must handle the unsupported case explicitly.
- `Capabilities` is hand-maintained, so it can drift from what the adapter
  really implements. Mitigated by
  `ModuleBoundaryTests.Capability_contracts_are_declared_in_the_application_layer`
  and by the adapter's capability tests.

**Gained:**

- An unsupported operation is a `null` from `GetCapability`, not an exception
  from inside a job.
- The UI can gate on capabilities instead of guessing
  (`GET /api/v1/providers`).
- "Verified" and "assumed" are structurally distinguishable: unverified
  capabilities are absent.

## Not chosen

- **Wide interface with `NotSupportedException`** — see above.
- **Capability flags enum.** A `[Flags]` enum would answer "can it?" but not
  give a typed handle to the operation, so every call site would still need a
  cast.
- **HTTP client against Stitch's REST API.** As of 0.11.1 the package's only
  library export is `defineIngest`; there is no published client for canvas
  operations. `stitch api <path>` exists but is undocumented per-endpoint. The
  CLI, where every command offers `--json` and `--schema`, is the supported
  programmatic surface. Revisit if Google publishes a client library.

---

## Amendments

### A1 — Two levels of verification, named (2026-10-20)

**Finding:** M00 review §9, discrepancy 7 (not a numbered CX finding). The term "declared
capability" conflated two different things. The spike index said unverified
capabilities are absent or isolated, while the adapter declared generation,
import, retrieval and capture — whose _success response mappings_ are entirely
unverified. Only variants were withheld. The threshold was inconsistent.

**Correction.** The decision is unchanged; the levels are now explicit:

| Level                 | Meaning                                                                                                                                                              |
| :-------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Request-verified**  | Request shaping is verified against the provider's own JSON Schema (`--schema`) and, where available, its dry-run planner. No live success response has been mapped. |
| **Response-verified** | A live call has been made and its response mapping is covered by a recorded fixture.                                                                                 |

**A capability is declared at request-verified.** The alternative — declaring
nothing until credentials exist — would make the registry useless for the whole
of M00 and most of M01, and would give the UI nothing to gate on.

Current position:

| Capability                          | Level                                                                    |
| :---------------------------------- | :----------------------------------------------------------------------- |
| `IScreenGenerationCapability`       | Request-verified (`--dry-run` evidence, SPIKE-01)                        |
| `IScreenImportCapability`           | Request-verified                                                         |
| `IScreenRetrievalCapability`        | Request-verified                                                         |
| `IDesignSystemProjectionCapability` | Request-verified (projection is local; no provider call)                 |
| `IRouteCaptureCapability`           | Request-verified                                                         |
| `IScreenVariantCapability`          | **Not declared** — not even request-verified; no dry-run evidence exists |

This is honest because `CheckReadinessAsync` reports usability separately, so a
caller can always distinguish "the adapter implements this" from "it can be
called in this environment right now". M01-01 promotes capabilities to
response-verified as live evidence is obtained, and
[docs/06-spikes/README.md](../../06-spikes/README.md) tracks which mappings
remain unverified.
