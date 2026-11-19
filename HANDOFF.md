# HANDOFF — AI Design Studio

**Written:** 2026-11-05 · **For:** a new session or agent picking this up cold
**Repository:** `/workspace/Gosei/AI-Design-Studio` · branch `main`

Read this, then [CLAUDE.md](CLAUDE.md) (the mandatory working rules), then
[docs/05-delivery/IMPLEMENTATION_STATUS.md](docs/05-delivery/IMPLEMENTATION_STATUS.md)
(current state with evidence).

> **Update — 2026-11-19.** Several statements below are superseded; the rest
> stands. Evidence is in IMPLEMENTATION_STATUS §9 (the 2026-11-19 rows).
>
> - **There are commits now.** The working tree was committed by the user
>   (`3f71156`, `79f9923`). Later work is on branch
>   `gosei-sy/study-handoff-codebase-review`, pushed with authorization; `main`
>   is unchanged.
> - **CI has run, for the first time:** run 44509571762, all three jobs green,
>   E2E `9 passed`. M01-11 has its evidence and **awaits acceptance**. Getting
>   there needed two fixes the workflow had always needed: an SDK version in
>   `global.json`, and a restore before `dotnet-ef` in the e2e job.
> - **Gates:** 477 backend (0 skipped), 19 frontend, 164 contracts, 9 E2E.
> - Next.js 16.4.6 (two advisories against 16.4.2); the lockfile now carries
>   win32 and macOS native bindings; `typecheck` runs `next typegen` first;
>   `.gitignore` anchors `/artifacts/`; two status-page defects fixed.
> - Development also happens on **Windows** now, so §8's environment facts are
>   Linux-specific. Windows E2E notes are in TEST_STRATEGY §7.
> - Unchanged: **M01-03 awaits re-acceptance; M01-04 has not started.**

> **⚠ There is another project in this workspace.** `/workspace/SocNhoEnglishV2`
> holds **M13.87**, household/pilot work, `pilot-web.yml`, IndexedDB queues.
> It is unrelated to this repository. Instructions mentioning households,
> transcripts, mastery, children or `M13_87_*` belong **there**, not here. This
> has already caused one misrouted instruction — check the milestone prefix
> (`M00`/`M01` = here) before acting.

---

## 1. What this product is

A tool helping teams design Web/Mobile applications consistently with AI tools
such as Google Stitch.

**The core principle, and the reason the architecture looks as it does:**

> The **Design Contract** and the **approved Design System** are the source of
> truth — **not** the output of any individual AI provider.

Everything downstream follows from that. Provider output is normalised into our
own model, projections onto a provider's theme are lossy and every loss is
reported, and a screenshot import is a _visual reference_, never an editable
design.

**Stack:** .NET 10 modular monolith + PostgreSQL + Next.js (App Router) studio.
Provider adapters behind capability interfaces.

---

## 2. Current state — precise, because the distinction matters here

| Item       | State                                                                                                      |
| :--------- | :--------------------------------------------------------------------------------------------------------- |
| **M00**    | ✅ Complete, independently re-accepted **APPROVED 2026-10-21** after 4 CHANGES REQUIRED rounds             |
| **M01-01** | 🚧 Offline prep done. Live execution blocked on **D-1/D-2**                                                |
| **M01-02** | ✅ **Accepted 2026-11-03** — APPROVED **with non-blocking findings** (6 review rounds)                     |
| **M01-03** | 🔬 **Implemented; three review rounds, nine findings, all corrected. AWAITING RE-ACCEPTANCE.**             |
| **M01-04** | ⛔ **NOT STARTED, and must not start until M01-03 is independently accepted**                              |
| M01-05…10  | ⛔ Not started                                                                                             |
| **M01-11** | ✅ Browser unblocked, E2E **7/7 locally**. Acceptance needs the **CI run** (needs a push — not authorized) |
| **M01-12** | ⛔ **BLOCKED** — Docker is not installed in this environment                                               |

**Nothing has ever been committed by an agent in this repo.** `git log` shows a
single commit `ef4fcb1 First Add`. All M00+M01 work is an uncommitted working
tree: ~50 modified tracked files, ~50 untracked paths. **Do not commit, push,
PR or deploy without explicit authorization** — that rule has held all session.

### Latest measured gates (2026-11-05, after re-review round 2)

```
dotnet test   → 477 passed, 0 failed, 0 skipped
                262 unit · 25 architecture · 190 integration
npm test      → 14 frontend · 164 contracts
E2E           → 7/7 (counted separately — needs two running processes)
build / dotnet format / typecheck / lint / format:check / frontend build → pass
has-pending-model-changes → none;  6 migrations apply from empty
audit:prod → 0 vulnerabilities;  dotnet --vulnerable → none across 10 projects
git diff --check → clean
```

