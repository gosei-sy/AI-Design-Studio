# User Stories

Grouped by workflow. Each story has testable acceptance criteria.

- **[M00 ✅]** — implemented and verified by an automated test
- **[M00 ◐]** — domain/invariant verified, no API or UI yet
- **[M01]** / **[M02]** / **[M03]** — planned

Story IDs are stable; the implementation plan references them.

---

## Epic A — Project setup

### A-1 — Start a new product design project

**[M00 ◐]**

> As a Product Owner, I want to create a project for a new product, so the team
> has one place where intent is recorded.

- Given an organization and a name, a project is created with kind
  `NewProduct`. ✅
- A project without an organization is rejected
  (`project.organization_required`). ✅
- A project without a name is rejected (`project.name_required`). ✅
- The kind cannot be changed afterwards — no API accepts it. ✅
- `POST /api/v1/projects` returns `201` with the project id. **[M01]**

### A-2 — Start a redesign project

**[M00 ◐]**

> As a Product Owner, I want to create a redesign project, so the system holds
> me to not losing functionality.

- A project created with kind `Redesign` requires a feature inventory before
  handoff. ✅ _(gate verified; wiring M02)_
- The UI makes the two workflows distinct from the first screen. **[M01]**

---

## Epic B — Design Contract

### B-1 — Author a contract draft

**[M00 ◐]**

> As a UX Lead, I want to draft the contract, so what the product must contain
> is written down before anything is generated.

- A draft starts at version 1; each subsequent draft increments. ✅
- Only one draft is open at a time (`contract.draft_already_open`). ✅
- A draft's payload can be edited freely. ✅
- A payload is rejected unless it validates against
  `design-contract.schema.json`. ✅ _(schema + 164 contract tests; API wiring M01)_
- A draft can be discarded, freeing its version number. ✅

### B-2 — Approve a contract

**[M00 ◐]**

> As a Product Owner, I want to approve the contract, so downstream work has a
> fixed target.

- Approval records approver, timestamp and a SHA-256 of the payload. ✅
- Approval requires an approver identity (`contract.approver_required`). ✅
- Approving with no draft open fails (`contract.no_draft`). ✅
- Exactly one version is `Approved` at any time — in the aggregate **and** in the
  database. ✅
- The previously approved version becomes `Superseded`. ✅

### B-3 — Trust that an approval sticks

**[M00 ✅]**

> As a reviewer, I want an approved contract to be immutable, so approval means
> something.

- Editing an `Approved` version fails (`contract.version_immutable`). ✅
- Editing a `Superseded` version fails the same way. ✅
- Changing an approved contract requires a new draft and a new approval. ✅

### B-4 — See contract history

**[M01]**

> As a Product Owner, I want to see every contract version and who approved it.

- `GET /api/v1/projects/{id}/contract/versions` returns all versions with
  status, approver and timestamp.
- Superseded payloads remain readable.

---

## Epic C — Design System

### C-1 — Author and approve a design system

**[M00 ◐]**

> As a UX Lead, I want to define and approve the token set.

- Versioning and approval behave exactly as B-1 to B-3. ✅
- The token model accepts tokens no provider can express — elevation, motion,
  breakpoints, component variants. ✅

### C-2 — Understand what a provider will actually apply

**[M00 ◐]**

> As a UX Lead, I want to know which of my tokens the provider cannot represent,
> **before** generating screens.

- Projecting onto a provider theme returns one warning per token dropped,
  approximated or snapped. ✅
- A font outside the provider's 67 families falls back and the warning names the
  requested family. ✅
- Declaring both light and dark warns that only one mode projects. ✅
- A radius off the five-step enum snaps and warns; ties resolve downward. ✅
- Each inexpressible section (`/elevation`, `/motion`, `/breakpoints`,
  `/components`, `/borders`) produces its own warning. ✅
- A semantic colour role with no provider slot is reported as dropped. ✅
- `POST /api/v1/projects/{id}/design-system/projections/{providerId}` returns the
  projection and its warnings, with no provider call and no cost. **[M01]**
