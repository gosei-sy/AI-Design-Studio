using System.Text;
using AIDesignStudio.Application.Artifacts;
using AIDesignStudio.Infrastructure.Artifacts;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers normalization of imported artifacts into the internal representation,
/// and — more importantly — that the representation never claims more fidelity
/// than the artifact actually carries.
/// </summary>
public sealed class ArtifactParsingTests
{
    private readonly HtmlDesignArtifactParser _html = new(NullLogger<HtmlDesignArtifactParser>.Instance);
    private readonly ScreenshotDesignArtifactParser _screenshot = new();

    private const string CapturedCheckout = """
        <!doctype html>
        <html>
          <head>
            <style>
              body { font-family: 'Plus Jakarta Sans', sans-serif; color: #111827; }
              .btn { background: #3366ff; border-radius: 8px; font-size: 16px; }
              .muted { color: rgba(17, 24, 39, 0.6); }
            </style>
          </head>
          <body>
            <header><h1>Checkout</h1></header>
            <nav><a href="/cart">Back to cart</a></nav>
            <main>
              <div><div>
                <form>
                  <label for="email">Email</label>
                  <input id="email" type="email" />
                  <button class="btn">Pay now</button>
                </form>
              </div></div>
            </main>
            <footer><p class="muted">All prices include VAT.</p></footer>
            <script>console.log('tracking');</script>
          </body>
        </html>
        """;

    private Task<NormalizedDesignArtifact> ParseHtml(string html) =>
        _html.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(html)),
            "text/html",
            "captured:/checkout",
            CancellationToken.None);

    [Fact]
    public async Task A_dom_snapshot_yields_structural_fidelity()
    {
        var artifact = await ParseHtml(CapturedCheckout);

        artifact.Fidelity.ShouldBe(ArtifactFidelity.StructuralWithStyles);
        artifact.Root.ShouldNotBeNull();
        artifact.SourceDescription.ShouldBe("captured:/checkout");
    }

    [Fact]
    public async Task Semantic_roles_are_recovered_from_tags()
    {
        var roles = (await ParseHtml(CapturedCheckout)).Root!.Flatten()
            .Select(n => n.Role)
            .ToList();

        roles.ShouldContain("banner");
        roles.ShouldContain("navigation");
        roles.ShouldContain("main");
        roles.ShouldContain("form");
        roles.ShouldContain("button");
        roles.ShouldContain("contentinfo");
        roles.ShouldContain("heading");
    }

    [Fact]
    public async Task An_explicit_aria_role_takes_precedence_over_the_tag()
    {
        var artifact = await ParseHtml("""<body><div role="alertdialog">Careful</div></body>""");

        artifact.Root!.Flatten().Select(n => n.Role).ShouldContain("alertdialog");
    }

    [Fact]
    public async Task Text_content_is_attached_to_the_element_that_owns_it()
    {
        var nodes = (await ParseHtml(CapturedCheckout)).Root!.Flatten().ToList();

        nodes.ShouldContain(n => n.Role == "heading" && n.Text == "Checkout");
        nodes.ShouldContain(n => n.Role == "button" && n.Text == "Pay now");
        nodes.ShouldContain(n => n.Role == "link" && n.Text == "Back to cart");
    }

    [Fact]
    public async Task Script_and_style_elements_are_excluded_from_the_tree()
    {
        var tags = (await ParseHtml(CapturedCheckout)).Root!.Flatten().Select(n => n.Tag).ToList();

        tags.ShouldNotContain("script");
        tags.ShouldNotContain("style");
    }

    /// <summary>
    /// Captured markup is full of meaningless wrapper divs. Keeping them would
    /// make every downstream comparison noise-dominated.
    /// </summary>
    [Fact]
    public async Task Meaningless_wrapper_elements_are_collapsed()
    {
        var artifact = await ParseHtml("""
            <body><div><div><div><button>Go</button></div></div></div></body>
            """);

        var nodes = artifact.Root!.Flatten().ToList();

        nodes.Count(n => n.Role == "container").ShouldBe(0);
        nodes.ShouldHaveSingleItem().Role.ShouldBe("button");
    }

    [Fact]
    public async Task Observed_styles_are_collected_as_token_candidates()
    {
        var candidates = (await ParseHtml(CapturedCheckout)).TokenCandidates;

        candidates.Colors.ShouldContain("#111827");
        candidates.Colors.ShouldContain("#3366ff");
        candidates.Colors.ShouldContain("rgba(17,24,39,0.6)");
        candidates.FontFamilies.ShouldContain(f => f.Contains("Plus Jakarta Sans"));
        candidates.FontSizes.ShouldContain("16px");
    }

    [Fact]
    public async Task Malformed_html_degrades_rather_than_throwing()
    {
        var artifact = await ParseHtml("<body><div><p>unclosed");

        ((int)artifact.Fidelity).ShouldBeGreaterThanOrEqualTo((int)ArtifactFidelity.Structural);
        artifact.Root.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_empty_artifact_reports_no_fidelity_instead_of_failing()
    {
        var artifact = await ParseHtml("   ");

        artifact.Fidelity.ShouldBe(ArtifactFidelity.None);
        artifact.Root.ShouldBeNull();
        artifact.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_incoherent_palette_is_flagged_for_consolidation()
    {
        var colors = string.Join(" ", Enumerable.Range(0, 50).Select(i => $".c{i}{{color:#{i:x6};}}"));
        var artifact = await ParseHtml($"<body><style>{colors}</style><p>Hi</p></body>");

        artifact.Warnings.ShouldContain(w => w.Contains("distinct colours"));
    }

    // -----------------------------------------------------------------------
    // The limit that must never be blurred
    // -----------------------------------------------------------------------

    /// <summary>
    /// An uploaded screenshot is a visual reference and nothing more. Treating it
    /// as an editable design is the most tempting mistake in this product, so the
    /// limit is asserted rather than merely documented.
    /// </summary>
    [Fact]
    public async Task A_screenshot_yields_visual_fidelity_only_with_no_structure_or_tokens()
    {
        var artifact = await _screenshot.ParseAsync(
            new MemoryStream([0x89, 0x50, 0x4E, 0x47]),
            "image/png",
            "upload:dashboard.png",
            CancellationToken.None);

        artifact.Fidelity.ShouldBe(ArtifactFidelity.Visual);
        artifact.Root.ShouldBeNull();
        artifact.NodeCount.ShouldBe(0);
        artifact.TokenCandidates.Colors.ShouldBeEmpty();
        artifact.TokenCandidates.FontFamilies.ShouldBeEmpty();
        artifact.Warnings.ShouldHaveSingleItem().ShouldContain("visual reference only");
    }

    [Fact]
    public void Each_parser_declares_the_content_types_it_handles()
    {
        _html.SupportedContentTypes.ShouldContain("text/html");
        _screenshot.SupportedContentTypes.ShouldContain("image/png");

        _html.SupportedContentTypes.ShouldNotContain("image/png");
        _screenshot.SupportedContentTypes.ShouldNotContain("text/html");
    }
}