M00's own closure figure was **436** and is preserved as a historical snapshot
in IMPLEMENTATION_STATUS §4. Do not update that section as M01 progresses.

---

## 3. Architecture decisions — all nine accepted

| ADR  | Decision                                                                |
| :--- | :---------------------------------------------------------------------- |
| 0001 | Modular monolith, not microservices                                     |
| 0002 | The Design Contract is the source of truth, not provider output         |
| 0003 | Capability-based provider adapters                                      |
| 0004 | Provider theme projection is lossy, and every loss is reported          |
| 0005 | Durable background jobs in PostgreSQL                                   |
| 0006 | A screenshot import is a visual reference, not an editable design       |
| 0007 | Dropping a feature in a redesign requires an explicit waiver            |
| 0008 | Authentication is local Identity with a cookie/BFF session — **A1, A2** |
| 0009 | Account proofing is trusted operator provisioning — **A1, A2, A3, A4**  |

**ADR-0008 amendments:** A1 = Wave 2 review corrections; A2 (accepted
2026-11-02) = the **same-origin BFF** is the accepted boundary, and D-8 is a
**fail-closed pre-production gate** rather than a bar on M01-02 acceptance.

**ADR-0009 amendments** (all corrections to defects, each naming the defect):

- **A1** — tenancy is transitive; every tenant-owned root carries
  `OrganizationId` directly and is query-filtered.
- **A2** — every role change persists its own actor; append-only audit chain.
- **A3.1** — a denormalized tenant key needs a **composite foreign key**, not a
  comment. Includes the **narrowly approved cross-module FK exception**.
- **A3.2** — `MigrationBaseline` records what the upgrade _observed_.
- **A4.1** — a baseline belongs only to a membership with **no audit events at
  all**. **A4.2** — rollback semantics.

### Rule 2 is real and has teeth

Never silently change an accepted ADR. Write a superseding ADR or an amendment,
get it approved, _then_ implement. I broke this once (M01-W2-R3: invented a D-8
gate rule in a status document) and the independent review caught it. The
withdrawal is still on the record in IMPLEMENTATION_STATUS §7 — leave it there.

---

## 4. Open decisions and risks

| ID      | Needs                                               | Blocks                                                                            |
| :------ | :-------------------------------------------------- | :-------------------------------------------------------------------------------- |
| **D-1** | Provision a Stitch account and API key              | M01-01, and therefore M01's main deliverable                                      |
| **D-2** | Authorize spend for live verification, with a cap   | M01-01                                                                            |
| **D-5** | Deployment target and secret store                  | M02. **Production also refuses to start without a Data Protection key protector** |
| **D-6** | Handoff package format (A-13)                       | M03                                                                               |
| **D-8** | A password-reset delivery transport                 | **Any Production deployment** — fail-closed, enforced in code                     |
| ~~D-3~~ | ✅ Two roles, Member + Approver (2026-10-21)        | —                                                                                 |
| ~~D-4~~ | ✅ Seven M00 ADRs accepted (2026-10-21)             | —                                                                                 |
| ~~D-7~~ | ✅ Option A, local Identity + cookie/BFF (ADR-0008) | —                                                                                 |
| ~~D-9~~ | ✅ Trusted operator provisioning (ADR-0009)         | —                                                                                 |

**Risks on the record** (IMPLEMENTATION_STATUS §8): R-4 `docker-compose.yml`
unverified; **R-5 no MFA recovery codes** (a non-blocking finding of M01-02's
acceptance — a user who loses their authenticator cannot self-recover, and
password reset cannot help because it does not clear the second factor);
**R-6 onboarding is manual**.

### Two consequences that surprise people

1. **Production cannot start at all today.** `IdentityStartupValidation` throws
   for `None` in Production, `DevelopmentLog` outside Development, and `Email`
   (unimplemented) anywhere. No transport value starts in Production until D-8
   is resolved. **That is the gate working, not a defect** — but whoever
   resolves D-8 must remove the `Email` throw in the same change.
2. **`aids-provision` cannot run in Production either**, because it starts the
   same host. So the first Production provisioning run is blocked behind D-8.

---

## 5. Repository layout

