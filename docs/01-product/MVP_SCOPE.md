# MVP Scope

The MVP is **M00 through M03**. This document states what is in, what is out,
and — for each exclusion — why, so that a later "can we just add…" has an
answer on record.

---

## 1. MVP definition

> A team can author and approve a Design Contract and Design System, generate
> screens against them through Google Stitch, review those screens against their
> contract obligations, and hand off to developers — for both a new product and a
> redesign of an existing application, with no silent functional loss and no
> silently absorbed provider limitation.

The MVP is complete when a real team completes one new-product project and one
redesign project end to end without the team that built it intervening.

---

## 2. In scope

### Included — and already verified (M00)

| Capability                                                          | Evidence                            |
| :------------------------------------------------------------------ | :---------------------------------- |
| Design Contract versioning, approval, immutability, payload hashing | 14 unit tests + 4 integration tests |
| Design System versioning on the same model                          | Shared invariants + DB index        |
| Screen version provenance and the contract-drift guard              | 13 unit tests                       |
| Feature inventory and the no-functional-loss handoff gate           | 11 unit tests                       |
| Durable job queue: lease, crash recovery, backoff, idempotency      | 19 unit + 8 integration tests       |
| Capability-based provider abstraction                               | 16 architecture + 18 adapter tests  |
| Lossy theme projection with per-token warnings                      | 15 tests                            |
| Artifact normalization with honest fidelity levels                  | 13 tests                            |
| Local asset storage with path-traversal confinement                 | 11 tests                            |
| Health endpoints and provider diagnostics                           | 8 integration tests                 |
| Shared contract schemas                                             | 36 schema tests                     |

### Included — planned

| Milestone | Scope                                                                                                                                                                                         |
| :-------- | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **M01**   | Authentication and authorization; project/contract/design-system CRUD; generation job handler; free generation planning endpoint; job status                                                  |
| **M02**   | Screen review UI with obligations shown; projection warnings in the UI; feature inventory UI and handoff readiness; screenshot and DOM import UI; Azure Blob adapter; rate limiting and quota |
| **M03**   | Handoff package generation; variant exploration (gated on M01-07); project history views                                                                                                      |

### Platform scope

- Web application only. Responsive down to tablet width.
- Target device values for generated screens: `MOBILE`, `DESKTOP`, `TABLET`,
  `AGNOSTIC` — all four verified present in the provider's schema.
- One AI provider: Google Stitch.

---

## 3. Out of scope for the MVP

### Permanently out of scope

These are not deferrals. They are decisions.

| Excluded                                      | Why                                                                                                                                                                                                         |
| :-------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **In-app visual design editing or rendering** | The product orchestrates providers and owns the contract. Building a design canvas would compete with the providers it depends on, for no gain to the contract guarantee.                                   |
| **Application code generation**               | Handoff is a specification. Code generation is a different product with a different correctness model. (Assumption A-13 may challenge this.)                                                                |
| **OCR or layout inference on screenshots**    | Produces plausible, unverifiable structure — catastrophic for a system whose purpose is preventing silent functional loss (ADR-0006). Revisit only with a confidence model and explicit human verification. |
| **Real-time collaborative editing**           | Approval is a deliberate, sequential act. Concurrent editing of a draft adds CRDT complexity against a workflow that wants a single candidate under review.                                                 |
| **Replacing a provider's canvas**             | Providers do this better and it is not where our value is.                                                                                                                                                  |

### Deferred past the MVP

