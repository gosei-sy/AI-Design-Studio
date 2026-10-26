using System.Text.Json;
using AIDesignStudio.Infrastructure.Providers.Stitch;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Asserts that projecting our design system onto Stitch's theme model reports
/// every loss rather than hiding it.
///
/// This is the executable form of the product's core principle. If these tests
/// pass, an approved design system that Stitch cannot represent produces a
/// visible warning instead of a silently wrong preview.
/// </summary>
public sealed class StitchThemeProjectionTests
{
    private const string FullTokens = """
        {
          "name": "Acme Design System",
          "typography": {
            "bodyFont": "Inter",
            "headlineFont": "Plus Jakarta Sans",
            "scale": {
              "body": { "fontSize": "16px", "lineHeight": "24px" },
              "h1": { "fontSize": "32px", "fontWeight": "700" }
            }
          },
          "color": { "roles": { "primary": "#3366FF", "secondary": "#22C55E" }, "modes": ["light"] },
          "shape": { "radiusBase": "8px" },
          "spacing": { "xs": "4px", "sm": "8px", "md": "16px" }
        }
        """;

    [Fact]
    public void An_exactly_representable_design_system_projects_without_warnings()
    {
        var projection = StitchThemeProjector.Project(FullTokens);

        projection.Warnings.ShouldBeEmpty();

        var theme = ThemeOf(projection.NativeThemeJson);
        theme.GetProperty("bodyFont").GetString().ShouldBe("INTER");
        theme.GetProperty("headlineFont").GetString().ShouldBe("PLUS_JAKARTA_SANS");
        theme.GetProperty("colorMode").GetString().ShouldBe("LIGHT");
        theme.GetProperty("customColor").GetString().ShouldBe("#3366ff");
        theme.GetProperty("roundness").GetString().ShouldBe("ROUND_EIGHT");
        theme.GetProperty("overrideSecondaryColor").GetString().ShouldBe("#22c55e");
    }

    [Fact]
    public void Free_form_spacing_and_typography_scales_carry_across_intact()
    {
        var theme = ThemeOf(StitchThemeProjector.Project(FullTokens).NativeThemeJson);

        theme.GetProperty("spacing").GetProperty("md").GetString().ShouldBe("16px");
        theme.GetProperty("typography").GetProperty("h1").GetProperty("fontSize").GetString().ShouldBe("32px");
    }

    /// <summary>
    /// Stitch accepts 67 named families. A licensed brand font is the common case
    /// and must be reported, not silently swapped.
    /// </summary>
    [Fact]
    public void An_unsupported_brand_font_falls_back_and_warns()
    {
        const string tokens = """
            {
              "typography": { "bodyFont": "Acme Grotesk Variable" },
              "color": { "roles": { "primary": "#000000" } },
              "shape": { "radiusBase": "8px" }
            }
            """;

        var projection = StitchThemeProjector.Project(tokens);

        var warning = projection.Warnings.ShouldHaveSingleItem();
        warning.TokenPath.ShouldBe("/typography/bodyFont");
        warning.RequestedValue.ShouldBe("Acme Grotesk Variable");
        warning.AppliedValue.ShouldBe("INTER");
        warning.Reason.ShouldContain("not one of Stitch's 67 supported families");

        ThemeOf(projection.NativeThemeJson).GetProperty("bodyFont").GetString().ShouldBe("INTER");
    }

    /// <summary>
    /// Stitch's theme carries a single colorMode, so a design system defining both
    /// light and dark cannot be represented by one Stitch design system.
    /// </summary>
    [Fact]
    public void A_dual_mode_palette_reports_that_only_one_mode_is_applied()
    {
        const string tokens = """
            {
              "typography": { "bodyFont": "Inter" },
              "color": { "roles": { "primary": "#3366ff" }, "modes": ["light", "dark"] },
              "shape": { "radiusBase": "8px" }
            }
            """;

        var projection = StitchThemeProjector.Project(tokens);

        var warning = projection.Warnings.ShouldHaveSingleItem();
        warning.TokenPath.ShouldBe("/color/modes");
        warning.AppliedValue.ShouldBe("LIGHT");
        warning.Reason.ShouldContain("one colorMode");
    }