```
backend/src/
  AIDesignStudio.SharedKernel      Entity, AggregateRoot, IClock, DomainException, ProjectOwnership
  AIDesignStudio.Domain            8 modules, invariants, no infrastructure
  AIDesignStudio.Application       ports, capability contracts, ICurrentUser, IOrganizationContext
  AIDesignStudio.Infrastructure    EF Core, storage, provider adapters, Identity, organizations
  AIDesignStudio.Api               HTTP host
  AIDesignStudio.Worker            job dispatcher (system-scoped)
  AIDesignStudio.Provisioning      aids-provision — operator provisioning CLI (ADR-0009)
backend/tests/
  AIDesignStudio.UnitTests         262
  AIDesignStudio.IntegrationTests  190 (needs PostgreSQL)
  AIDesignStudio.ArchitectureTests  25 (boundary + organization-scope guards)
frontend/                          Next.js studio + Playwright E2E
packages/contracts/                shared JSON Schemas + TS types
docs/                              product, architecture, design, engineering, delivery, spikes
```

Dependencies point inward only: `SharedKernel ← Domain ← Application ←
Infrastructure ← hosts`. Enforced by NetArchTest.

**Domain modules:** Projects, DesignContracts, DesignSystems, Screens, Redesign,
Assets, Jobs, **Organizations**. Modules may not reference each other's types —
asserted, and the module list is hard-coded in `ModuleBoundaryTests`, so a new
module must be added there.

---

## 6. The M01-03 authorization model — read this before touching anything near it

Nine findings across three review rounds landed here. The shape is now:

| Question                           | Answered by                                                                | **Not** by                        |
| :--------------------------------- | :------------------------------------------------------------------------- | :-------------------------------- |
| Who is calling?                    | session cookie → `ICurrentUser.UserId`                                     | —                                 |
| What may they do?                  | `auth.organization_memberships` → `IOrganizationContext`                   | any claim, header or body field   |
| Which rows exist for this request? | a global EF query filter on all **seven** tenant roots, defaulting to none | a `Where` clause per query        |
| Is the tenant key honest?          | a **composite FK** to `projects (organization_id, id)`                     | a comment saying it cannot drift  |
| May they approve?                  | `organization.approver` policy / `OrganizationAccess.RequireApprover()`    | `RequireRole("Approver")`         |
| Who can grant access?              | `aids-provision`, a console tool                                           | no HTTP route, in any environment |

**Seven tenant-owned roots**, all carrying `OrganizationId` and all filtered:
`DesignProject`, `DesignContract`, `DesignSystem`, `Screen`, `FeatureInventory`,
`AssetReference`, `JobRecord`.

**Five of them are project-owned** and carry
`fk_<table>_project_tenant` → `projects (organization_id, id)`:
`design_contracts`, `design_systems`, `screens`, `feature_inventories`,
`assets`. They are created from a single `ProjectOwnership` value
(`DesignContract.Create(project.Ownership, clock)`), never from two loose Guids.

**Scope has three states and the default is closed:** `None` (nothing visible —
any context built without a scope), `Organization` (set from a resolved
membership), `System` (the worker and `aids-provision`, via
`AddInfrastructure(OrganizationScoping.System)` — a **required parameter**, so a
host cannot forget).

**Roles are NOT ASP.NET Core Identity roles.** Identity roles are global; a row
in `auth.user_roles` named `Approver` would make someone an approver of every
tenant. A test assigns exactly that and asserts the Approver endpoint still
returns 403. Do not reach for `RequireRole`.

**Two deliberate exemptions from filtering**, asserted by value in
`OrganizationScopeGuardTests`: `Organization` (its key _is_ the tenant id) and
`OrganizationMembership` (it _produces_ the scope, so filtering it is circular).

**Endpoints that exist:** `/api/v1/organizations/current`,
`/api/v1/organizations/{organizationId:guid}` (any other org → **404, never
403**, to avoid a tenant-existence oracle), `/api/v1/organizations/current/members`
(Approver only). Plus auth and health. **No project CRUD — that is M01-04.**

**`POST /api/v1/auth/register` is mapped in Development only.** Outside it:
anonymous → **401** (the fallback policy applies to unmatched routes too),
authenticated → **404** from the router.

---

## 7. Commands — run these and paste real output

```bash
dotnet build                                   # warnings are errors
dotnet test                                    # 477, 0 skipped
dotnet format --verify-no-changes
npm test                                       # 14 frontend + 164 contracts
npm run typecheck && npm run lint && npm run format:check
npm run build --workspace frontend

# E2E needs the API running first:
ASPNETCORE_URLS=http://localhost:5080 ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project backend/src/AIDesignStudio.Api &
npm run test:e2e --workspace frontend          # 7/7

dotnet-ef migrations has-pending-model-changes \
  --project backend/src/AIDesignStudio.Infrastructure \
  --startup-project backend/src/AIDesignStudio.Api

npm run audit:prod
dotnet list package --vulnerable --include-transitive
git diff --check
```