- Warnings appear in the UI beside the design system. **[M02]**

---

## Epic D — Screens and generation

### D-1 — Generate a screen from the approved contract

**[M01]**

> As a UX Lead, I want to generate a screen for a contract obligation.

- Generation is enqueued as a durable job and returns `202` with a job id.
  _(queue verified ✅; handler M01)_
- A duplicate submission with the same idempotency key returns the existing job,
  never a second billable call. ✅
- The result becomes a new screen version with `ProviderGenerated` origin and
  the provider's artifact id. **[M01]**
- A provider failure is classified: credential errors are not retried, transient
  errors are. ✅

### D-2 — Preview the cost of generating before committing

**[M00 ✅]**

> As a Product Owner, I want to see what a generation request would do before it
> spends anything.

- A plan request returns the ordered provider operations with no provider call
  and no cost. ✅
- Planning works while billable operations are disabled. ✅
- The plan states that executing it **would** be billable. ✅
- A request targeting neither an existing nor a new project is rejected. ✅

### D-3 — Be protected from accidental spend

**[M00 ✅]**

> As an operator, I want billable calls off by default, so a misconfigured
> environment cannot spend money.

- Every billable method fails with `BILLABLE_OPERATIONS_DISABLED` while the flag
  is off. ✅
- The error is non-retryable, so a job does not burn attempts against it. ✅
- Readiness probes and planning remain available. ✅

### D-4 — Review a screen against its obligations

**[M00 ◐]**

> As a reviewer, I want to approve or reject a screen version.

- A version must pass through `InReview` before approval. ✅
- Rejection requires a reason (`screen.rejection_reason_required`). ✅
- A rejected version can be resubmitted. ✅
- Approval requires an approver identity. ✅
- Approving supersedes the previously approved version; exactly one stays
  approved. ✅
- Review shows the contract's obligations for that screen beside the artifact.
  **[M02]**

### D-5 — Not approve stale output

**[M00 ✅]**

> As a reviewer, I want approval blocked when a screen was produced against an
> older contract, so I cannot accidentally approve work against superseded
> intent.

- A version records the contract version id **and** payload hash it was produced
  against. ✅
- Approval fails with `screen.contract_drift` when either differs from the
  contract in force. ✅
- The same contract id with a **different payload hash** also fails — editing
  contract content cannot slip through. ✅
- A blocked approval leaves the version's state unchanged. ✅

### D-6 — Know which provider capabilities are available

**[M00 ✅]**

> As a UX Lead, I want the UI to offer only what the configured provider can
> actually do.

- `GET /api/v1/providers` lists each adapter and the capabilities it declares. ✅
- An unverified capability is absent from the list, not present-and-broken. ✅
- `GET /api/v1/providers/{id}/readiness` reports installed and authenticated
  separately. ✅
- Missing credentials are reported by **name only**. ✅
- A missing CLI is reported as data (`isInstalled: false`), not a server error. ✅
- An unknown provider returns `404`. ✅

### D-7 — Explore variants of a screen

**[M01]**

> As a UX Lead, I want several variants of a screen to choose between.

- Blocked: `IScreenVariantCapability` is deliberately not declared until the
  response mapping is verified against a live account. See M01-07.
- Count is bounded (provider supports 1–5).
- Each variant becomes its own screen version.

---

## Epic E — Redesign without functional loss

### E-1 — Catalogue what the existing application does

**[M00 ◐]**

> As a Product Owner, I want to record every current capability.

- A feature requires a stable key (`inventory.key_required`). ✅
- Duplicate keys are rejected case-insensitively
  (`inventory.duplicate_key`). ✅
- A newly catalogued feature starts `Unaddressed`. ✅
- Inventory can be imported from a list. **[M02]**

### E-2 — Map features to redesigned screens

**[M00 ◐]**

> As a UX Lead, I want to record which new screens cover which old features.

- Marking covered requires at least one real screen id
  (`inventory.covering_screen_required`). ✅
