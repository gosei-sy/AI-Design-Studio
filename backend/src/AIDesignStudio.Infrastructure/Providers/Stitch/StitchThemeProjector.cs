using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDesignStudio.Application.Providers;

namespace AIDesignStudio.Infrastructure.Providers.Stitch;

/// <summary>
/// Projects our approved design system onto Stitch's native theme model.
///
/// The constraint set below was read from the provider itself
/// (<c>stitch edit design-system --schema</c>, @google/stitch 0.11.1) rather than
/// from documentation. Stitch's theme is largely enumerated:
///
///   * bodyFont / headlineFont / labelFont: one of 67 named families.
///   * colorMode: a single value, LIGHT or DARK.
///   * roundness: one of five steps (2/4/8/12/full px).
///   * customColor: one seed colour, plus four optional role overrides.
///   * spacing / typography: free-form string maps (carried across verbatim).
///   * designMd: free-form prose, advisory only.
///
/// A real design system is strictly richer than this. The projection therefore
/// loses information in a known, enumerable way, and every loss is reported as a
/// <see cref="ProjectionWarning"/> rather than silently absorbed. This is the
/// mechanical reason the Design Contract — not provider output — is the source of
/// truth. See ADR-0004.
/// </summary>
public static class StitchThemeProjector
{
    /// <summary>Font families Stitch accepts, verified against the provider schema.</summary>
    public static readonly IReadOnlySet<string> SupportedFonts = new HashSet<string>(StringComparer.Ordinal)
    {
        "BE_VIETNAM_PRO", "EPILOGUE", "INTER", "LEXEND", "MANROPE", "NEWSREADER", "NOTO_SERIF",
        "PLUS_JAKARTA_SANS", "PUBLIC_SANS", "SPACE_GROTESK", "SPLINE_SANS", "WORK_SANS", "DOMINE",
        "LIBRE_CASLON_TEXT", "EB_GARAMOND", "LITERATA", "SOURCE_SERIF_4", "SOURCE_SERIF_FOUR",
        "MONTSERRAT", "METROPHOBIC", "METROPOLIS", "SOURCE_SANS_3", "SOURCE_SANS_THREE", "NUNITO_SANS",
        "ARIMO", "HANKEN_GROTESK", "RUBIK", "GEIST", "DM_SANS", "IBM_PLEX_SANS", "SORA", "ANYBODY",
        "ANTON", "ARCHIVO_NARROW", "ATKINSON_HYPERLEGIBLE_NEXT", "BARLOW_CONDENSED", "BEBAS_NEUE",
        "BODONI_MODA", "BRICOLAGE_GROTESQUE", "CHIVO", "CLIMATE_CRISIS", "COMFORTAA", "COURIER_PRIME",
        "FIRA_SANS", "GOOGLE_SANS", "GOOGLE_SANS_CODE", "GOOGLE_SANS_FLEX", "GOOGLE_SANS_MONO",
        "GOOGLE_SANS_TEXT", "IBM_PLEX_SERIF", "JETBRAINS_MONO", "KARLA", "LIBRE_FRANKLIN",
        "MERRIWEATHER", "NOTO_SANS", "OPEN_SANS", "OSWALD", "OUTFIT", "PLAYFAIR_DISPLAY",
        "POIRET_ONE", "QUESTRIAL", "QUICKSAND", "RALEWAY", "ROBOTO_FLEX", "SPACE_MONO", "SYNE",
        "VOLLKORN",
    };

    /// <summary>Fallback when a requested family has no Stitch equivalent.</summary>
    public const string FallbackFont = "INTER";

    /// <summary>Roundness steps Stitch accepts, as (token, pixel value).</summary>
    private static readonly (string Token, int Px)[] RoundnessSteps =
    [
        ("ROUND_TWO", 2),
        ("ROUND_FOUR", 4),
        ("ROUND_EIGHT", 8),
        ("ROUND_TWELVE", 12),
    ];