### Provisioning an account (no HTTP path exists)

```bash
export AIDS_ConnectionStrings__Default="Host=localhost;Port=5432;Database=aidesignstudio;Username=aids;Password=aids_dev_only"
export DOTNET_ENVIRONMENT=Development
dotnet run --project backend/src/AIDesignStudio.Provisioning -- \
  provision --email you@example.test --organization "Acme Design" \
  --role Member --role Approver --actor "$USER@$(hostname)"
```

**Never `echo 'secret' | aids-provision`.** It refuses `--password` because argv
is world-readable via `/proc/<pid>/cmdline` and lands in shell history — and
piping a literal puts it in the same two places. Use the interactive prompt, or
`read -rs PW; printf '%s' "$PW" | …; unset PW`. This is tested.

---

## 8. Environment facts that will cost you an hour each

1. **PostgreSQL stops on its own, repeatedly.** It died ~5 times this session.
   `pg_isready -h localhost -p 5432`; restart with
   `sudo -n pg_ctlcluster 15 main start` (plain `service postgresql start` warns
   about `/var/run/postgresql` permissions but works). **Integration tests
   report skips, not failures, when it is down — a skip is never a pass.**
   Re-run after restarting and report the 0-skipped figure.
2. **`dotnet-ef` is 10.0.12 against a 10.0.13 runtime.** It prints a warning and
   works. Add `$HOME/.dotnet/tools` to `PATH`.
3. **Docker is not installed.** `docker compose config` → exit 127. M01-12 is
   genuinely blocked; do not fake it.
4. **`gh` is not authenticated** in this container. Anything needing
   `gh run list` cannot be done here. Do not ask the user for a token.
5. **`AskUserQuestion` and `ExitPlanMode` both failed** with
   `AbortError: Stream closed` this session. If they fail again, state
   assumptions in text and proceed.
6. On drvfs mounts every `bin/` symlink can dangle; `npm ci` fixes it and leaves
   the lockfile byte-identical.
7. `npm test` can time out when run in the same command as other long work —
   run it alone.

---

## 9. Where things will trip you up (hard-won)

1. **EF and domain-generated ids.** `Entity.Id` is a UUID v7 set in the
   constructor and `ConfigureDomainGeneratedKeys` declares it
   `ValueGenerated.Never` — **restricted to `Entity` subclasses**, because
   `IdentityUserClaim.Id` is an int identity column and declaring it
   never-generated makes every insert write 0 and collide.
2. **The Stitch CLI exits 0 on failure.** Read `success` from the JSON envelope.
3. **Projection warnings are normal output.** A non-empty `ThemeProjection.Warnings`
   is the expected case.
4. **`Unaddressed` feature coverage is a blocker, not a default.**
5. **Tests are xUnit v3 on Microsoft.Testing.Platform.** `dotnet test` needs the
   `test.runner` section in `global.json`; test projects must be `Exe`.
6. **A screenshot gives `Visual` fidelity and nothing more.** No OCR, no layout
   inference (ADR-0006).
7. **Schemeful SameSite.** An HTTP page and an HTTPS API are different _sites_,
   so a `SameSite=Strict` cookie is never sent. This broke the documented
   browser path twice. The studio proxies `/api/*` server-side; `frontend/src/proxy.ts`
   injects `X-Forwarded-Proto`; the API trusts loopback only.
8. **Identity has four cookie schemes.** Use `AddIdentityCookies()`; rejecting a
   principal signs out across all four and throws if only one is registered.
9. **`[property: Required]`, not `[Required]`,** on positional record
   parameters — an attribute there targets the _parameter_, and the
   DataAnnotations validator reads _properties_.
10. **The job claim is raw SQL and is not query-filtered.** Correct (the
    dispatcher must see every tenant), but it means the filter is not what
    protects that path — running only in the worker is.
11. **A forgotten scope makes a worker silently idle, not loud.** This cost two
    timed-out tests. Hence the required `OrganizationScoping` parameter.
12. **`MembershipAuditBaselineSql` must never be edited.** An applied migration
    has to keep meaning what it meant when applied. New behaviour = new
    migration.

---

## 10. Review history, and the lesson that keeps recurring

Fourteen independent review rounds so far. M00: 4 × CHANGES REQUIRED then
APPROVED. M01 Wave 1: 4 rounds then approved. M01-02: 6 rounds then APPROVED
with non-blocking findings. M01-03: 3 rounds, nine findings, all corrected,
**not yet accepted**.

