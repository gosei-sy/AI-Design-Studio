# SPIKE-04: Parsing an imported artifact into a normalized internal representation

**Status:** ✅ **Verified**
**Date:** 2026-10-20

---

## Question

Can an imported design artifact be parsed into a provider-neutral internal
representation that the rest of the system can reason about?

---

## The representation

```csharp
record NormalizedDesignArtifact(
    string SourceDescription,
    ArtifactFidelity Fidelity,
    NormalizedNode? Root,
    TokenCandidates TokenCandidates,
    IReadOnlyList<string> Warnings);

record NormalizedNode(
    string Role,          // semantic role, not tag name
    string Tag,
    string? Text,         // text this node owns
    IReadOnlyList<NormalizedNode> Children);

record TokenCandidates(
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> FontFamilies,
    IReadOnlyList<string> FontSizes);
```

This is the **only** shape the rest of the system consumes. Provider payloads and
raw uploads are converted at the boundary, so no module downstream of import
depends on a provider's format.

Three deliberate design choices:

1. **Role, not tag.** Downstream code reasons about `banner` and `navigation`,
   not `header` and `nav`. An explicit ARIA `role` attribute wins over the tag.
2. **Styles aggregated, not per-node.** Per-node style from a snapshot is too
   noisy to be useful, so observed values are collected once at the artifact
   level.
3. **"Candidates", not tokens.** The name is load-bearing — see below.

---

## Verified behaviour

Input: a realistic captured checkout page with a `<style>` block, semantic
landmarks, a form, nested wrapper `div`s, and a tracking `<script>`. 13 tests.

| Behaviour                                                                                            | Verified |
| :--------------------------------------------------------------------------------------------------- | :------- |
| A DOM snapshot yields `StructuralWithStyles`                                                         | ✅       |
| Semantic roles recovered: `banner`, `navigation`, `main`, `form`, `button`, `contentinfo`, `heading` | ✅       |
| An explicit ARIA role overrides the tag (`div role="alertdialog"`)                                   | ✅       |
| Text attached to the element that owns it, not to ancestors                                          | ✅       |
| `<script>` and `<style>` excluded from the tree                                                      | ✅       |
| Meaningless wrapper elements collapsed                                                               | ✅       |
| Colours harvested: hex and `rgba()`                                                                  | ✅       |
| Font stacks and sizes harvested                                                                      | ✅       |
| Malformed HTML degrades instead of throwing                                                          | ✅       |
| Empty input yields `None` with a warning                                                             | ✅       |
| >40 distinct colours flagged for consolidation                                                       | ✅       |
| Content types strictly partitioned between parsers                                                   | ✅       |

### Wrapper collapsing

```html
<body>
  <div>
    <div>
      <div><button>Go</button></div>
    </div>
  </div>
</body>
```

normalizes to a single `button` node. Captured markup is full of layout wrappers;
keeping them would make every downstream comparison noise-dominated. A node
survives only if it has a semantic role or owns text; a meaningless node with
exactly one child is replaced by that child.

### Never throwing on user input

```csharp
catch (Exception ex)
{
    logger.LogWarning(ex, "HTML parse failed for {Source}", sourceDescription);
    return NormalizedDesignArtifact.VisualOnly(
        sourceDescription, $"The HTML could not be parsed: {ex.Message}");
}
```

User uploads are routinely malformed. A failed import is worse than a partial
one, so the parser reports reduced fidelity and a warning rather than failing.
Verified by `Malformed_html_degrades_rather_than_throwing` and
`An_empty_artifact_reports_no_fidelity_instead_of_failing`.

---

## What the parser deliberately does not do

### It does not infer a design system

Observed styles are `TokenCandidates`. The name is the point: one page's
computed styles are **evidence** for a design system, not a design system.
Promotion to an approved token set requires human approval (ADR-0002).

The parser reinforces this with a warning when the evidence is obviously
incoherent:

```
50 distinct colours were observed, which is far more than a coherent
palette. Treat these as raw observations requiring consolidation.
```

and when inline styles are present:

```
Inline styles are present. Observed values are token candidates only;
they are not an approved design system.
```

### It does not resolve the CSS cascade

Style extraction is regex over style blocks and inline attributes. That is a
deliberate choice, not a shortcut: resolving the cascade would imply a fidelity
the artifact does not have. The goal is to collect candidate literals for a
human to review, and for that purpose a precise cascade model would be more
machinery for no additional truth.

A note for whoever revisits this: the first instinct on reading "regex over CSS"
is to replace it with a parser. Before doing so, check whether anything
downstream actually needs resolved values — if the output is still a review
list, the parser buys nothing.

### It does not OCR images

Raster uploads go to `ScreenshotDesignArtifactParser` and get `Visual` fidelity
(SPIKE-03, ADR-0006).

---

## A bug found and fixed during the spike

The initial font-family regex excluded quote characters from the captured value:

```
font-family\s*:\s*([^;"'}]+)
```

Against real CSS this fails:

```css
font-family: "Plus Jakarta Sans", sans-serif;
```

The character class stopped at the opening quote, capturing only whitespace, so
every quoted family — the common case — was silently dropped. The bug surfaced
because the test used a realistic quoted font stack rather than a bare name.

Fixed to `([^;}]+)` with trailing-quote trimming, and the whole stack is now kept
rather than just the first family: the stack is what was actually observed.

Worth recording because it is the kind of bug that a simplified test fixture
hides.

---

## Not verified

| Item                                                           | Milestone                                                                                 |
| :------------------------------------------------------------- | :---------------------------------------------------------------------------------------- |
| Normalizing provider-returned markup                           | Blocked on SPIKE-02 — we have not confirmed Stitch exposes markup                         |
| Import endpoint                                                | M01                                                                                       |
| Promoting token candidates to a design system draft            | M02                                                                                       |
| Conformance checking normalized output against a design system | Post-MVP; needs reliable provider markup                                                  |
| Diffing two normalized artifacts                               | Post-MVP. Note: generation is non-deterministic, so this is a comparison aid, not a diff. |

---

## Tests

`backend/tests/AIDesignStudio.UnitTests/ArtifactParsingTests.cs` — 13 tests.