    /// <summary>
    /// Token paths in our design system that Stitch's theme model cannot express
    /// at all. Presence of any of these produces a warning so reviewers know the
    /// provider preview will not reflect them.
    /// </summary>
    private static readonly (string Pointer, string Reason)[] InexpressibleSections =
    [
        ("/elevation", "Stitch's theme model has no elevation or shadow tokens."),
        ("/motion", "Stitch's theme model has no motion or easing tokens."),
        ("/breakpoints", "Stitch targets a device enum, not author-defined breakpoints."),
        ("/components", "Stitch has no component-variant model; component rules stay studio-side."),
        ("/borders", "Stitch exposes roundness only; border width and style are not themeable."),
    ];

    public static ThemeProjection Project(string designSystemTokensJson)
    {
        var warnings = new List<ProjectionWarning>();

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(designSystemTokensJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Design system tokens are not valid JSON: {ex.Message}",
                nameof(designSystemTokensJson));
        }

        if (root is not JsonObject tokens)
        {
            throw new ArgumentException("Design system tokens must be a JSON object.", nameof(designSystemTokensJson));
        }

        var typography = tokens["typography"] as JsonObject;
        var color = tokens["color"] as JsonObject;

        // Roles and modes are separate: every role is a hex colour, modes is an
        // enum array. See design-system.schema.json and M00-R3-002.
        var roles = color?["roles"] as JsonObject;
        var shape = tokens["shape"] as JsonObject;

        var theme = new JsonObject
        {
            ["bodyFont"] = MapFont(typography?["bodyFont"]?.GetValue<string>(), "/typography/bodyFont", warnings),
            ["headlineFont"] = MapFont(
                typography?["headlineFont"]?.GetValue<string>(), "/typography/headlineFont", warnings),
            ["colorMode"] = MapColorMode(color, warnings),
            ["customColor"] = MapSeedColor(roles, warnings),
            ["roundness"] = MapRoundness(shape?["radiusBase"]?.GetValue<string>(), warnings),
        };

        var labelFont = typography?["labelFont"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(labelFont))
        {
            theme["labelFont"] = MapFont(labelFont, "/typography/labelFont", warnings);
        }

        MapRoleOverrides(roles, theme, warnings);
        CarryFreeFormMaps(tokens, theme);
        ReportInexpressibleSections(tokens, warnings);

        var payload = new JsonObject
        {
            ["designSystem"] = new JsonObject
            {
                ["displayName"] = tokens["name"]?.GetValue<string>() ?? "AI Design Studio",
                ["theme"] = theme,
            },
        };