    /// <summary>
    /// Roundness is a five-step enum, so an arbitrary radius must snap and warn.
    /// Equidistant values resolve downward to the smaller step: under-rounding
    /// changes a component's character less than over-rounding does.
    /// </summary>
    [Theory]
    [InlineData("2px", "ROUND_TWO", false)]
    [InlineData("4px", "ROUND_FOUR", false)]
    [InlineData("8px", "ROUND_EIGHT", false)]
    [InlineData("12px", "ROUND_TWELVE", false)]
    [InlineData("9999px", "ROUND_FULL", false)]
    [InlineData("11px", "ROUND_TWELVE", true)]
    [InlineData("7px", "ROUND_EIGHT", true)]
    [InlineData("10px", "ROUND_EIGHT", true)]
    [InlineData("6px", "ROUND_FOUR", true)]
    [InlineData("3px", "ROUND_TWO", true)]
    public void A_radius_snaps_to_the_nearest_step_and_warns_only_when_inexact(
        string radius, string expectedToken, bool expectWarning)
    {
        var tokens = $$"""
            {
              "typography": { "bodyFont": "Inter" },
              "color": { "roles": { "primary": "#000000" } },
              "shape": { "radiusBase": "{{radius}}" }
            }
            """;

        var projection = StitchThemeProjector.Project(tokens);

        ThemeOf(projection.NativeThemeJson).GetProperty("roundness").GetString().ShouldBe(expectedToken);
        projection.Warnings.Any(w => w.TokenPath == "/shape/radiusBase").ShouldBe(expectWarning);
    }

    [Fact]
    public void Sections_Stitch_cannot_express_at_all_are_each_reported()
    {
        const string tokens = """
            {
              "typography": { "bodyFont": "Inter" },
              "color": { "roles": { "primary": "#000000" } },
              "shape": { "radiusBase": "8px" },
              "elevation": { "card": "0 1px 2px rgba(0,0,0,.08)" },
              "motion": { "standard": "200ms ease-out" },
              "breakpoints": { "md": "768px" },
              "components": { "button": { "variants": ["primary", "ghost"] } },
              "borders": { "width": "1px" }
            }
            """;

        var paths = StitchThemeProjector.Project(tokens).Warnings.Select(w => w.TokenPath).ToList();

        paths.ShouldContain("/elevation");
        paths.ShouldContain("/motion");
        paths.ShouldContain("/breakpoints");
        paths.ShouldContain("/components");
        paths.ShouldContain("/borders");
    }

    [Fact]
    public void A_semantic_colour_role_with_no_Stitch_slot_is_reported_as_dropped()
    {
        const string tokens = """
            {
              "typography": { "bodyFont": "Inter" },
              "color": { "roles": { "primary": "#000000", "danger": "#dc2626", "success": "#16a34a" } },
              "shape": { "radiusBase": "8px" }
            }
            """;

        var warnings = StitchThemeProjector.Project(tokens).Warnings;

        warnings.Count(w => w.AppliedValue is null).ShouldBe(2);
        warnings.ShouldContain(w => w.TokenPath == "/color/roles/danger");
        warnings.ShouldContain(w => w.TokenPath == "/color/roles/success");
    }

    [Fact]
    public void A_missing_primary_colour_is_reported_because_Stitch_requires_a_seed()
    {
        const string tokens = """
            { "typography": { "bodyFont": "Inter" }, "shape": { "radiusBase": "8px" } }
            """;

        var projection = StitchThemeProjector.Project(tokens);

        projection.Warnings.ShouldContain(w => w.TokenPath == "/color/roles/primary");
        ThemeOf(projection.NativeThemeJson).GetProperty("customColor").GetString().ShouldBe("#000000");
    }

    [Fact]
    public void Every_field_Stitch_marks_required_is_always_present_in_the_projection()
    {
        // The provider schema requires: bodyFont, colorMode, customColor,
        // headlineFont, roundness. A projection missing any of them would be
        // rejected by the provider at request time.
        var theme = ThemeOf(StitchThemeProjector.Project("{}").NativeThemeJson);

        foreach (var required in new[] { "bodyFont", "colorMode", "customColor", "headlineFont", "roundness" })
        {
            theme.TryGetProperty(required, out _).ShouldBeTrue($"'{required}' is required by Stitch");
        }
    }

    [Theory]
    [InlineData("Plus Jakarta Sans", "PLUS_JAKARTA_SANS")]
    [InlineData("  inter  ", "INTER")]
    [InlineData("IBM Plex Sans", "IBM_PLEX_SANS")]
    [InlineData("Source Serif 4", "SOURCE_SERIF_4")]
    public void Font_names_normalize_to_provider_tokens(string input, string expected) =>
        StitchThemeProjector.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Invalid_json_is_rejected_rather_than_silently_producing_a_default_theme()
    {
        Should.Throw<ArgumentException>(() => StitchThemeProjector.Project("not json"));
        Should.Throw<ArgumentException>(() => StitchThemeProjector.Project("[1,2,3]"));
    }

    [Fact]
    public void The_verified_font_enum_matches_the_provider_schema_size()
    {
        // Read from `stitch edit design-system --schema` (@google/stitch 0.11.1).
        // A change here means the provider's surface moved and the projection
        // needs re-verifying.
        StitchThemeProjector.SupportedFonts.Count.ShouldBe(67);
        StitchThemeProjector.SupportedFonts.ShouldContain("INTER");
        StitchThemeProjector.SupportedFonts.ShouldContain("GOOGLE_SANS_FLEX");
    }

    private static JsonElement ThemeOf(string nativeThemeJson) =>
        JsonDocument.Parse(nativeThemeJson).RootElement
            .GetProperty("designSystem").GetProperty("theme");
}