- An empty list or `Guid.Empty` is rejected. ✅
- A covered feature no longer blocks handoff. ✅
- Covering a previously waived feature clears the waiver. ✅

### E-3 — Drop a feature deliberately

**[M00 ✅]**

> As a Product Owner, I want to drop a feature **on the record**, so the
> decision is distinguishable from an oversight.

- Waiving requires a reason (`inventory.waiver_reason_required`). ✅
- Waiving requires a named owner (`inventory.waiver_owner_required`). ✅
- A rejected waiver leaves coverage unchanged — no partial state. ✅
- Waiving a covered feature clears its covering screens. ✅

### E-4 — Be stopped from shipping a lossy redesign

**[M00 ✅]**

> As a Product Owner, I want handoff blocked while any feature is undecided.

- Handoff fails with `inventory.uncovered_features` while any feature is
  `Unaddressed`. ✅
- The error names the blocking features. ✅
- An empty inventory does not block (nothing catalogued, nothing to lose). ✅
- `GET /api/v1/projects/{id}/handoff/readiness` surfaces this. **[M02]**

---

## Epic F — Import

### F-1 — Upload a screenshot and not be misled

**[M00 ✅]**

> As a Product Owner, I want to upload screenshots as reference, and I want the
> system to be honest about what that gives it.

- A raster upload yields `Visual` fidelity: no element tree, no text, no
  tokens. ✅
- The warning states the limit and recommends DOM capture instead. ✅
- The screen version records origin `ImportedScreenshot`, distinct from
  `CapturedMarkup`. ✅
- `POST /api/v1/projects/{id}/imports` always states fidelity in its
  response. **[M01]**

### F-2 — Capture a real page for higher fidelity

**[M00 ◐]**

> As a developer, I want to capture a running route, so the redesign starts from
> what the application actually renders.

- A DOM snapshot yields a semantic element tree with ARIA roles honoured. ✅
- Text is attached to the element that owns it. ✅
- Script and style elements are excluded. ✅
- Meaningless wrapper elements are collapsed. ✅
- Observed colours, fonts and sizes are collected as **candidates**, never as an
  approved design system. ✅
- An incoherent palette (>40 colours) is flagged for consolidation. ✅
- Malformed HTML degrades and warns; it never throws. ✅
- Route capture runs through the provider's `capture` command. **[M01]**

---

## Epic G — Operations

### G-1 — Know the environment is working

**[M00 ✅]**

> As a developer, I want to see at a glance whether the API, database and
> provider are usable.

- `/health/live` passes without the database. ✅
- `/health/ready` includes a real database check. ✅
- Health output never leaks a connection string or credential. ✅
- The studio page reports API health, provider capabilities and missing
  credentials, and degrades to a readable message when the API is down. ✅

### G-2 — Not lose work when a worker dies

**[M00 ✅]**

> As an operator, I want an in-flight job to be recovered automatically.

- A job orphaned by a crashed worker is reclaimed once its lease expires. ✅
- A live lease is respected — no double execution. ✅
- Reclaiming counts as a new attempt, so a job that kills its worker eventually
  dies rather than looping. ✅
- Two workers claiming concurrently take disjoint jobs. ✅
- Retryable failures reschedule with exponential backoff. ✅
- Non-retryable failures go straight to `Dead` with attempts remaining. ✅

### G-3 — Inspect and cancel a job

**[M01]**

> As an operator, I want to see a job's state and cancel it.

- `GET /api/v1/jobs/{id}` returns state, attempt and last error.
- Cancelling a terminal job fails (`job.already_terminal`). ✅ _(domain)_
- A cancelled job is never claimed again. ✅ _(domain)_

---

## Epic H — Handoff

### H-1 — Receive an unambiguous handoff

**[M03]**

> As a developer, I want a handoff that states what was approved and what is
> uncertain.

- States the approved contract version and design system version.
- Lists approved screens with provider provenance.
- **Lists every projection warning**, so known provider-vs-design-system
  differences are explicit.
- **States which screens were never verified against a live provider.**
- Lists every waived feature with its reason and owner.