        return new ThemeProjection(StitchDesignProvider.Id, payload.ToJsonString(), warnings);
    }

    /// <summary>
    /// Normalizes a CSS family name to a Stitch font token, e.g. "Plus Jakarta
    /// Sans" to PLUS_JAKARTA_SANS. Falls back with a warning when there is no
    /// equivalent — the common case for licensed brand fonts.
    /// </summary>
    private static string MapFont(string? requested, string pointer, List<ProjectionWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return FallbackFont;
        }

        var token = Normalize(requested);

        if (SupportedFonts.Contains(token))
        {
            return token;
        }

        warnings.Add(new ProjectionWarning(
            pointer,
            requested,
            FallbackFont,
            $"'{requested}' is not one of Stitch's {SupportedFonts.Count} supported families. " +
            $"Substituted {FallbackFont}; the provider preview will not use the brand font."));

        return FallbackFont;
    }

    internal static string Normalize(string family) =>
        new string([.. family.Trim().ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_')])
            .Trim('_');

    /// <summary>
    /// Stitch carries a single colorMode. A design system that defines both light
    /// and dark cannot be represented in one Stitch design system, so the second
    /// mode is reported as lost.
    /// </summary>
    private static string MapColorMode(JsonObject? color, List<ProjectionWarning> warnings)
    {
        var modes = (color?["modes"] as JsonArray)?
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.ToUpperInvariant())
            .ToList() ?? [];

        if (modes.Count > 1)
        {
            warnings.Add(new ProjectionWarning(
                "/color/modes",
                string.Join(", ", modes),
                modes[0],
                "Stitch's theme carries one colorMode. Only the first mode is applied; " +
                "the remaining mode(s) need a separate Stitch design system."));
        }

        var single = modes.FirstOrDefault();

        return single is "DARK" ? "DARK" : "LIGHT";
    }

    private static string MapSeedColor(JsonObject? roles, List<ProjectionWarning> warnings)
    {
        var primary = roles?["primary"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(primary))
        {
            warnings.Add(new ProjectionWarning(
                "/color/roles/primary",
                "(absent)",
                "#000000",
                "Stitch requires a seed colour. The design system did not specify a primary colour."));

            return "#000000";
        }

        return primary.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Maps semantic colour roles onto Stitch's four override slots. Any role
    /// beyond those four is dropped with a warning.
    /// </summary>
    private static void MapRoleOverrides(JsonObject? roles, JsonObject theme, List<ProjectionWarning> warnings)
    {
        if (roles is null)
        {
            return;
        }

        var slots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["secondary"] = "overrideSecondaryColor",
            ["tertiary"] = "overrideTertiaryColor",
            ["neutral"] = "overrideNeutralColor",
        };

        foreach (var (role, slot) in slots)
        {
            var value = roles[role]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(value))
            {
                theme[slot] = value.Trim().ToLowerInvariant();
            }
        }

        // Every other role has no home in the Stitch theme.
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "primary", "secondary", "tertiary", "neutral",
        };

        foreach (var property in roles)
        {
            if (known.Contains(property.Key) || property.Value is null)
            {
                continue;
            }

            warnings.Add(new ProjectionWarning(
                $"/color/roles/{property.Key}",
                property.Value.ToJsonString(),
                null,
                $"Stitch has no theme slot for the '{property.Key}' colour role. " +
                "It is enforced studio-side only and will not appear in the provider preview."));
        }
    }

    /// <summary>
    /// Snaps a CSS radius to the nearest Stitch roundness step, warning when the
    /// requested value is not exactly representable.
    ///
    /// Equidistant values (10px is 2px from both 8 and 12) resolve to the smaller
    /// step. <see cref="RoundnessSteps"/> is ascending and <c>MinBy</c> keeps the
    /// first minimum, so the rule is enforced by ordering: under-rounding is the
    /// safer visual error, since over-rounding changes a component's character
    /// more than it changes its size.
    /// </summary>
    private static string MapRoundness(string? radiusBase, List<ProjectionWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(radiusBase))
        {
            return "ROUND_EIGHT";
        }

        var raw = radiusBase.Trim();

        if (raw.Equals("9999px", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return "ROUND_FULL";
        }

        var digits = new string([.. raw.TakeWhile(c => char.IsDigit(c) || c == '.')]);

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
        {
            warnings.Add(new ProjectionWarning(
                "/shape/radiusBase", raw, "ROUND_EIGHT",
                $"Could not interpret '{raw}' as a pixel radius; defaulted to ROUND_EIGHT."));

            return "ROUND_EIGHT";
        }

        var nearest = RoundnessSteps.MinBy(s => Math.Abs(s.Px - px));

        if (Math.Abs(nearest.Px - px) > 0.001)
        {
            warnings.Add(new ProjectionWarning(
                "/shape/radiusBase", raw, nearest.Token,
                $"Stitch exposes roundness as a five-step enum. {raw} snapped to {nearest.Token} ({nearest.Px}px)."));
        }

        return nearest.Token;
    }

    /// <summary>
    /// Stitch accepts free-form <c>spacing</c> and <c>typography</c> string maps,
    /// so these carry across without loss.
    /// </summary>
    private static void CarryFreeFormMaps(JsonObject tokens, JsonObject theme)
    {
        if (tokens["spacing"] is JsonObject spacing && spacing.Count > 0)
        {
            theme["spacing"] = spacing.DeepClone();
        }

        if (tokens["typography"]?["scale"] is JsonObject scale && scale.Count > 0)
        {
            theme["typography"] = scale.DeepClone();
        }
    }

    private static void ReportInexpressibleSections(JsonObject tokens, List<ProjectionWarning> warnings)
    {
        foreach (var (pointer, reason) in InexpressibleSections)
        {
            var key = pointer.TrimStart('/');

            if (tokens[key] is not null)
            {
                warnings.Add(new ProjectionWarning(pointer, "(present)", null, reason));
            }
        }
    }
}
