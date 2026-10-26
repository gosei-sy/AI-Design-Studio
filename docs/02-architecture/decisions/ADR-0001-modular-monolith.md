# ADR-0001: Modular monolith, not microservices

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Solution Architect, Lead Developer

## Context

AI Design Studio has seven identifiable domains (projects, design contracts,
design systems, screens, redesign inventory, assets, jobs). The team is small,
the product has no users yet, and the load profile is low-volume, long-latency:
requests spend seconds to minutes waiting on an external AI provider, not
milliseconds in our own code.

The question is where the module boundaries live — in process, or over a network.

## Decision

A single deployable backend with explicit in-process module boundaries.

Two host processes share one codebase and one database:

- `AIDesignStudio.Api` — HTTP surface
- `AIDesignStudio.Worker` — background job execution

These are separate processes for operational reasons only: provider calls are
slow and must not occupy request threads, and the two scale on different
signals. They are not separate services. They share a schema, a migration
history, and a transaction boundary.

Boundaries are enforced by layering (`SharedKernel` → `Domain` → `Application` →
`Infrastructure` → hosts) and by assertions in
`AIDesignStudio.ArchitectureTests`, not by convention.

## Rationale

The thing that makes this product correct is that a screen cannot be approved
against a contract version that is no longer in force
(`screen.contract_drift`), and that enqueueing provider work is atomic with the
state change that requested it. Both are transactional invariants spanning what
would otherwise be separate services. Distributing them would replace a
transaction with a saga, for no gain in throughput we need.

Microservices would also give us a per-service deployment story, which we do not
need, in exchange for network partitions, eventual consistency, and distributed
tracing, which we would have to build.

## Consequences

**Accepted:**

- One database, so a module can reach another's tables if someone writes the
  query. Mitigated by the architecture tests, which fail the build when a domain
  module references another module's types.
- The whole backend deploys together. A change to the redesign module
  redeploys the API.
- Horizontal scaling is per-process, not per-module.

**Gained:**

- Approval and enqueue are single transactions.
- Refactoring across module boundaries is a compiler-checked operation.
- Local development needs PostgreSQL and nothing else.

## Revisit when

- A single module's resource profile diverges enough that co-deployment is
  wasteful (most likely candidate: artifact parsing, if it becomes CPU-heavy).
- Independent deployment cadence becomes a real constraint, i.e. more than one
  team ships on its own schedule.

Until then, extracting a service is a cost with no corresponding benefit. The
module boundaries are drawn so that extraction stays possible: cross-module
references are by aggregate id only, which is the precondition for splitting
later without a rewrite.

## Not chosen

- **Microservices** — see above.
- **Separate database per module** — gives up the atomic invariants that are the
  point of the product.
- **Event bus between modules** — adds asynchrony between components that need
  to be consistent, and a broker to operate. The durable job queue (ADR-0005)
  already covers the one genuinely asynchronous boundary.
