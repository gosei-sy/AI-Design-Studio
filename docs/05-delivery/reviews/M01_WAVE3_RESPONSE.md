# M01 Wave 3 — Response to independent review

**Review:** [M01_WAVE3_CODEX_REVIEW.md](M01_WAVE3_CODEX_REVIEW.md) · **Decision:** CHANGES REQUIRED (2 P1, 2 P2)
**Response date:** 2026-11-03 · **Author:** Lead Developer
**State:** all four findings corrected. **M01-03 awaits independent re-review and is not accepted. M01-04 has not started.**

The review was correct on all four findings, and correct about the one that
matters most: **the guard test was the defect, not just the missing filters.**

---

## W3-001 — P1 — Tenant isolation omitted project-owned aggregate roots

### What was wrong, in my words

The decision in ADR-0009 says every organization-scoped query is structurally
filtered. What I built filtered `DesignProject`, `AssetReference` and
`JobRecord` — the three roots that already carried `OrganizationId` from M00.
`DesignContract`, `DesignSystem`, `Screen` and `FeatureInventory` are
tenant-owned **through `ProjectId`**, had no filter, and could be read by
primary key across tenants.

The architecture guard could not see them, and that is the part worth dwelling
on. It **discovered its own population** — "find every type with an
`OrganizationId` property" — and then asserted that every member of that
population had a filter. It passed with four roots unprotected. A guard that
chooses what to check can only ever confirm the author's model of what exists.

This is the same error I have now made repeatedly in this milestone, in a new
costume: **I tested the mechanism I had built rather than the claim I was
making.** W2-001 configured a validation interval and never attached the
validator; W2-RR-002 counted failed MFA attempts and never consulted the
counter; here I filtered the entities I had thought of and let the test define
"all" as "the ones I thought of". The report then cited that test as evidence
for "every organization-scoped query path is guarded", which made my own
documentation false. The reviewer was right to call the acceptance claim false
rather than merely incomplete.

### Correction

**1. Every tenant-owned aggregate root carries the tenant key directly.**
`OrganizationId` added to `DesignContract`, `DesignSystem`, `Screen` and
`FeatureInventory`, with a required-at-construction check and a query filter —
seven roots, one uniform rule.

Of the two options the review offered, this is "remodel ownership so the tenant
key is directly and consistently filterable". The alternative — filtering
through a correlated subquery on `projects` — would put a subquery on every read
of every table, and would make the filter expression different for direct and
indirect roots, which is the asymmetry that caused the defect. Denormalizing is
safe because a project's organization is **immutable**: `DesignProject` exposes
no way to move one between tenants, so the two values cannot drift. It also
matches the shape `AssetReference` and `JobRecord` already had.

**2. The guard now partitions the whole EF model** instead of discovering a
subset. `OrganizationScopeGuardTests` takes every entity type in the model and
requires each to be exactly one of:

