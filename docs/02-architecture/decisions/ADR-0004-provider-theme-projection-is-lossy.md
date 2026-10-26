# ADR-0004: Provider theme projection is lossy, and every loss is reported

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Solution Architect, UX Lead

## Context

Our Design System must reach the provider so generated screens look like our
product. The question is which direction is authoritative, and what happens to
anything that does not fit.

The provider constraint set was read from the provider itself, not from its
documentation:

```
stitch edit design-system --schema    # @google/stitch 0.11.1
```

Stitch's `theme` object accepts exactly:

| Field                                                                    | Type                                     | Notes                        |
| :----------------------------------------------------------------------- | :--------------------------------------- | :--------------------------- |
| `bodyFont`, `headlineFont`, `labelFont`                                  | enum, 67 values                          | Named families only          |
| `colorMode`                                                              | enum: `LIGHT`, `DARK`                    | **One value**                |
| `customColor`                                                            | hex string                               | Single seed colour           |
| `colorVariant`                                                           | enum, 9 values                           | Tonal algorithm              |
| `overridePrimaryColor` … `overrideTertiaryColor`, `overrideNeutralColor` | hex                                      | Four role slots              |
| `roundness`                                                              | enum: `ROUND_TWO/FOUR/EIGHT/TWELVE/FULL` | Five steps                   |
| `spacing`                                                                | free-form string map                     | Carries across intact        |
| `typography`                                                             | free-form map of CSS properties          | Carries across intact        |
| `designMd`                                                               | markdown string                          | Advisory prose, not enforced |

Required: `bodyFont`, `colorMode`, `customColor`, `headlineFont`, `roundness`.

A real design system is strictly richer. It has a brand font that is probably
not among the 67. It has `danger` and `success` roles with no slot. It usually
defines both light and dark. It has elevation, motion, breakpoints and
component variants that have no representation at all.

## Decision

Projection is **one-directional and lossy, and every loss is enumerated**.

- Our design system is the source. We never import a provider theme.
- `IDesignSystemProjectionCapability.ProjectDesignSystem` returns a
  `ThemeProjection` containing the provider-native payload **and** a list of
  `ProjectionWarning`.
- A non-empty warning list is the **normal case**, not an error.
- Warnings surface to reviewers. They are never swallowed.

A `ProjectionWarning` states the token path, what the design system asked for,
what the provider will actually receive (`null` when dropped), and why.

### Projection rules, as implemented

| Design system input                                           | Projection                          | Warning                                                |
| :------------------------------------------------------------ | :---------------------------------- | :----------------------------------------------------- |
| Font in the 67                                                | Exact token                         | No                                                     |
| Font outside the 67                                           | Falls back to `INTER`               | Yes — brand font will not appear in the preview        |
| `color.primary`                                               | `customColor`                       | No                                                     |
| `color.secondary/tertiary/neutral`                            | Override slots                      | No                                                     |
| Any other colour role                                         | Dropped                             | Yes — studio-enforced only                             |
| `color.modes: [light]`                                        | `colorMode: LIGHT`                  | No                                                     |
| `color.modes: [light, dark]`                                  | First mode only                     | Yes — second mode needs its own provider design system |
| `shape.radiusBase` on a step                                  | Exact token                         | No                                                     |
| `shape.radiusBase` off a step                                 | Nearest step, ties resolve **down** | Yes                                                    |
| `spacing`, `typography.scale`                                 | Verbatim                            | No                                                     |
| `elevation`, `motion`, `breakpoints`, `borders`, `components` | Dropped                             | Yes, one per section                                   |

Ties round down because under-rounding alters a component's character less than
over-rounding does.

The `designMd` field is an escape hatch for prose, and is treated as advisory:
it is a hint to a generative model, not an enforced constraint, and the system
never relies on it for correctness.

## Consequences

**Accepted:**

- A provider preview will routinely differ from the approved design system. This
  is surfaced, not hidden, and reviewers must understand the difference.
- The projection needs maintenance when the provider's enums change. The font
  count is asserted in a test
  (`The_verified_font_enum_matches_the_provider_schema_size`), so a change
  fails the build rather than degrading silently.
- Dark mode needs a second provider design system.

**Gained:**

- An approved design system is never silently downgraded to what a provider
  happens to support.
- "Why does the Stitch preview use Inter instead of our brand font?" has a
  machine-generated answer at the token level.

## Not chosen

- **Restrict our design system to what providers support.** Would make the
  product's output a function of the weakest provider.
- **Fail when a token cannot be projected.** Would block legitimate work; most
  design systems contain something unrepresentable.
- **Project silently.** The failure mode this ADR exists to prevent.

## Amendments

### A1 — Colour token paths after the roles/modes split (2026-10-20)

**Finding:** M00-R4-003.

The projection-rules table above was written against the original design-system
schema, where colour roles and colour modes were siblings:
`color.primary`, `color.modes`. M00-R3-002 split them — every value under
`color.roles` is a colour, so the `additionalProperties` contract and the
TypeScript index signature describing it are homogeneous, where previously the
signature had to admit arrays and let `danger: ["light"]` type-check.

The decision and every projection rule are unchanged. Only the paths moved. The
table is left as written, per the rule against editing an accepted decision in
place; this amendment is how it reads now:

| As written in the table            | Current path                             |
| :--------------------------------- | :--------------------------------------- |
| `color.primary`                    | `color.roles.primary`                    |
| `color.secondary/tertiary/neutral` | `color.roles.secondary/tertiary/neutral` |
| Any other colour role              | Any other key under `color.roles`        |
| `color.modes: [light]`             | `color.modes: [light]` — unchanged       |
| `color.modes: [light, dark]`       | `color.modes: [light, dark]` — unchanged |

`ProjectionWarning.TokenPath` reports the current paths, so a dropped role
surfaces as `/color/roles/danger` rather than `/color/danger`. The warning text,
the fallback behaviour and the tie-rounds-down rule are untouched.

**Where this is enforced:** `StitchThemeProjector` reads `color["roles"]` and
`color["modes"]`. `StitchThemeProjectionTests` asserts the emitted paths —
`/color/roles/danger`, `/color/roles/success`, `/color/roles/primary` — and
`/color/modes`, which the split left where it was.
