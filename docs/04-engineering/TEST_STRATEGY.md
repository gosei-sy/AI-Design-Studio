# Test Strategy

**Rule 4 of the development rules:** every important business invariant must
have an automated test. An invariant without a test is not implemented.

---

## 1. Current state

| Suite                              |   Tests | Needs      | Runtime    |
| :--------------------------------- | ------: | :--------- | :--------- |
| `AIDesignStudio.UnitTests`         |     262 | nothing    | ~7 s       |
| `AIDesignStudio.ArchitectureTests` |      25 | nothing    | ~7 s       |
| `AIDesignStudio.IntegrationTests`  |     190 | PostgreSQL | ~95 s      |
| `frontend` (Vitest)                |      14 | nothing    | ~43 s      |
| `packages/contracts` (Vitest)      |     164 | nothing    | ~6 s       |
| **Total**                          | **655** |            | **~2 min** |

Measured on 2026-11-05, after the M01 Wave 3 **re-review round 2** corrections.
The backend suite is `477` of that total (`dotnet test`), and the frontend `178`
(`npm test`).

**`MigrationUpgradeTests` is a shape this suite did not have before.** It starts
at a _named prior migration_, seeds the rows a real deployment would already
hold, then migrates forward — and in one case back again. Every Wave 3 finding
that survived the normal suite was invisible for one structural reason: the
fixture migrates to latest before any test runs, and the aggregates only ever
build internally consistent data. **A suite that can only create new data can
only verify new data.** Each of those tests provisions and drops its own
database.

Two further lessons from re-review round 2 are encoded there:

- **Migrating an already-current database proves nothing.** EF skips an applied
  migration, so an idempotency assertion written that way executes no SQL at all.
  The backfill statements live in `MembershipAuditBaselineSql` precisely so a
  test can run _them_ twice, and it asserts counts per membership and per type —
  a global total can hide one row appearing while another disappears.
- **The upgrade _window_ is a third state, not a midpoint.** A database at
  migration 5 already has the event table and is writing real audit entries; a
  test that seeds after the latest migration never visits that state, which is
  how a baseline came to be written over memberships that had observed history.

The suite is no longer the half-minute it was at M00: M01-03's integration tests
each boot a host and provision through the real service, which is what makes them
worth having and also what makes them cost a second each.

Every figure on this page is a **discovered** count, taken from the runners
rather than by counting `[Fact]` attributes — a `[Theory]` contributes one
attribute and several tests, and tallying attributes is how the per-file tables
below came to disagree with their own totals (M00-R4-003).

**223 of these were added across four rounds of independent review.** See
[../05-delivery/reviews/M00_CODEX_REVIEW_RESPONSE.md](../05-delivery/reviews/M00_CODEX_REVIEW_RESPONSE.md)
and the later rounds in the same directory for the finding-to-test mapping.

---

## 2. Layers, and what each is for

### Unit tests — invariants

No database, no filesystem beyond a temp directory, no network. A controllable
clock for anything time-dependent.

Counted by test class, not by file: `StitchAdapterTests.cs` holds two classes.

| Class                        |   Tests | Covers                                                                           |
| :--------------------------- | ------: | :------------------------------------------------------------------------------- |
| `JobRecordTests`             |      27 | Lease fencing, crash recovery, attempt exhaustion, retry classification, backoff |
| `StitchThemeProjectionTests` |      24 | Lossy projection and per-token warnings, including `/color/roles/*` paths        |
| `StitchDesignProviderTests`  |      20 | Capabilities, cost guard, retry, readiness totality                              |
| `ScreenApprovalTests`        |      20 | Provenance, contract-drift guard, atomic approval, rejection feedback            |
| `DesignSystemTests`          |      20 | **Design system lifecycle parity** (added for CX-007)                            |
| `DesignContractTests`        |      15 | Versioning, immutability, supersession, hashing, collection safety               |
| `FeatureInventoryTests`      |      14 | Coverage states, handoff gate, invariant-bypass guards                           |
| `AssetStorageTests`          |      14 | Round-trip, hashing, path confinement                                            |
| `ArtifactParsingTests`       |      12 | Normalization and fidelity limits                                                |
| `DesignProjectTests`         |      11 | Workflow kind validation and immutability                                        |
| `StitchEnvelopeParsingTests` |       5 | Both CLI response shapes, failure modes                                          |
| `JobDispatcherLeaseTests`    |       3 | Fail-closed renewal: heartbeat loss, heartbeat throwing, shutdown (R3-001B)      |
| **Total**                    | **185** |                                                                                  |

### Architecture tests — boundaries