- organization-filtered,
- **owned** by something that is (an owned collection has no independent query
  path, so its isolation is its root's), or
- on a **by-value exemption list** with a stated reason — ten entries, all
  identity, key material, the tenant itself, or the membership table that
  produces the scope.

**Being unclassified is itself a failure.** A table added in M02 cannot be
missed, because the test does not have to know it exists. Five more assertions
sit on top: every aggregate root in the Domain carries the key, every key-bearing
root is filtered, the seven known tenant tables are named explicitly, each filter
actually reads the scope rather than being `x => true`, and the exemption list is
asserted by value so it cannot grow quietly.

**3. Real two-tenant integration coverage.**
`No_tenant_owned_aggregate_leaks_across_organizations` seeds a project **plus a
contract, design system, screen, feature inventory, asset and job** in each of
two organizations, then asserts from an organization-A-scoped context:

- every list returns only A's rows, for all seven kinds;
- **every by-primary-key lookup of a B row returns null** — the path the review
  named, and the one M01-05 handlers will use;
- every A row is still reachable, so the filter is not simply hiding everything;
- owned collections travel with their root;
- and a `count(*)` over `information_schema` asserts that exactly **seven** app
  tables carry `organization_id`, so a future aggregate cannot be left out of
  this test silently.

`A_closed_scope_sees_no_tenant_row_of_any_kind` does the same for the fail-closed
default across all seven.

**4. Regressed an indirect root, as asked.** `DesignContract`'s filter removed:

```
failed OrganizationScopeGuardTests.Every_entity_in_the_model_is_filtered_owned_or_explicitly_exempt
  unclassified should be empty but had 2 items and was
  ["DesignContract", "DesignContractVersion (owned by DesignContract)"]

failed OrganizationScopeGuardTests.Every_aggregate_root_carrying_the_tenant_key_is_filtered
  unguarded should be empty but had 1 item and was ["DesignContract"]

failed OrganizationScopeGuardTests.All_seven_known_tenant_tables_are_filtered
  filtered should contain "DesignContract" but was actually
  ["AssetReference", "DesignSystem", "JobRecord", "DesignProject", "FeatureInventory", "Screen"]

failed AuthorizationTests.No_tenant_owned_aggregate_leaks_across_organizations
  await asA.DesignContracts... should satisfy (x.OrganizationId == a) but [DesignContract] do not

failed AuthorizationTests.A_closed_scope_sees_no_tenant_row_of_any_kind
  await closed.DesignContracts.CountAsync(Ct) should be 0 but was 3
```

Three guard tests **and** both integration proofs fail. All pass again on
restore. Note the owned type appearing in the first failure — that is the
partition catching a consequence the old guard had no concept of.

**5. Documentation reconciled to what the tests prove.** ADR-0009 now carries
**amendment A1**, which records the defect, the corrected position and the
regression evidence. The "tenant isolation is structural" consequence is
annotated with "as first implemented this was true of three roots, not seven".
DATA_MODEL §9a lists all seven and explains the denormalization. The Wave 3
report's evidence mapping is corrected.

### Migration

`20261103061233_ScopeTenantRootsAndAuditRoleChanges` adds the column to four
tables **and backfills it from the owning project**:

```sql
UPDATE app.design_contracts c SET organization_id = p.organization_id
  FROM app.projects p WHERE p.id = c.project_id;
-- …and the same for design_systems, screens, feature_inventories
```

Without the backfill every pre-existing row would keep the all-zero default and
become invisible to every tenant. That is fail-closed, but it is also data
silently lost from the application's point of view — and the correct value is not
a guess, because each table already records the project it belongs to.

---

## W3-002 — P1 — Role changes retained the original operator's attribution

### What was wrong

`ApplyMembershipAsync` built the current operator's attribution string and then
called `active.ChangeRoles(roles, clock)`, which took no actor and discarded it.
So when operator A granted Member and operator B later granted **Approver**, the
stored membership — and the member roster the API returns — still named A.

A log line named B. The reviewer's sentence on this is the one I should have
written myself: _logs are not a substitute for an authoritative audit record
unless their durability, integrity, retention and correlation are explicitly
designed._ None of those were designed. Granting approval authority is the most
privileged change this system has, and its durable state said something false
about who made it.

### Correction

The review preferred an append-only audit record and set a minimum of
distinguishing initial provisioning from the most recent change without
overwriting history. Both are implemented, because they answer different
questions.

**`auth.organization_membership_events` — the append-only chain.** One entry per
provision, role change and revocation, each carrying the actor, the role
transition (`previous_roles` → `new_roles`), and the organization and user
**denormalized** so an auditor reading that table alone can say who changed
whose access, in which tenant, when.

It is an **owned collection of the membership aggregate**, so the entry is
written in the same `SaveChanges` as the state change it records: "a role changed
and nothing recorded it" is not a reachable state. It is deliberately **not**
`AutoInclude`d — the authorization path reads a membership on every request and
never needs its history.

**`roles_changed_by` / `roles_changed_at` on the membership row**, so the latest
answer is visible in `psql` and in the API without a join, and **without
overwriting `provisioned_by`**. A check constraint requires both or neither.

**`ChangeRoles` now requires an actor**, and refuses with
`membership.role_changer_required` rather than defaulting to anything.

**The member roster returns both**, which was the externally visible half of the
finding.

**The limit, stated rather than glossed:** the chain is append-only by
construction of the aggregate, not by database privilege. A writer with table
rights could still delete a row. **S-22 therefore stays partial**, and ADR-0009
A2 says so.

### Evidence

The scenario the review described, run through the real CLI against a fresh
database — two different operators, read back from PostgreSQL:

```
$ printf '%s' "$INITIAL_PASSWORD" | aids-provision provision --email alice@example.test \
    --organization "Acme Design" --role Member --actor "alice-granter@ops.test"
  roles         Member
  asserted by   alice-granter@ops.test via aids-provision@holyclaude

$ aids-provision provision --email alice@example.test --organization "Acme Design" \
    --role Member --role Approver --actor "bob-promoter@ops.test"
  roles         Member, Approver
  asserted by   bob-promoter@ops.test via aids-provision@holyclaude

$ psql -c "SELECT provisioned_by, roles_changed_by, roles FROM auth.organization_memberships"
provisioned_by   | alice-granter@ops.test via aids-provision@holyclaude
roles_changed_by | bob-promoter@ops.test via aids-provision@holyclaude
roles            | 3

$ psql -c "SELECT type, actor, previous_roles, new_roles FROM auth.organization_membership_events ORDER BY occurred_at"
Provisioned  | alice-granter@ops.test … | -  -> 1
RolesChanged | bob-promoter@ops.test …  | 1  -> 3
```

Automated: `The_persisted_audit_chain_names_the_operator_behind_every_change`
(three actors — provision, promote, revoke — read back from the database),
`The_member_roster_reports_who_last_changed_the_roles`,
`A_membership_that_was_never_changed_reports_no_role_change`,
`The_database_refuses_a_role_change_with_half_an_audit_record`, and in the unit
suite `Every_change_appends_an_attributed_audit_entry`,
`An_unattributed_role_change_is_refused`,
`The_audit_chain_cannot_be_mutated_through_the_property`.

---

## W3-003 — P2 — Contradictory Wave 2 and Wave 3 states

All six cited sites corrected, and then swept **by claim** rather than by
wording.

| Site                           | Was                                                                                                                | Now                                                                 |
| :----------------------------- | :----------------------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------ |
| `IMPLEMENTATION_PLAN.md:14`    | "Wave 2 is the authorized scope… M01-02 only"                                                                      | Wave 3 / M01-03 only, M01-02 accepted, M01-04 gated                 |
| `IMPLEMENTATION_PLAN.md:25`    | M01-03 "awaiting independent review"                                                                               | "Corrections applied — awaiting re-review", with the finding counts |
| `IMPLEMENTATION_PLAN.md:43`    | "436 passing tests" presented as current                                                                           | labelled "**at M00 closure**", pointing at the current total        |
| `IMPLEMENTATION_PLAN.md:179`   | M01-02 "first review returned CHANGES REQUIRED… not accepted"                                                      | Accepted 2026-11-03 with its two open findings named                |
| `IMPLEMENTATION_PLAN.md:529`   | A malformed 4-column row reading "⛔ Not started" in a 2-column table                                              | A proper constraint row recording the W3-001/W3-002 lessons         |
| `IMPLEMENTATION_STATUS.md:6`   | `Last updated: 2026-11-02`                                                                                         | 2026-11-03                                                          |
| `IMPLEMENTATION_STATUS.md:17`  | "Wave 2 is the authorized scope… nothing beyond it started"                                                        | Wave 3 / M01-03, M01-02 accepted, M01-04 not started                |
| `IMPLEMENTATION_STATUS.md:116` | One cell claiming both "M01-02 accepted" **and** "awaits acceptance", both "D-9 resolved" **and** "D-9 unresolved" | Rewritten as one dated Wave 3 state                                 |

The §116 cell is the clearest example of how this happened: I **prefixed** new
text to the old instead of replacing it, so the cell asserted a state and its
negation. The verbatim-string edits I used throughout this wave are what made
that possible, and a sweep by claim is what caught it.

Historical facts stayed historical: M00's 436-test closure snapshot (§4), the
dated changelog entries, and the record of the five CHANGES REQUIRED rounds are
all untouched. Current-state sections now also say the Wave 3 review returned
CHANGES REQUIRED — `README.md`, `SECURITY_REQUIREMENTS.md` S-20, and the plan's
wave table.

---

## W3-004 — P2 — The runbook put a literal password in shell history

The review is right, and the inconsistency was glaring once pointed at: the tool
refuses `--password` because argv is world-readable and lands in shell history,
and then the documented example was `echo 'Correct-Horse-9-Battery!' | …`, which
puts the literal in **the same two places** via the producer process.

**Corrected.** The README's provisioning section now leads with the
**interactive** form — the tool prompts and does not echo — and carries a callout
stating the boundary explicitly: **reading from stdin is only as safe as whatever
writes to it.** For automation it shows

```bash
read -rs INITIAL_PASSWORD
printf '%s' "$INITIAL_PASSWORD" | aids-provision provision …
unset INITIAL_PASSWORD
```

`printf` is a shell builtin, so no process carries the value, and the history
records only `"$INITIAL_PASSWORD"`. A secret file read with `< path` is equally
acceptable when its permissions and lifetime are managed. What is never
acceptable is the literal appearing in a command.

The same guidance is now in `aids-provision --help`, with the wrong form shown as
wrong — and that is **tested**, by
`ProvisioningCommandLineTests.Usage_warns_that_stdin_is_only_as_safe_as_its_producer`,
so the runbook cannot silently regress to the unsafe idiom. The Wave 3 report's
evidence section was rewritten the same way.

---

## Re-verification — actual output, 2026-11-03

```
$ dotnet build --configuration Release
  0 Warning(s), 0 Error(s)

$ dotnet test --configuration Release
  total: 455   failed: 0   succeeded: 455   skipped: 0

$ dotnet format --verify-no-changes            → exit 0

$ npm test
  frontend            14 passed (14)
  packages/contracts  164 passed (164)

$ npm run typecheck                            → exit 0
$ npm run lint                                 → exit 0
$ npm run format:check                         → All matched files use Prettier code style!
$ npm run build --workspace frontend           → compiled; Proxy (Middleware) emitted

$ npm run test:e2e --workspace frontend        (real Development API on :5080)
  7 passed (1.8m)

$ dotnet-ef migrations has-pending-model-changes …
  No changes have been made to the model since the last migration.

$ npm run audit:prod                           → found 0 vulnerabilities
$ dotnet list package --vulnerable --include-transitive
                                               → no vulnerable packages, 10/10 projects
$ git diff --check                             → exit 0
```

**Per suite:** 261 unit · 22 architecture · 172 integration · 14 frontend · 164
contracts = **633**, plus 7 E2E counted separately. **+11 backend tests** over
the submitted 444, and **0 skipped** — PostgreSQL was running for every run
quoted here.

### Fresh database, from empty

```
$ dotnet-ef database update   (disposable database)
Applying migration '20261020053356_InitialSchema'.
Applying migration '20261020094048_ReviewCorrections'.
Applying migration '20261026041140_AddIdentityAndDataProtection'.
Applying migration '20261103040427_AddOrganizationsAndMemberships'.
Applying migration '20261103061233_ScopeTenantRootsAndAuditRoleChanges'.
Done.

$ -- app tables carrying organization_id
assets · design_contracts · design_systems · feature_inventories · jobs · projects · screens   (7)

$ -- membership constraints
ck_..._active_audit · ck_..._revoked_audit · ck_..._role_change_audit · ck_..._roles
ck_..._status · fk_..._organization · fk_..._user · pk_...                                     (8)

$ -- auth.organization_membership_events
id · membership_id · organization_id · user_id · type · previous_roles · new_roles · actor · occurred_at
```

The database was dropped afterwards. **Docker Compose remains BLOCKED** (Docker
not installed) and **M01-11's CI run remains outstanding** — both separately
tracked, and neither touched by this response.

---

## Status

**M01-02 remains accepted** with its two recorded non-blocking findings (D-8,
MFA recovery codes), neither of which this response closes.

**M01-03 is corrected and submitted for independent re-review. It is not
accepted.** **M01-04 has not started**, and will not until M01-03 is accepted.

No Stitch call, no billable operation, no commit, push, PR or deployment, no new
infrastructure, and no Production configuration. `BillableOperationsEnabled`
remains `false`.