| Deferred                                                | Earliest       | Why not now                                                                                                                                         |
| :------------------------------------------------------ | :------------- | :-------------------------------------------------------------------------------------------------------------------------------------------------- |
| Second provider (Claude Design)                         | Post-MVP       | The capability abstraction makes a second adapter additive. One adapter verified beats two assumed. Escalates if A-08 resolves badly.               |
| Multi-approver sign-off and quorums                     | Post-MVP       | `ApprovedBy` is a single string (A-11). Adding approvals as an entity is contained but unjustified without evidence teams need it.                  |
| Design system conformance checking of generated screens | Post-MVP       | Needs reliable structural extraction from provider output, which depends on unverified retrieval (SPIKE-02). Reviewers compare manually until then. |
| Figma import or export                                  | Post-MVP       | No evidence of demand; large integration surface.                                                                                                   |
| Dark mode as a first-class variant                      | Post-MVP       | Stitch carries one `colorMode`, so this means a second provider design system and a UI for pairing them.                                            |
| Job retention and archival                              | M02 → post-MVP | Dead jobs accumulate. Not a correctness problem at MVP volume.                                                                                      |
| Audit log table                                         | Post-MVP       | Approval metadata lives on the aggregates. A dedicated append-only trail is a compliance feature without a stated compliance requirement.           |
| Prompt template library                                 | Post-MVP       | Reasonable, but it optimises generation quality before we have verified generation works at all.                                                    |
| Usage analytics and telemetry                           | Post-MVP       | Needs a privacy decision first.                                                                                                                     |
| Export to Storybook or similar                          | Post-MVP       | Depends on handoff format landing (M03).                                                                                                            |
| Mobile-native studio app                                | Not planned    | The studio is a desktop-class authoring tool.                                                                                                       |

---

## 4. Explicit non-goals for correctness

Stated so they are never mistaken for bugs:

1. **Generation is not reproducible.** The same prompt and contract can yield
   different screens. Versioning records what was produced, not how to reproduce
   it. Guaranteeing reproducibility is outside our control.
2. **Provider previews will differ from the approved design system.** Inherent
   (ADR-0004). The product's job is to report the difference precisely, not to
   eliminate it.
3. **The redesign guarantee is bounded by inventory quality.** Nothing
   _catalogued_ is lost silently. A feature nobody recorded is invisible to the
   system (A-04).
4. **A screenshot import gives visual fidelity only.** Not a limitation to fix;
   a fact to represent honestly (ADR-0006).

---

## 5. MVP acceptance criteria

The MVP is accepted when all of the following hold, each demonstrable:

| #   | Criterion                                                          | Milestone                    |
| :-- | :----------------------------------------------------------------- | :--------------------------- |
| 1   | A team can author, approve and version a Design Contract           | M01                          |
| 2   | An approved contract version is provably immutable                 | **M00 ✅**                   |
| 3   | A team can author and approve a Design System                      | M01                          |
| 4   | Projection warnings are visible before generation                  | M02                          |
| 5   | Screens generate through Stitch against the approved contract      | M01                          |
| 6   | A screen produced against a superseded contract cannot be approved | **M00 ✅**                   |
| 7   | Reviewers see contract obligations beside the artifact             | M02                          |
| 8   | A redesign project cannot reach handoff with undecided features    | **M00 ✅** (gate) / M02 (UI) |
| 9   | Dropping a feature requires a reason and a named owner             | **M00 ✅**                   |
| 10  | Screenshot import never implies structural fidelity                | **M00 ✅**                   |
| 11  | A worker crash loses no enqueued job                               | **M00 ✅**                   |
| 12  | A duplicate submission cannot double-charge the provider           | **M00 ✅**                   |
| 13  | Handoff states approved versions, warnings and unverified items    | M03                          |
| 14  | Provider capabilities are discovered, never assumed                | **M00 ✅**                   |
| 15  | No credential value is ever logged, returned or committed          | **M00 ✅**                   |

Nine of fifteen are already met and verified. The remaining six need API
surface and UI, not new architecture.

---

## 6. Blocking dependency

**A-08 — server-side Stitch credentials** is the one open item that can change
the MVP's shape rather than its schedule.

Criteria 5 and 13 depend on live generation, which depends on a credential that
works unattended from a background worker. Stitch's OAuth flow is interactive
(browser, loopback port), and we have not confirmed that `STITCH_API_KEY` can be
provisioned for server-side use.

If it cannot, generation needs per-user delegated tokens with consent and
refresh handling. That is an architecture change requiring re-approval, not an
implementation detail.

Resolving A-08 is **M01-01**, the first task of the next milestone, and
deliberately so.
