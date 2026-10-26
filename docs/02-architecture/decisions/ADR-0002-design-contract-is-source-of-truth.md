# ADR-0002: The Design Contract is the source of truth, not provider output

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Product Owner, Solution Architect, UX Lead

## Context

The product orchestrates AI design tools. The tempting architecture is to treat
the provider's canvas as the system of record: the user generates screens in
Stitch, we store references, and the canvas holds the truth.

That architecture fails in four concrete ways:

1. **Provider models are lossier than a design system.** Verified against
   `@google/stitch` 0.11.1: Stitch's theme carries two font slots from a fixed
   list of 67 families, one seed colour plus four role overrides, a single
   `colorMode`, and a five-step roundness enum. A design system with a licensed
   brand font, semantic `danger`/`success` roles, elevation, or both light and
   dark modes cannot be represented. See ADR-0004 and SPIKE-01.
2. **Generation is non-deterministic.** The same prompt does not produce the
   same screen. There is nothing to diff and nothing to re-derive.
3. **Providers change without warning.** Stitch's Loop feature is documented as
   experimental and may change "without a major version bump".
4. **Approval needs something immutable.** "The team approved this" is
   meaningless if what was approved can change afterwards.

## Decision

The **Design Contract** is the normative artifact. It states what the product
must contain: information architecture, flows, per-screen obligations, required
states, accessibility target and content rules.

Provider output is **evidence that a contract was satisfied**. It is never the
contract.

Three mechanisms enforce this:

1. **Versioned, immutable approval.** A contract version is `Draft`, `Approved`
   or `Superseded`. Only a draft is mutable. At most one version is `Approved`,
   enforced both in `DesignContract` and by the filtered unique index
   `ux_contract_single_approved`. Approval records a SHA-256 of the payload.
2. **Provenance on every artifact.** `ScreenVersion` records the
   `ContractVersionId` and `ContractPayloadHash` it was produced against.
   Approving a screen version whose recorded hash differs from the contract
   currently in force fails with `screen.contract_drift`. Matching on id alone
   would be insufficient — the hash catches edited content under the same id.
3. **Provider names cannot leak.** `AIDesignStudio.Domain` and
   `AIDesignStudio.Application` contain no type whose name mentions a provider.
   Asserted by
   `ModuleBoundaryTests.No_provider_name_appears_in_the_domain_or_application_layer`.

## Consequences

**Accepted:**

- More work than storing a canvas reference: a contract must be authored and
  approved before generation is useful.
- A contract change invalidates screens produced against the old version. This
  is the intended behaviour, and it will be experienced as friction.
- We store and version documents a provider would have stored for us.

**Gained:**

- Approval means something and survives the provider.
- Switching or adding providers does not invalidate approved work.
- A provider's inability to express part of the design system is a reported
  warning (`ProjectionWarning`), not a silently wrong preview.

## Not chosen

- **Provider canvas as system of record** — fails for all four reasons above.
- **Deriving the design system from imported artifacts.** The HTML parser
  collects observed colours and fonts as `TokenCandidates`, deliberately named:
  one page's computed styles are evidence, not an approved token set. Promotion
  requires human approval.
