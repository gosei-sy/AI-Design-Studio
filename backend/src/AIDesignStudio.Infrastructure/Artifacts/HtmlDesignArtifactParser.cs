using System.Text.RegularExpressions;
using AIDesignStudio.Application.Artifacts;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace AIDesignStudio.Infrastructure.Artifacts;

/// <summary>
/// Normalizes an HTML or DOM snapshot into <see cref="NormalizedDesignArtifact"/>.
///
/// Handles the output of a capture step (self-contained HTML of a real route),
/// which is the input to the redesign workflow. Produces
/// <see cref="ArtifactFidelity.StructuralWithStyles"/>: a semantic element tree
/// plus observed style values as token *candidates*.
///
/// What it deliberately does not do: infer a design system. Observed colours and
/// fonts from one page are evidence, not an approved token set; promoting them
/// requires human approval (see ADR-0002).
/// </summary>
public sealed partial class HtmlDesignArtifactParser(ILogger<HtmlDesignArtifactParser> logger)
    : IDesignArtifactParser
{
    /// <summary>Elements that carry no design meaning and would swamp the tree.</summary>
    private static readonly HashSet<string> IgnoredTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "meta", "link", "noscript", "template", "br", "wbr",
    };

    /// <summary>
    /// Maps HTML tags to stable semantic roles so downstream code reasons about
    /// roles rather than tag names.
    /// </summary>
    private static readonly Dictionary<string, string> RoleByTag = new(StringComparer.OrdinalIgnoreCase)
    {
        ["header"] = "banner",
        ["nav"] = "navigation",
        ["main"] = "main",
        ["aside"] = "complementary",
        ["footer"] = "contentinfo",
        ["form"] = "form",
        ["button"] = "button",
        ["a"] = "link",
        ["input"] = "input",
        ["select"] = "select",
        ["textarea"] = "textarea",
        ["table"] = "table",
        ["img"] = "image",
        ["h1"] = "heading",
        ["h2"] = "heading",
        ["h3"] = "heading",
        ["h4"] = "heading",
        ["h5"] = "heading",
        ["h6"] = "heading",
        ["ul"] = "list",
        ["ol"] = "list",
        ["li"] = "listitem",
        ["p"] = "paragraph",
        ["label"] = "label",
        ["section"] = "region",
        ["article"] = "article",
        ["dialog"] = "dialog",
    };

    public IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/html", "application/xhtml+xml" };

    public async Task<NormalizedDesignArtifact> ParseAsync(
        Stream content,
        string contentType,
        string sourceDescription,
        CancellationToken cancellationToken)
    {
        string html;

        using (var reader = new StreamReader(content, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return new NormalizedDesignArtifact(
                sourceDescription, ArtifactFidelity.None, null, TokenCandidates.Empty,
                ["The artifact was empty."]);
        }

        var warnings = new List<string>();
        IDocument document;

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            document = await context.OpenAsync(req => req.Content(html), cancellationToken);
        }
        catch (Exception ex)
        {
            // Never throw on user input: a reduced-fidelity result is more useful
            // than a failed import.
            logger.LogWarning(ex, "HTML parse failed for {Source}", sourceDescription);

            return NormalizedDesignArtifact.VisualOnly(
                sourceDescription, $"The HTML could not be parsed: {ex.Message}");
        }

        var body = document.Body;

        if (body is null)
        {
            return NormalizedDesignArtifact.VisualOnly(
                sourceDescription, "The document had no <body> element.");
        }

        var root = Normalize(body) ?? new NormalizedNode("document", "body", null, []);
        var candidates = ExtractTokenCandidates(html, warnings);

        if (document.QuerySelectorAll("[style]").Length > 0)
        {
            warnings.Add(
                "Inline styles are present. Observed values are token candidates only; " +
                "they are not an approved design system.");
        }

        var fidelity = candidates.Colors.Count > 0 || candidates.FontFamilies.Count > 0
            ? ArtifactFidelity.StructuralWithStyles
            : ArtifactFidelity.Structural;

        logger.LogInformation(
            "Normalized {Source}: {NodeCount} nodes, fidelity {Fidelity}",
            sourceDescription, root.CountSelfAndDescendants(), fidelity);

        return new NormalizedDesignArtifact(sourceDescription, fidelity, root, candidates, warnings);
    }

    /// <summary>
    /// Converts an element subtree, dropping noise elements and collapsing
    /// elements that contribute neither text nor children.
    /// </summary>
    private static NormalizedNode? Normalize(IElement element)
    {
        if (IgnoredTags.Contains(element.TagName))
        {
            return null;
        }

        var children = element.Children
            .Select(Normalize)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        var text = DirectText(element);
        var hasOwnMeaning = RoleByTag.ContainsKey(element.TagName) || !string.IsNullOrEmpty(text);

        // A wrapper div with one child adds nothing; hoist the child instead.
        if (!hasOwnMeaning && children.Count == 1)
        {
            return children[0];
        }

        if (!hasOwnMeaning && children.Count == 0)
        {
            return null;
        }

        var role = ResolveRole(element);
        return new NormalizedNode(role, element.TagName.ToLowerInvariant(), text, children);
    }

    /// <summary>
    /// Prefers an explicit ARIA role, then the tag's semantic role, then a generic
    /// container role.
    /// </summary>
    private static string ResolveRole(IElement element)
    {
        var aria = element.GetAttribute("role");

        if (!string.IsNullOrWhiteSpace(aria))
        {
            return aria.Trim().ToLowerInvariant();
        }

        return RoleByTag.TryGetValue(element.TagName, out var role) ? role : "container";
    }

    /// <summary>Text owned by this element, excluding text inside descendants.</summary>
    private static string? DirectText(IElement element)
    {
        var text = string.Concat(
            element.ChildNodes
                .Where(n => n.NodeType == NodeType.Text)
                .Select(n => n.TextContent));

        text = WhitespaceRun().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Harvests observed colour, font and size literals from style blocks and
    /// inline styles.
    ///
    /// Regex is appropriate here precisely because the goal is not to build a
    /// CSS model: it is to collect candidate literals for a human to review. A
    /// full cascade resolution would imply a fidelity the artifact does not have.
    /// </summary>
    private static TokenCandidates ExtractTokenCandidates(string html, List<string> warnings)
    {
        var colors = new SortedSet<string>(StringComparer.Ordinal);
        var fonts = new SortedSet<string>(StringComparer.Ordinal);
        var sizes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var match in HexColor().Matches(html).Cast<Match>())
        {
            colors.Add(match.Value.ToLowerInvariant());
        }

        foreach (var match in RgbColor().Matches(html).Cast<Match>())
        {
            colors.Add(match.Value.ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        foreach (var match in FontFamily().Matches(html).Cast<Match>())
        {
            // Keep the whole stack ("'Plus Jakarta Sans', sans-serif") rather than
            // just the first family: the stack is what was actually observed.
            // Only a quote that terminates an inline style attribute is stripped.
            var value = match.Groups[1].Value.Trim().TrimEnd('"', '\'').Trim();

            if (value.Length is > 0 and < 200)
            {
                fonts.Add(value);
            }
        }

        foreach (var match in FontSize().Matches(html).Cast<Match>())
        {
            sizes.Add(match.Groups[1].Value.Trim());
        }

        if (colors.Count > 40)
        {
            warnings.Add(
                $"{colors.Count} distinct colours were observed, which is far more than a coherent " +
                "palette. Treat these as raw observations requiring consolidation.");
        }

        return new TokenCandidates([.. colors], [.. fonts], [.. sizes]);
    }

    [GeneratedRegex(@"#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"rgba?\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*(?:,\s*[\d.]+\s*)?\)")]
    private static partial Regex RgbColor();

    [GeneratedRegex(@"font-family\s*:\s*([^;}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FontFamily();

    [GeneratedRegex(@"font-size\s*:\s*([\d.]+(?:px|rem|em|pt))", RegexOptions.IgnoreCase)]
    private static partial Regex FontSize();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}

/// <summary>
/// Handles raster screenshot uploads.
///
/// Returns <see cref="ArtifactFidelity.Visual"/> and nothing else. There is no
/// OCR, no layout inference, and no token extraction, because an uploaded PNG
/// contains none of that information. Representing a screenshot as if it were an
/// editable design is the single most tempting mistake in this product; this
/// parser exists to make the limit explicit rather than implicit.
/// </summary>
public sealed class ScreenshotDesignArtifactParser : IDesignArtifactParser
{
    public IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/webp", "image/avif",
        };

    public Task<NormalizedDesignArtifact> ParseAsync(
        Stream content,
        string contentType,
        string sourceDescription,
        CancellationToken cancellationToken) =>
        Task.FromResult(NormalizedDesignArtifact.VisualOnly(
            sourceDescription,
            $"'{contentType}' is a raster image. It provides a visual reference only: no element tree, " +
            "no text content, and no design tokens can be recovered from it. Capture the DOM of the " +
            "running application instead if structural fidelity is required."));
}
