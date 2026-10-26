# Coding Standards

Enforced mechanically wherever possible. `TreatWarningsAsErrors` is on,
`EnforceCodeStyleInBuild` is on, and `dotnet format --verify-no-changes` gates
CI, so most of the rules below fail a build rather than a review.

---

## 1. Mechanical gates

| Gate                          | Tool                          | Enforced by                               |
| :---------------------------- | :---------------------------- | :---------------------------------------- |
| C# style and naming           | `.editorconfig` + Roslyn      | `dotnet format --verify-no-changes` in CI |
| Warnings are errors           | `Directory.Build.props`       | `dotnet build`                            |
| Nullable reference types      | `<Nullable>enable</Nullable>` | Compiler                                  |
| Module boundaries             | NetArchTest                   | `AIDesignStudio.ArchitectureTests`        |
| TS/TSX/JSON/MD/CSS formatting | Prettier                      | `npm run format:check`                    |
| TS correctness                | `tsc --noEmit` strict         | `npm run typecheck`                       |
| React and Next rules          | ESLint                        | `npm run lint`                            |
| Migration/model sync          | `dotnet-ef`                   | CI step                                   |

If a rule can be enforced by a tool, it is not in this document as prose.

---

## 2. C#

### Layering

Dependencies point inward: `SharedKernel ← Domain ← Application ←
Infrastructure ← hosts`. Violations fail an architecture test, not a review.

### Naming

| Kind                                | Convention                            |
| :---------------------------------- | :------------------------------------ |
| Private instance field              | `_camelCase`                          |
| Private `const` / `static readonly` | `PascalCase`                          |
| Async method                        | `…Async` suffix                       |
| Interface                           | `I` prefix                            |
| Test method                         | `A_sentence_describing_the_invariant` |

The first two are separate rules in `.editorconfig` on purpose — a single
`_camelCase` rule for all private fields flags every constant.

### Time

Always through `IClock`. Never `DateTimeOffset.UtcNow` in domain or application
code.

```csharp
// WRONG
ApprovedAt = DateTimeOffset.UtcNow;

// RIGHT
public void Approve(string approvedBy, IClock clock) => ApprovedAt = clock.UtcNow;
```

Lease expiry and retry backoff are time-dependent invariants; they must be
testable in milliseconds, not by sleeping.

### Errors

Domain invariant violations use `DomainException` with a **stable code**:

```csharp
throw new DomainException("contract.version_immutable",
    $"Version {VersionNumber} is {Status} and cannot be modified. Start a new draft instead.");
```

- The code is a public contract. Clients branch on it; renaming one is breaking.
- The message is for humans and may change.
- Format: `<module>.<snake_case_reason>`.

Provider failures use `ProviderException` carrying the provider's own code and a
retryability flag.

### Aggregates

```csharp
public sealed class DesignContract : AggregateRoot
{
    private readonly List<DesignContractVersion> _versions = [];
    private DesignContract() { }                       // EF Core

    public IReadOnlyList<DesignContractVersion> Versions => _versions;

    public static DesignContract Create(Guid projectId, IClock clock) { /* guards */ }
}
```

Rules, each enforced by an architecture test:

- Aggregate roots are `sealed`. An inheritable aggregate can have its invariant
  checks overridden.
- No public setters. Every state change goes through a method that can enforce
  the relevant invariant.
- Collections exposed as `IReadOnlyList<T>` over a private backing field.
- A private parameterless constructor for EF, commented as such.
- Static factory with guards, not a public constructor.
- Cross-module references **by id only**.

### Guard clauses first

```csharp
public static DesignProject Create(Guid organizationId, string name, ProjectKind kind, IClock clock)
{
    if (organizationId == Guid.Empty)
        throw new DomainException("project.organization_required", "A project must belong to an organization.");

    if (string.IsNullOrWhiteSpace(name))
        throw new DomainException("project.name_required", "A project must have a name.");

    return new DesignProject { /* ... */ };
}
```

### Throw rather than return a bool for a gate

```csharp
// RIGHT — the caller cannot ignore it
public void EnsureReadyForHandoff()
{
    var blocking = BlockingItems;
    if (blocking.Count > 0)
        throw new DomainException("inventory.uncovered_features", $"...{string.Join(", ", ...)}");
}
```

A `bool` return on a safety gate is an invitation to ignore it.

### Return false for a normal non-outcome

```csharp
// RIGHT — losing a claim race is normal operation, not a failure
public bool TryClaim(string leaseOwner, TimeSpan leaseDuration, IClock clock)
{
    if (State != JobState.Pending && !leaseExpired) return false;
    ...
}
```

The distinction from the previous rule: a gate the caller must respect throws; an
expected race outcome returns a value.

### Async

- `CancellationToken` as the last parameter, threaded through.
- No `async void` outside event handlers.
- No `.Result` or `.Wait()`.
- `await using` for `IAsyncDisposable`.

---

## 3. Comments

Comments explain **why**, never what. The diff shows what.

```csharp
// WRONG
// Set the status to Approved
Status = ContractVersionStatus.Approved;

// RIGHT
// A filtered unique index is the only thing that holds under concurrent
// approvals: two transactions can each read a state with no approved version.
v.HasIndex(x => x.ContractId)
    .HasDatabaseName("ux_contract_single_approved")
    .IsUnique()
    .HasFilter("status = 'Approved'");
```

Comment when:

- A non-obvious constraint drives the code (a provider quirk, a concurrency
  hazard).
