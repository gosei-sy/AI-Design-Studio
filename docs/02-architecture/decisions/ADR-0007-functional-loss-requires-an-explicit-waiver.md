# ADR-0007: Dropping a feature in a redesign requires an explicit waiver

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Product Owner, Solution Architect

## Context

The redesign workflow's stated promise is "a consistent redesign without
unintentionally losing functionality". The word doing the work is
_unintentionally_. Deliberately dropping a feature is legitimate product
decision-making; losing one because nobody noticed is a defect.

The two are indistinguishable at the end of a redesign unless the system makes
them different **while the work is happening**. A checklist in a document does
not: the omission and the decision both look like absence.

## Decision

The `FeatureInventory` aggregate catalogues what the existing application does
today. Each item has exactly one coverage state:

| State         | Meaning                                     | Blocks handoff |
| :------------ | :------------------------------------------ | :------------- |
| `Unaddressed` | Catalogued, not yet addressed               | **Yes**        |
| `Covered`     | Mapped to at least one redesigned screen    | No             |
| `Waived`      | Deliberately dropped, with reason and owner | No             |

Three rules make silence impossible to mistake for a decision:

1. **`Unaddressed` is the default and it blocks.**
   `EnsureReadyForHandoff()` throws `inventory.uncovered_features` and names the
   blocking items. It throws rather than returning a bool so a caller cannot
   ignore the result.
2. **`Covered` must point at something real.** `MarkCovered` requires at least
   one non-empty screen id (`inventory.covering_screen_required`). Otherwise the
   gate could be satisfied by asserting coverage that does not exist.
3. **`Waived` requires a reason _and_ an accountable owner.**
   `inventory.waiver_reason_required` and `inventory.waiver_owner_required`. An
   unattributed waiver is indistinguishable from an accident, which would defeat
   the whole mechanism.

Transitions are symmetrical and clean: covering a waived feature clears the
waiver; waiving a covered feature clears its covering screens. A feature is
never half in two states.

## Consequences

**Accepted:**

- A redesign cannot reach handoff until every catalogued feature has been
  decided. This is friction, and it is the point.
- Inventory quality bounds the guarantee. The system guarantees that nothing
  **catalogued** is lost silently; it cannot know about a feature nobody
  recorded. This limit is stated in the PRD rather than implied away.
- Waivers accumulate an audit trail that someone has to read.

**Gained:**

- "We decided to drop fax export" and "we forgot fax export" are different
  states in the database, with different consequences.
- The handoff gate is mechanical, so it cannot be skipped under deadline
  pressure without someone recording a waiver and their name.

## Verification

`backend/tests/AIDesignStudio.UnitTests/FeatureInventoryTests.cs`, in
particular:

- `A_newly_catalogued_feature_starts_unaddressed_and_blocks_handoff`
- `MarkCovered_requires_at_least_one_real_screen`
- `Waive_requires_a_reason_and_an_accountable_owner`
- `The_handoff_gate_names_the_blocking_features`

## Not chosen

- **Warn instead of block.** Warnings are ignored under deadline pressure, which
  is exactly when functional loss happens.
- **Infer coverage automatically** by matching screen keys to feature keys. A
  key match does not establish that the capability survived, and an automatic
  pass would be worse than no gate at all because it would look like one.
- **Allow an anonymous waiver.** Removes the accountability that makes a waiver
  different from an omission.
