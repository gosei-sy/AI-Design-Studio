# Architecture Decision Records

One file per decision. An ADR is immutable once accepted: to change a decision,
write a new ADR that supersedes it and update the status line of the old one.
Never edit an accepted ADR's decision in place — the record of what was decided,
and why, is the artifact.

| ADR                                                                                   | Decision                                                                      | Status                                                |
| :------------------------------------------------------------------------------------ | :---------------------------------------------------------------------------- | :---------------------------------------------------- |
| [0001](ADR-0001-modular-monolith.md)                                                  | Modular monolith, not microservices                                           | Accepted                                              |
| [0002](ADR-0002-design-contract-is-source-of-truth.md)                                | The Design Contract is the source of truth, not provider output               | Accepted                                              |
| [0003](ADR-0003-capability-based-provider-adapters.md)                                | Capability-based provider adapters                                            | Accepted                                              |
| [0004](ADR-0004-provider-theme-projection-is-lossy.md)                                | Provider theme projection is lossy, and every loss is reported                | Accepted                                              |
| [0005](ADR-0005-durable-jobs-in-postgresql.md)                                        | Durable background jobs in PostgreSQL                                         | Accepted                                              |
| [0006](ADR-0006-screenshot-import-is-visual-only.md)                                  | A screenshot import is a visual reference, not an editable design             | Accepted                                              |
| [0007](ADR-0007-functional-loss-requires-an-explicit-waiver.md)                       | Dropping a feature in a redesign requires an explicit waiver                  | Accepted                                              |
| [0008](ADR-0008-authentication-is-local-identity-with-cookie-sessions.md)             | Authentication is local Identity with a cookie/BFF session                    | Accepted — A1, A2 (A2 accepted 2026-11-02)            |
| [0009](ADR-0009-account-proofing-is-trusted-operator-provisioning.md)                 | Account proofing is trusted operator provisioning                             | Accepted — A1, A2 (2026-11-03), A3, A4 (2026-11-05)   |
| [0010](ADR-0010-payload-schemas-are-validated-in-process-and-pinned-to-the-studio.md) | Payload schemas are validated in-process and pinned to the studio's validator | **Proposed** (2026-11-19) — implemented, not accepted |

## Template

```markdown
# ADR-NNNN: <decision in one line>

- **Status:** Proposed | Accepted | Superseded by ADR-NNNN
- **Date:** YYYY-MM-DD
- **Deciders:** <roles>

## Context

What forces are in play? Include measured facts, not assumptions. If a claim
about an external system matters, say how it was verified.

## Decision

What we are doing, specifically enough to implement.

## Consequences

**Accepted:** what we are giving up.
**Gained:** what we get.

## Verification

Which tests hold this decision in place.

## Not chosen

The alternatives, and the concrete reason each was rejected.
```

## Which ADRs the code references

These are cited in source comments, so a reader who hits a surprising
constraint can find the reasoning:

- `JobRecord`, `PostgresJobQueue`, `JobDispatcher`, `AppDbContext` → ADR-0005
- `DesignContract`, `IDesignProvider` → ADR-0002
- `IDesignProvider`, `Capabilities`, `StitchDesignProvider` → ADR-0003
- `DesignSystem`, `StitchThemeProjector`, `IDesignSystemProjectionCapability` → ADR-0004
- `ScreenOrigin`, `ScreenshotDesignArtifactParser` → ADR-0006
- `FeatureInventory` → ADR-0007
- `Organization`, `OrganizationMembership`, `OrganizationContext`, `OrganizationScope`,
  `OperatorProvisioningService`, `OrganizationEndpoints`, `aids-provision` → ADR-0009
- `JsonSchemaValidator`, `DesignContractPayloadValidator`,
  `packages/contracts/conformance` → ADR-0010 (proposed)
