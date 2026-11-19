namespace AIDesignStudio.Application.Artifacts;

/// <summary>
/// How much of a design an artifact actually carries. Recorded explicitly so the
/// product never implies more than it has: a PNG upload and an HTML snapshot are
/// both "an imported design", but only one of them can be reasoned about
/// structurally.
/// </summary>
public enum ArtifactFidelity
{
    /// <summary>Nothing could be extracted.</summary>
    None = 0,

    /// <summary>
    /// Pixels only. No element tree, no text, no tokens. An uploaded screenshot
    /// is Visual fidelity and nothing more, regardless of how good it looks.
    /// </summary>
    Visual = 1,

    /// <summary>An element tree and text content were recovered (HTML/DOM snapshot).</summary>
    Structural = 2,

    /// <summary>Element tree plus resolved style values usable as token candidates.</summary>
    StructuralWithStyles = 3,
}

/// <summary>
/// Provider-neutral internal representation of an imported design artifact.
///
/// This is the only shape the rest of the system consumes. Provider payloads and
/// raw uploads are converted into it at the boundary, so no module downstream of
/// import depends on a provider's format.
/// </summary>
/// <param name="SourceDescription">Where this came from, for audit.</param>
/// <param name="Fidelity">What was genuinely recovered.</param>
/// <param name="Root">Element tree root, or null at Visual fidelity.</param>
/// <param name="TokenCandidates">
/// Observed style values. These are *candidates* for a design system, never an
/// approved token set: promotion requires human approval.
/// </param>
/// <param name="Warnings">Everything the parser could not do. Surfaced to the user.</param>
public sealed record NormalizedDesignArtifact(
    string SourceDescription,
    ArtifactFidelity Fidelity,
    NormalizedNode? Root,
    TokenCandidates TokenCandidates,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Total node count in the tree; 0 at Visual fidelity.</summary>
    public int NodeCount => Root?.CountSelfAndDescendants() ?? 0;

    public static NormalizedDesignArtifact VisualOnly(string sourceDescription, string reason) =>
        new(sourceDescription, ArtifactFidelity.Visual, Root: null, TokenCandidates.Empty, [reason]);
}

/// <summary>
/// One node of the normalized tree. Intentionally small: a semantic role, the
/// text it contributes, and its children. Styling is aggregated separately in
/// <see cref="TokenCandidates"/> rather than attached per node, because per-node
/// style from a snapshot is too noisy to be useful.
/// </summary>
public sealed record NormalizedNode(
    string Role,
    string Tag,
    string? Text,
    IReadOnlyList<NormalizedNode> Children)
{
    public int CountSelfAndDescendants() => 1 + Children.Sum(c => c.CountSelfAndDescendants());

    /// <summary>Depth-first enumeration including this node.</summary>
    public IEnumerable<NormalizedNode> Flatten()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}

/// <param name="Colors">Distinct colour literals observed, normalized to lowercase hex where possible.</param>
/// <param name="FontFamilies">Distinct font-family stacks observed.</param>
/// <param name="FontSizes">Distinct font-size literals observed.</param>
public sealed record TokenCandidates(
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> FontFamilies,
    IReadOnlyList<string> FontSizes)
{
    public static TokenCandidates Empty { get; } = new([], [], []);
}

/// <summary>
/// Parses a raw imported artifact into <see cref="NormalizedDesignArtifact"/>.
/// </summary>
public interface IDesignArtifactParser
{
    /// <summary>Content types this parser handles, e.g. <c>text/html</c>.</summary>
    IReadOnlySet<string> SupportedContentTypes { get; }

    /// <summary>
    /// Normalizes the artifact. Must not throw on malformed input: it reports
    /// reduced fidelity and a warning instead, because user uploads are
    /// routinely malformed and a failed import is worse than a partial one.
    /// </summary>
    Task<NormalizedDesignArtifact> ParseAsync(
        Stream content,
        string contentType,
        string sourceDescription,
        CancellationToken cancellationToken);
}