A modular monolith stays modular only if the boundaries are checked. Written
down, they erode within a few milestones.

16 tests covering: layering, no persistence or web framework in the domain, no
provider name outside Infrastructure, adapter and capability placement,
cross-module coupling, sealed aggregates, no public setters, clock discipline,
and **no aggregate handing out a mutable collection**.

That last one was added for CX-004: the public-setter test did not catch an
`IReadOnlyList<T>` whose runtime value is a `List<T>`, which a caller can cast
back and mutate. It instantiates each aggregate and inspects what is actually
returned rather than the declared type. **Verified to fail on the regression** —
reintroducing `=> _versions` reports
`DesignContract.Versions returns a mutable List`.

These run in a second and catch a class of mistake no reviewer reliably catches.

### Integration tests — things only a real database can prove

A throwaway database per run, migrated from empty. That means **"migrations
apply cleanly to an empty database" is verified continuously**, not assumed.

| Class                  |  Tests | Covers                                                                                                  |
| :--------------------- | -----: | :------------------------------------------------------------------------------------------------------ |
| `DurableJobQueueTests` |     31 | `SKIP LOCKED` concurrency, crash recovery, backoff, idempotency, lease fencing, **database-time claim** |
| `PersistenceTests`     |     16 | Schema, round-trips, `jsonb`, **database-level approval uniqueness**                                    |
| `ApiEndpointTests`     |      8 | Real host over HTTP: health, providers, no credential leaks                                             |
| `JobDispatcherTests`   |      2 | Dispatcher against a real queue: lease loss cancels the handler, terminal failure                       |
| **Total**              | **57** |                                                                                                         |

What needs a real database and why:

| Behaviour               | Why in-memory cannot prove it                      |
| :---------------------- | :------------------------------------------------- |
| Single-approved-version | Filtered unique indexes do not exist in-memory     |
| Concurrent claiming     | `FOR UPDATE SKIP LOCKED` is PostgreSQL row locking |
| Crash recovery          | Needs lease state surviving a separate connection  |
| Idempotency under race  | Unique index enforcement                           |
| `jsonb` queryability    | The `->>` operator                                 |
| Migrations applying     | There is nothing to migrate in-memory              |

### End-to-end tests — the browser boundary

Seven Playwright specs, run against both real services with the studio over
HTTPS proxying the API. Three drive the full authentication flow: antiforgery
token, registration, login, **the session cookie actually landing in the
browser's jar** with `HttpOnly`/`Secure`/`SameSite=Strict`, an authenticated
call, logout, and antiforgery rejection.

They exist because M01-02 twice shipped an auth API that worked in `TestServer`
and failed in a browser. In-process tests do not enforce `Secure`, do not apply
schemeful `SameSite`, and have no cookie jar that can refuse. **Counted
separately from the 655** — they need two running processes, so they are not part
of `dotnet test` or `npm test`.

**M01-03 added no E2E spec.** Its guarantees are status codes, visible rows and
refused writes, all of which the integration suite asserts against the real host;
a browser adds nothing to any of them. The one browser-only property in this area
— that the session cookie is actually accepted by a cookie jar — was already
covered by `auth.spec.ts` in M01-02.

### Frontend tests

`api.test.ts` (8) covers request shaping, error classification, the
unreachable-API path, and that credential **names** are surfaced without values.
`system-status.test.tsx` (6) covers each render state including the degraded one.

### Contract schema tests

164 tests across three suites: `contracts.test.ts` (37),
`schema-alignment.test.ts` (29), `schema-coverage.test.ts` (98).

`contracts.test.ts` validates both JSON Schemas with Ajv against typed
fixtures. `schema-alignment.test.ts` pins declared key lists to the TypeScript
types at compile time and compares them to the schemas at runtime.
`schema-coverage.test.ts` walks every object node and **generates** a case per
rule — each required field omitted, each optional field omitted, and each
`additionalProperties` contract probed with samples derived from its own
schema, with a guard that every discovered map is either generated or
explicitly classified with a reason (M00-R3-002). The
new suite pins declared key lists to the TypeScript types at compile time,
compares them to the schemas at runtime, and exercises every required/optional
rule per property.

**Verified in both directions:** adding a property to a schema fails the test
run; removing one from TypeScript fails `tsc` with
`Type 'true' is not assignable to type 'never'`.

### End-to-end — ✅ executed locally, CI run outstanding

**7 Playwright specs, 7 passing**, run against both real services with the studio
over HTTPS proxying the API. `frontend/e2e/status.spec.ts` (4) and
`frontend/e2e/auth.spec.ts` (3).

