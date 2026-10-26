# SPIKE-03: Importing a screenshot and associated metadata

**Status:** ✅ **Verified** — including, and especially, its limits
**Date:** 2026-10-20

---

## Question

Can we import a screenshot with metadata, store it safely, and represent
honestly what it does and does not give us?

The brief is explicit on the last part: _"Do not treat importing a screenshot as
equivalent to extracting a complete editable design."_ This spike exists as much
to pin down the limit as to prove the capability.

---

## Verified: storage

`LocalFileSystemAssetStorage` over the `IAssetStorage` port. 11 tests.

| Behaviour                                                         | Verified |
| :---------------------------------------------------------------- | :------- |
| Round-trip with correct byte count and SHA-256                    | ✅       |
| Hash computed **while streaming**, so the stream is read once     | ✅       |
| `Exists` / `Delete`, with delete idempotent                       | ✅       |
| Missing asset reports `FileNotFoundException`                     | ✅       |
| Key format `org/{org}/project/{project}/asset/{asset}.{ext}`      | ✅       |
| Extension normalized (`.PNG` → `.png`); missing extension handled | ✅       |

### Path-traversal confinement

Storage keys are the only client-influenced value that reaches the filesystem,
so they are the one place a tenant boundary could be crossed. Rejected inputs,
each covered by a test case:

```
../../../etc/passwd
org/x/../../../../etc/passwd
/etc/passwd
..\..\windows\system32
C:/windows/system32
""   "   "          (empty / whitespace)
org/a\0b            (control character)
```

Two layers, deliberately:

1. The key is validated for shape (no leading `/`, no `..`, no `\`, no `:`, no
   control characters).
2. The **resolved absolute path** is re-checked against the configured root.

Layer 2 exists so the guard survives a future weakening of layer 1.

**Keys are server-generated, never client-supplied.** `IAssetStorage.BuildKey`
puts the organization id first, so a mis-scoped query cannot silently reach
another tenant's blobs.

---

## Verified: metadata

`AssetReference` holds what the database needs; bytes live in the backend.

| Field              | Guard                                                                  |
| :----------------- | :--------------------------------------------------------------------- |
| `OrganizationId`   | Required (`asset.organization_required`)                               |
| `ProjectId`        |                                                                        |
| `Kind`             | `Screenshot` \| `MarkupSnapshot` \| `ScreenRender` \| `DesignDocument` |
| `StorageKey`       | Required, unique (`asset.storage_key_required`)                        |
| `ContentType`      |                                                                        |
| `SizeBytes`        | Must be > 0 (`asset.empty`)                                            |
| `ContentHash`      | Required (`asset.hash_required`)                                       |
| `OriginalFileName` | Retained for display only, never used as a path                        |

The original filename is deliberately never part of the storage key. A
user-supplied filename in a path is a traversal vector and a collision source.

---

## Verified: the limit

This is the part that matters.

```csharp
public Task<NormalizedDesignArtifact> ParseAsync(...) =>
    Task.FromResult(NormalizedDesignArtifact.VisualOnly(
        sourceDescription,
        $"'{contentType}' is a raster image. It provides a visual reference only: " +
        "no element tree, no text content, and no design tokens can be recovered " +
        "from it. Capture the DOM of the running application instead if " +
        "structural fidelity is required."));
```

`ScreenshotDesignArtifactParser` does no OCR, no layout inference, no token
extraction. It handles `image/png`, `image/jpeg`, `image/webp`, `image/avif` and
returns `ArtifactFidelity.Visual`.

### The limit is asserted, not merely documented

```csharp
[Fact]
public async Task A_screenshot_yields_visual_fidelity_only_with_no_structure_or_tokens()
{
    var artifact = await _screenshot.ParseAsync(
        new MemoryStream([0x89, 0x50, 0x4E, 0x47]), "image/png",
        "upload:dashboard.png", CancellationToken.None);

    artifact.Fidelity.ShouldBe(ArtifactFidelity.Visual);
    artifact.Root.ShouldBeNull();
    artifact.NodeCount.ShouldBe(0);
    artifact.TokenCandidates.Colors.ShouldBeEmpty();
    artifact.TokenCandidates.FontFamilies.ShouldBeEmpty();
    artifact.Warnings.ShouldHaveSingleItem().ShouldContain("visual reference only");
}
```

Documentation does not fail a build. This test does. If someone later adds OCR
and starts populating `Root` from a PNG, this test breaks and forces the
conversation.

### Fidelity is a value in the data model

```
None = 0                  nothing extractable
Visual = 1                pixels only
Structural = 2            element tree and text
StructuralWithStyles = 3  tree plus observed style values
```

Because fidelity is data rather than a convention, no downstream feature can
accidentally assume structure that is not there. `ScreenOrigin` carries the same
distinction onto the screen version: `ImportedScreenshot` is a different value
from `CapturedMarkup`, so provenance is queryable.

### Content types are strictly partitioned

```csharp
_html.SupportedContentTypes.ShouldNotContain("image/png");
_screenshot.SupportedContentTypes.ShouldNotContain("text/html");
```

A PNG cannot accidentally reach the HTML parser and acquire fidelity it has not
earned.

---

## Why this matters more than it looks

A screenshot that _appears_ to be an editable design is the most damaging
possible output of this product. Consider the redesign workflow: if the system
implies it understands a screenshot, a user reasonably assumes the capabilities
visible in it are now tracked. They are not. The redesign would then drop
functionality that was visible in a screenshot but never represented in the
system — which is precisely the failure the whole redesign workflow exists to
prevent.

The honest path is the higher-fidelity one, and the warning says so: capture the
DOM of a running application instead. Google's own CLI guidance agrees — when
importing an existing application, capture real views rather than generating
them.

Recorded as ADR-0006.

---

## Not verified

| Item                                   | Milestone                                                                                      |
| :------------------------------------- | :--------------------------------------------------------------------------------------------- |
| Azure Blob Storage adapter             | M02 — port is defined; adapter unwritten                                                       |
| Upload endpoint and multipart handling | M01                                                                                            |
| Upload size limits and virus scanning  | M02                                                                                            |
| Image dimension extraction as metadata | Not scoped. Note: dimensions are _metadata_, not structure, so this would not change fidelity. |
| Thumbnail generation                   | Not scoped                                                                                     |

---

## Tests

`backend/tests/AIDesignStudio.UnitTests/AssetStorageTests.cs` — 11 tests
`backend/tests/AIDesignStudio.UnitTests/ArtifactParsingTests.cs` — 13 tests
(the screenshot-limit assertions are in the latter)