**Every single P1 in this project has been the same error in a new costume:
I verified the mechanism I had built instead of the claim I was making.**

| Finding    | The shape of it                                                                    |
| :--------- | :--------------------------------------------------------------------------------- |
| W2-001     | Configured a validation interval; never attached the validator                     |
| W2-RR-002  | Counted failed MFA attempts; never consulted the counter                           |
| W3-001     | Filtered the entities I had thought of, and let the guard define "all" as "those"  |
| W3-RR-001  | Wrote "the keys cannot drift" — true of mutation, false of creation                |
| W3-RR2-001 | A predicate that asked "has no baseline yet" instead of "predates the chain"       |
| W3-RR2-002 | An idempotency test that re-migrated an already-current database, executing no SQL |

Derived rules, earned the hard way:

- **A guard that chooses its own population can only confirm the author's model
  of what exists.** `OrganizationScopeGuardTests` therefore _partitions the whole
  EF model_ — being unclassified is itself a failure.
- **A suite that can only create new data can only verify new data.** Hence
  `MigrationUpgradeTests`, which starts at a _named prior migration_. The upgrade
  **window** is a third state, not a midpoint.
- **A probe asserting one constraint must violate only that constraint.** Twice
  a malformed row tripped `23502`/`23505` before the FK was consulted.
- **A correct guard fed an inconsistent operand rejects valid writes as
  confidently as invalid ones** (four rounds of lease fencing in M00).
- **Sweep documentation by _claim_, not by wording.** Grepping the old phrasing
  only finds instances that used it.
- **Never present manual evidence as an automated test.** That reads as covered
  and is worse than no test.

---

## 11. What to do next

1. **Wait for M01-03 re-acceptance.** It is submitted; evidence is
   [M01_WAVE3_R2_RESPONSE.md](docs/05-delivery/reviews/M01_WAVE3_R2_RESPONSE.md).
   **Do not start M01-04.**
2. If the re-review returns findings again: reproduce each one _before_ changing
   anything, correct it, re-run the whole gate with **zero skips**, write a
   response document naming the defect in your own words, and record any
   decision as an ADR amendment rather than a quiet edit.
3. Unblockable only by the Product Owner: **D-8** (then Production and
   `aids-provision` can run at all), **D-1/D-2** (then M01-01 can execute live),
   **D-5**, and **MFA recovery codes** (R-5).
4. **M01-11** needs a push to run CI — not authorized. **M01-12** needs Docker.

### Standing prohibitions, in force all session

No Stitch or provider call. No spend. `Providers:Stitch:BillableOperationsEnabled`
stays **`false`**. No new infrastructure or external services (no IdP, mail
service, Redis, broker, second database). No commit, push, PR, deployment or
Production configuration without explicit authorization. No weakening of
existing authentication, antiforgery, account-enumeration, billable-operation,
domain, storage, job-lease or tenant-isolation guards. Preserve all nine ADRs
and their amendments. **Never log or return a credential**, and never pass one as
a process argument.

---

## 12. Document map

| Need                         | Read                                                          |
| :--------------------------- | :------------------------------------------------------------ |
| Working rules (mandatory)    | `CLAUDE.md`, `AGENTS.md`                                      |
| Current state + evidence     | `docs/05-delivery/IMPLEMENTATION_STATUS.md`                   |
| Milestone tasks and gates    | `IMPLEMENTATION_PLAN.md`                                      |
| Why a decision was made      | `docs/02-architecture/decisions/` (+ `README.md` index)       |
| Schema, tenancy, audit chain | `docs/02-architecture/DATA_MODEL.md` (§9a, §10a, §10b)        |
| HTTP surface                 | `docs/02-architecture/API_CONTRACTS.md` (§3a = organizations) |
| Security requirement status  | `docs/04-engineering/SECURITY_REQUIREMENTS.md` (S-19…S-22)    |
| Test shapes and counts       | `docs/04-engineering/TEST_STRATEGY.md`                        |
| Blockers and assumptions     | `docs/01-product/OPEN_ASSUMPTIONS.md`                         |
| Verified vs assumed spikes   | `docs/06-spikes/README.md`                                    |
| Wave reports                 | `docs/05-delivery/M01_WAVE{1,2,3}_REPORT.md`                  |
| Every review + response      | `docs/05-delivery/reviews/`                                   |

**Reporting standard for this project:** state what was done with file paths,
commands with their **actual** output, real test counts, what was **verified**
versus **assumed**, blockers and what unblocks them, and anything incomplete
with the reason. A failing test reported as passing is worse than a failing test.