**Historical note.** Through M00 this section read "⚠ unverified — 4 specs, not
executed", because the Chromium binary was absent. M01-11 installed the build
matching the lockfile and the suite has run since; see the § above for why the
browser specs exist at all. The browser install must use the **workspace**
Playwright binary, not `npx`, or it fetches a build the test run cannot find.

**Still outstanding for M01-11 acceptance:** the **CI** run. Passing locally is
not the acceptance criterion, and the specs are counted separately from the 655
because they need two running processes.

---

## 3. Deliberate choices

### Why not Testcontainers

Testcontainers needs a Docker daemon. Docker was unavailable in the M00
environment, and a harness that cannot run is worse than one with an explicit
prerequisite. `DatabaseFixture` reads `AIDS_TEST_CONNECTION` and falls back to a
local server; CI supplies a service container through the same variable.

### Why integration tests skip instead of fail

```csharp
Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
```

With the reason naming the host, port and the variable to set. A developer
without PostgreSQL gets **201** passing tests and a clear instruction, rather
than 122 confusing connection errors.

**Reporting rule:** if tests skip, say so. "339 passed" when 122 skipped is a
false report — the backend total is 339 only when PostgreSQL is reachable, and
any report of it must state the skip count. This has bitten twice during M01:
PostgreSQL stopped between runs and a suite reported every integration test as
skipped.

### Why Shouldly, not FluentAssertions

FluentAssertions v8 moved to a paid commercial licence for non-OSS use. It was
removed from this project during M00. Do not reintroduce it.

### Why xUnit v3

`Assert.Skip` for the graceful-degradation above, and
`TestContext.Current.CancellationToken` so tests honour cancellation. v3 runs on
Microsoft.Testing.Platform, which needs `global.json`'s `test.runner` section and
test projects marked `Exe`.

### Why controllable clocks

```csharp
clock.Advance(shortLease + TimeSpan.FromSeconds(5));
```

Lease expiry and exponential backoff are verified in milliseconds. The
alternative — sleeping — makes the suite slow and flaky, which makes it skipped.

---

## 4. What a good test looks like here

### It names the invariant

```csharp
public void An_approved_version_cannot_be_modified()
public void Approval_is_blocked_when_the_contract_payload_hash_differs()
public void A_job_orphaned_by_a_crashed_worker_is_reclaimed_after_its_lease_expires()
```

### It explains itself when the reason is subtle

```csharp
/// <summary>
/// The subtle case: the same contract version id, but different payload bytes.
/// Matching on id alone would let edited contract content through.
/// </summary>
```

### It asserts the error code, not the message

```csharp
Should.Throw<DomainException>(() => approved.UpdatePayload("...", _clock))
    .Code.ShouldBe("contract.version_immutable");
```

Codes are a contract; messages are not.

### It asserts that a rejected operation changed nothing

```csharp
ex.Code.ShouldBe("screen.contract_drift");
version.ReviewState.ShouldBe(ReviewState.InReview, "a blocked approval must not change state");
```

A guard that throws _and_ mutates is worse than no guard.

### It uses real recorded data for external systems

`StitchAdapterTests` embeds verbatim stdout from `@google/stitch` 0.11.1. The
adapter is tested against the provider's real behaviour without the provider
installed or authenticated.

### It uses realistic fixtures

The font-family regex bug found in SPIKE-04 only surfaced because the fixture
used `font-family: 'Plus Jakarta Sans', sans-serif` — real CSS — rather than a
bare name. A simplified fixture would have hidden it.

---

## 5. Invariants currently under test

