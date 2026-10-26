# ADR-0006: A screenshot import is a visual reference, not an editable design

- **Status:** Accepted (M00)
- **Date:** 2026-10-20
- **Deciders:** Product Owner, Solution Architect, UX Lead

## Context

The redesign workflow starts from "screenshots and an existing feature
inventory". There is strong pressure — product and commercial — to present a
screenshot upload as though the system now understands the design. It does not.
A PNG contains pixels. It has no element tree, no text content, no tokens.

Blurring this is the most damaging mistake available to this product: it would
cause a redesign to silently drop functionality that was visible in a screenshot
but never represented in the system, which is precisely the failure the redesign
workflow exists to prevent.

Google's own CLI guidance is explicit on the same point: when importing an
existing application, capture real views and upload them — _"Never call
`stitch generate screen` during code import"_ — because generation invents UI
the application does not have.

## Decision

Imported artifacts carry an explicit, enumerated **fidelity** level, and the
system never claims more than the artifact provides.

```csharp
public enum ArtifactFidelity
{
    None = 0,                   // nothing extractable
    Visual = 1,                 // pixels only
    Structural = 2,             // element tree and text
    StructuralWithStyles = 3,   // tree plus observed style values
}
```

| Input                           | Fidelity               | What is recovered                                                           |
| :------------------------------ | :--------------------- | :-------------------------------------------------------------------------- |
| PNG / JPEG / WebP / AVIF upload | `Visual`               | Nothing. No OCR, no layout inference, no tokens.                            |
| Captured DOM / HTML snapshot    | `StructuralWithStyles` | Semantic element tree, text, observed colours/fonts/sizes as **candidates** |

`ScreenshotDesignArtifactParser` returns `Visual` and a warning stating the
limit, in the words the user should see:

> `image/png` is a raster image. It provides a visual reference only: no element
> tree, no text content, and no design tokens can be recovered from it. Capture
> the DOM of the running application instead if structural fidelity is required.

`ScreenOrigin` records the same distinction on every screen version:
`ImportedScreenshot` is a separate value from `CapturedMarkup`, so provenance is
queryable rather than inferred.

### Consequences for the product

- The redesign workflow **requires** a feature inventory. Screenshots are
  supporting evidence, never the source of functional coverage. The handoff gate
  is `FeatureInventory.EnsureReadyForHandoff()`, which blocks while any
  catalogued feature is neither covered nor explicitly waived (ADR-0007).
- Where a running application is reachable, DOM capture is strongly preferred
  and the UI must say why.
- Observed styles are `TokenCandidates`, deliberately named. One page's computed
  styles are evidence for a design system, not a design system.

## Consequences

**Accepted:**

- Less impressive than claiming a screenshot can be "converted to a design".
- Users with no access to a running application get lower fidelity, and must do
  more manual work.
- We must keep resisting the pressure to add OCR or layout inference and present
  the guesses as structure.

**Gained:**

- A redesign cannot lose functionality because of an overstated import.
- Fidelity is a value in the data model, so no downstream feature can
  accidentally assume structure that is not there.

## Verification

`ArtifactParsingTests.A_screenshot_yields_visual_fidelity_only_with_no_structure_or_tokens`
asserts the limit directly: `Root` is null, `NodeCount` is 0, and both token
candidate lists are empty. The limit is tested, not merely documented, because
documentation does not fail a build.

## Not chosen

- **OCR plus layout inference on screenshots.** Produces plausible, unverifiable
  structure — the worst possible output for a system whose purpose is to avoid
  silent functional loss. Reconsider only with a confidence model and an
  explicit human verification step.
- **Treat all imports as equivalent.** Collapses the distinction this ADR
  exists to preserve.