- A simpler-looking alternative is wrong, and the next reader will try it.
- An external system behaves surprisingly — **say how it was verified**.
- An ADR governs the code. Cite it.

Do not comment obvious code, and do not restate a method name.

### XML docs

On public types and members in `SharedKernel`, `Domain` and `Application` where
the contract is non-obvious. State the invariant, not the mechanics:

```csharp
/// <summary>
/// Takes a lease on a pending job. Returns false rather than throwing when the
/// job is not claimable, because losing a claim race is normal operation when
/// several workers poll the same table.
/// </summary>
```

---

## 4. Persistence

- Table and column names in `snake_case` (via `EFCore.NamingConventions`).
- Enums persist as **text**, so data stays legible and reordering an enum cannot
  change stored meaning.
- Documents in `jsonb`, never text.
- All timestamps `timestamptz`.
- Aggregate child collections are **owned**, so they cannot be mutated
  independently of their aggregate.
- **No foreign keys across module boundaries.** Preserves module independence and
  future extraction.
- Approval uniqueness via **filtered unique index**. The aggregate alone cannot
  hold it.
- Computed properties over collections must be `Ignore`d, or EF treats them as
  navigations.
- `Entity.Id` is `ValueGenerated.Never`. See DATA_MODEL.md §9 before changing
  this — it is a correctness fix, not a preference.

Every migration must apply cleanly to an **empty** database. Verified on every
integration test run.

---

## 5. Provider adapters

- Implement only capabilities you have verified. An unverified capability is
  absent from `Capabilities`, not present and hopeful.
- `CheckReadinessAsync` never throws and is never billable.
- Guard every billable operation with `EnsureBillableAllowed`.
- Classify retryability from the provider's own error code.
- Credentials from the environment only — **never** process arguments
  (`/proc/<pid>/cmdline` is world-readable). Redact them from logs.
- Provider output is untrusted: parse it, never evaluate it.
- Timeout with a process-tree kill, so a hung CLI cannot hold a worker lease.

A type whose name contains a provider name may exist only in
`AIDesignStudio.Infrastructure.Providers`.

---

## 6. HTTP

- Minimal APIs grouped per resource, one file per group.
- RFC 9457 problem details for all errors.
- `DomainException.Code` becomes the problem type.
- Health endpoints never leak exception text — they are typically
  unauthenticated and exception text contains connection strings.
- Readiness reports credential **names**, never values.
- `/api/v1` for resources; health endpoints unversioned.

---

## 7. TypeScript

### Strictness

`strict`, plus `noUncheckedIndexedAccess`, `noUnusedLocals`,
`noUnusedParameters`, `verbatimModuleSyntax`.

### Types mirror the schemas

`packages/contracts/src/index.ts` mirrors the JSON Schemas, which are normative.
`contracts.test.ts` asserts they stay aligned, so drift fails a build.

### Errors

Distinguish failure modes the user acts on differently:

```typescript
export class ApiError extends Error { constructor(readonly status: number, ...) }
export class ApiUnreachableError extends Error { /* "Is it running?" */ }
```

"The API returned 404" and "the API is not running" need different messages;
during setup the second is overwhelmingly common.

### React

- Server components by default; `"use client"` only where interactivity needs it.
- Dynamic data inside `<Suspense>` so the shell prerenders. Next 16's Cache
  Components model requires this — `export const dynamic` is rejected.
- Degrade readably: when the API is unreachable, render a message, not a blank
  error page.
- Semantic HTML and ARIA. `role="alert"` for errors, `aria-labelledby` on
  regions.

---

## 8. Tests

Detail in [TEST_STRATEGY.md](TEST_STRATEGY.md). Standards:

- Test names are sentences:
  `Approval_is_blocked_when_the_contract_payload_hash_differs`.
- One invariant per test. A test asserting five things tells you little when it
  fails.
- Shouldly, not FluentAssertions (v8+ requires a paid commercial licence).
- Controllable clocks, never `Thread.Sleep`.
- Comment _why_ a test exists when it is not obvious:

```csharp
/// <summary>
/// The subtle case: the same contract version id, but different payload bytes.
/// Matching on id alone would let edited contract content through.
/// </summary>
```

- Use realistic fixtures. The font-family regex bug in SPIKE-04 was only caught
  because the fixture used a quoted font stack like real CSS, not a bare name.

---

## 9. Dependencies

Before adding one, ask whether ~200 lines would do instead. The durable job
queue is 200 lines rather than Hangfire because we need retry classification
driven by provider error codes and did not want a library's schema in our
database.

Check the licence. FluentAssertions was removed from this project because v8
moved to a paid commercial licence for non-OSS use.

Runtime dependencies must be clean of high/critical advisories
(`npm audit --omit=dev` gates CI). Dev-only advisories are tracked in
[SECURITY_REQUIREMENTS.md](SECURITY_REQUIREMENTS.md) rather than blocking the
build, because some have no upstream fix and would make the gate permanently
red.

A tool is a `devDependency`. `shadcn` was moved out of `dependencies` during M00
for exactly this reason — a code generator in the runtime graph pulls its whole
transitive tree into production.

---

## 10. Formatting

| Setting              | Value    |
| :------------------- | :------- |
| Line length          | 100      |
| C# indent            | 4 spaces |
| Everything else      | 2 spaces |
| Line endings         | LF       |
| Final newline        | Required |
| Quotes (TS)          | Double   |
| Semicolons (TS)      | Required |
| Trailing commas (TS) | All      |

EF-generated migrations are excluded from style enforcement — they are
regenerated by the tool, so hand-formatting them would be undone.