| Invariant                                              | Where                                      | Level              |
| :----------------------------------------------------- | :----------------------------------------- | :----------------- |
| Approved/superseded contract versions are immutable    | `DesignContractTests`                      | Unit               |
| Exactly one approved contract version                  | `DesignContractTests` + `PersistenceTests` | Unit + **DB**      |
| Approval records approver, timestamp, payload hash     | `DesignContractTests`                      | Unit               |
| One draft at a time                                    | `DesignContractTests`                      | Unit               |
| Screen approval blocked on contract drift (id or hash) | `ScreenApprovalTests`                      | Unit               |
| A blocked approval does not change state               | `ScreenApprovalTests`                      | Unit               |
| Exactly one approved screen version                    | `ScreenApprovalTests` + index              | Unit + **DB**      |
| Review must pass through `InReview`                    | `ScreenApprovalTests`                      | Unit               |
| Rejection requires a reason                            | `ScreenApprovalTests`                      | Unit               |
| `Unaddressed` features block handoff                   | `FeatureInventoryTests`                    | Unit               |
| Coverage requires a real screen id                     | `FeatureInventoryTests`                    | Unit               |
| Waivers require reason **and** owner                   | `FeatureInventoryTests`                    | Unit               |
| Job lease and crash recovery                           | `JobRecordTests` + `DurableJobQueueTests`  | Unit + **DB**      |
| Reclaim counts as a new attempt                        | `JobRecordTests`                           | Unit               |
| Lost claim race does not consume an attempt            | `JobRecordTests`                           | Unit               |
| Non-retryable failures die immediately                 | both                                       | Unit + **DB**      |
| Idempotency prevents duplicate billable work           | both                                       | Unit + **DB**      |
| Concurrent workers take disjoint jobs                  | `DurableJobQueueTests`                     | **DB**             |
| Storage keys cannot escape their root                  | `AssetStorageTests`                        | Unit               |
| A screenshot yields `Visual` fidelity only             | `ArtifactParsingTests`                     | Unit               |
| Malformed artifacts degrade, never throw               | `ArtifactParsingTests`                     | Unit               |
| Every projection loss is reported                      | `StitchThemeProjectionTests`               | Unit               |
| Required provider theme fields always present          | `StitchThemeProjectionTests`               | Unit               |
| Billable operations refused when disabled              | `StitchAdapterTests`                       | Unit               |
| Credential errors are not retried                      | `StitchAdapterTests`                       | Unit               |
| CLI failure detected despite exit code 0               | `StitchEnvelopeParsingTests`               | Unit               |
| Unverified capabilities are not declared               | `StitchAdapterTests` + `ApiEndpointTests`  | Unit + Integration |
| Health output leaks no credentials                     | `ApiEndpointTests`                         | Integration        |
| Migrations apply to an empty database                  | `DatabaseFixture`                          | **DB**             |
| Module boundaries hold                                 | `ModuleBoundaryTests`                      | Architecture       |

---

## 6. Known gaps

Stated rather than implied.

| Gap                                     | Risk                                  | Plan                                                                                                                                                                                                  |
| :-------------------------------------- | :------------------------------------ | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **No E2E execution**                    | Browser-level regressions uncaught    | The independent review confirmed the specs are discovered and the Next server starts; only the Chromium binary was missing. CI installs it. Unblock locally in M01-11.                                |
| **No live provider test**               | Response mappings unverified          | SPIKE-02 test plan, gated on A-08                                                                                                                                                                     |
| No load or concurrency-at-scale testing | Unknown behaviour at high queue depth | Post-MVP; provider rate limits bind first                                                                                                                                                             |
| No mutation testing                     | Test quality unmeasured               | Consider Stryker.NET post-MVP. The two guard tests most likely to rot — the mutable-collection check and the schema-alignment check — were each verified by hand to fail on an introduced regression. |
| No accessibility testing                | WCAG 2.1 AA unverified                | With the first real UI (M01) — axe-core in Playwright                                                                                                                                                 |
| No coverage measurement                 | Unknown                               | Deliberate: coverage percentage is a weak proxy. The invariant table above is the real measure.                                                                                                       |
| Azure Blob adapter untested             | Does not exist                        | M02, against the same port tests                                                                                                                                                                      |

---

## 7. Commands

```bash
# Everything
dotnet test                                    # 339
npm test                                       # 14 + 164

# One suite
dotnet test --project backend/tests/AIDesignStudio.UnitTests/AIDesignStudio.UnitTests.csproj

# One test
dotnet test --project backend/tests/AIDesignStudio.UnitTests/AIDesignStudio.UnitTests.csproj \
  -- --filter-method '*contract_drift*'

# Per-test failure detail instead of a summary
dotnet test -p:TestingPlatformShowTestsFailure=true

# SQL for a persistence failure
AIDS_TEST_SQL_LOG=1 dotnet test --project backend/tests/AIDesignStudio.IntegrationTests/...

# E2E — 7/7 locally. Needs the API running; Playwright starts the HTTPS studio.
dotnet run --project backend/src/AIDesignStudio.Api &
npm run test:e2e --workspace frontend
```

---

## 8. Definition of done

A change is done when:

1. Its invariants have tests that fail if the invariant is removed.
2. `dotnet test` and `npm test` pass, with the real counts reported.
3. `dotnet format --verify-no-changes`, `npm run lint`, `npm run typecheck` and
   `npm run format:check` pass.
4. A model change has its migration, and
   `dotnet-ef migrations has-pending-model-changes` is clean.
5. New external-system behaviour is recorded in a spike document with how it was
   verified.
6. Anything unverified is labelled unverified.
