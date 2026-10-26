using System.Text.Json;
using AIDesignStudio.Application.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIDesignStudio.Infrastructure.Providers.Stitch;

/// <summary>
/// Google Stitch adapter, driving the <c>@google/stitch</c> CLI.
///
/// Why the CLI and not an HTTP client: as of @google/stitch 0.11.1 the package's
/// only library export is <c>defineIngest</c>. There is no published client
/// library for canvas operations, so the CLI (which every command exposes with
/// <c>--json</c> and <c>--schema</c>) is the supported programmatic surface.
/// See docs/06-spikes/SPIKE-01-stitch-auth-and-capabilities.md.
///
/// Verified capability surface is recorded in that spike. Capabilities this
/// adapter does *not* claim are simply absent from <see cref="Capabilities"/>,
/// so callers discover the gap by asking rather than by catching.
/// </summary>
public sealed class StitchDesignProvider :
    IDesignProvider,
    IScreenGenerationCapability,
    IScreenImportCapability,
    IScreenRetrievalCapability,
    IDesignSystemProjectionCapability,
    IRouteCaptureCapability
{
    public const string Id = "google-stitch";

    private readonly IStitchCommandRunner _runner;
    private readonly StitchOptions _options;
    private readonly ILogger<StitchDesignProvider> _logger;

    public StitchDesignProvider(
        IStitchCommandRunner runner,
        IOptions<StitchOptions> options,
        ILogger<StitchDesignProvider> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderId => Id;

    public IReadOnlySet<Type> Capabilities { get; } = new HashSet<Type>
    {
        typeof(IScreenGenerationCapability),
        typeof(IScreenImportCapability),
        typeof(IScreenRetrievalCapability),
        typeof(IDesignSystemProjectionCapability),
        typeof(IRouteCaptureCapability),

        // Deliberately absent: IScreenVariantCapability. The CLI supports
        // `generate variants`, but the adapter has no verified response mapping
        // for it yet (no credentials during M00), so claiming it would be a lie.
        // Tracked as M01-07.
    };

    public TCapability? GetCapability<TCapability>() where TCapability : class, IProviderCapability =>
        Capabilities.Contains(typeof(TCapability)) ? this as TCapability : null;

    // -----------------------------------------------------------------------
    // Readiness
    // -----------------------------------------------------------------------

    /// <summary>
    /// Probes readiness with <c>stitch status --json</c>, which contacts no
    /// billable endpoint and succeeds even when unauthenticated.
    ///
    /// Stitch has two independent credentials and both matter: a Google OAuth
    /// sign-in, and a separate <c>STITCH_API_KEY</c> required for canvas
    /// operations (projects, screens, captures, design systems). Being signed in
    /// is not sufficient, which is why the two are reported separately.
    /// </summary>
    public async Task<ProviderReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        StitchResult result;

        try
        {
            result = await _runner.RunAsync(["status", "--json"], cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stitch readiness probe threw");
            return ProviderReadiness.Unavailable(Id, $"Readiness probe failed: {ex.Message}");
        }

        if (!result.Success || result.Data is null)
        {
            return result.ErrorCode == "CLI_NOT_FOUND"
                ? ProviderReadiness.Unavailable(
                    Id, "The stitch CLI is not installed or not on PATH. Install @google/stitch.")
                : ProviderReadiness.Unavailable(Id, result.ErrorMessage ?? "Readiness probe failed.");
        }

        var data = result.Data.Value;

        // The runner treats any valid bare JSON as success, so `[]`, `null`, a
        // string or a number can all arrive here. TryGetProperty throws
        // InvalidOperationException on a non-object, which would break this
        // method's "must never throw" contract from outside the try block above.
        if (data.ValueKind != JsonValueKind.Object)
        {
            return ProviderReadiness.Unavailable(
                Id,
                $"The stitch CLI returned {data.ValueKind} where a status object was expected. " +
                "The CLI's output format may have changed.");
        }

        var oauth = data.TryGetProperty("authenticated", out var a) && a.ValueKind == JsonValueKind.True;

        var canvasPresent = data.TryGetProperty("canvas", out var canvas)
            && canvas.ValueKind == JsonValueKind.Object
            && canvas.TryGetProperty("present", out var p)
            && p.ValueKind == JsonValueKind.True;

        var missing = new List<string>();

        if (!oauth)
        {
            missing.Add("google-oauth");
        }

        if (!canvasPresent)
        {
            missing.Add("STITCH_API_KEY");
        }

        var detail = (oauth, canvasPresent) switch
        {
            (true, true) => "Signed in and canvas credential present.",
            (true, false) => "Signed in, but no canvas credential. Projects, screens and design systems are unavailable.",
            (false, true) => "Canvas credential present, but not signed in to Google.",
            (false, false) => "Not signed in and no canvas credential.",
        };

        return new ProviderReadiness(Id, IsInstalled: true, IsAuthenticated: oauth && canvasPresent, detail, missing);
    }

    // -----------------------------------------------------------------------
    // Screen generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves what a generation request would do, using the CLI's
    /// <c>--dry-run</c>. Verified to work with no credentials and no network, so
    /// request shaping is testable in CI at zero cost.
    /// </summary>
    public async Task<GenerationPlan> PlanScreenAsync(
        GenerateScreenRequest request, CancellationToken cancellationToken)
    {
        var args = BuildGenerateScreenArgs(request);
        args.Add("--dry-run");

        var result = await _runner.RunAsync(args, cancellationToken);

        if (!result.Success || result.Data is null)
        {
            throw Fail(result, retryableByDefault: false);
        }

        var steps = new List<PlannedStep>();

        if (result.Data.Value.TryGetProperty("requests", out var requests) &&
            requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in requests.EnumerateArray())
            {
                var operation = step.TryGetProperty("tool", out var tool)
                    ? tool.GetString() ?? "unknown"
                    : "unknown";

                var arguments = new Dictionary<string, string>(StringComparer.Ordinal);

                if (step.TryGetProperty("args", out var stepArgs) && stepArgs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in stepArgs.EnumerateObject())
                    {
                        arguments[prop.Name] = prop.Value.ToString();
                    }
                }

                steps.Add(new PlannedStep(operation, arguments));
            }
        }

        return new GenerationPlan(Id, steps, IsBillable: true);
    }

    public async Task<GeneratedScreen> GenerateScreenAsync(
        GenerateScreenRequest request, CancellationToken cancellationToken)
    {
        EnsureBillableAllowed(nameof(GenerateScreenAsync));

        var result = await _runner.RunAsync(BuildGenerateScreenArgs(request), cancellationToken);

        if (!result.Success || result.Data is null)
        {
            throw Fail(result, retryableByDefault: true);
        }

        var data = result.Data.Value;

        return new GeneratedScreen(
            ProviderId: Id,
            ProviderScreenId: ReadString(data, "id") ?? throw Malformed("id"),
            ProviderProjectId: ReadString(data, "project") ?? request.ProjectRef ?? string.Empty,
            CanvasUrl: ReadString(data, "canvasUrl"));
    }

    private static List<string> BuildGenerateScreenArgs(GenerateScreenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("A generation prompt is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ProjectRef) && string.IsNullOrWhiteSpace(request.NewProjectTitle))
        {
            throw new ArgumentException(
                "Either an existing project reference or a new project title is required.", nameof(request));
        }

        var args = new List<string> { "generate", "screen", "--prompt", request.Prompt };

        if (!string.IsNullOrWhiteSpace(request.ProjectRef))
        {
            args.AddRange(["--project", request.ProjectRef]);
        }
        else
        {
            args.AddRange(["--new-project", request.NewProjectTitle!]);
        }

        args.AddRange(["--device", ToStitchDevice(request.Device)]);

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            args.AddRange(["--title", request.Title]);
        }

        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            args.AddRange(["--model", request.ModelId]);
        }

        args.Add("--json");
        return args;
    }

    /// <summary>
    /// Maps our device enum to Stitch's. All four of our values exist in the
    /// provider's schema, so this mapping is lossless.
    /// </summary>
    internal static string ToStitchDevice(TargetDevice device) => device switch
    {
        TargetDevice.Mobile => "MOBILE",
        TargetDevice.Desktop => "DESKTOP",
        TargetDevice.Tablet => "TABLET",
        TargetDevice.Agnostic => "AGNOSTIC",
        _ => throw new ArgumentOutOfRangeException(nameof(device), device, "Unsupported target device."),
    };

    // -----------------------------------------------------------------------
    // Import and retrieval
    // -----------------------------------------------------------------------

    /// <summary>
    /// Uploads an existing HTML snapshot or image as a screen.
    ///
    /// This is the supported path for redesign work. Google's own CLI guidance is
    /// explicit that code import must capture and upload real views rather than
    /// generate them, because generation would invent UI that the existing
    /// application does not have.
    /// </summary>
    public async Task<ImportedScreen> ImportScreenAsync(
        ImportScreenRequest request, CancellationToken cancellationToken)
    {
        EnsureBillableAllowed(nameof(ImportScreenAsync));

        var args = new List<string> { "upload", "screen", request.FilePath, "--project", request.ProjectRef };

        if (!string.IsNullOrWhiteSpace(request.Route))
        {
            args.AddRange(["--route", request.Route]);
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            args.AddRange(["--title", request.Title]);
        }

        args.Add("--json");

        var result = await _runner.RunAsync(args, cancellationToken);

        if (!result.Success || result.Data is null)
        {
            throw Fail(result, retryableByDefault: true);
        }

        var data = result.Data.Value;

        return new ImportedScreen(
            ProviderId: Id,
            ProviderScreenId: ReadString(data, "id") ?? throw Malformed("id"),
            ProviderProjectId: ReadString(data, "project") ?? request.ProjectRef,
            CanvasUrl: ReadString(data, "canvasUrl"));
    }

    public async Task<RetrievedScreen> GetScreenAsync(
        string projectRef, string screenRef, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            ["get", "screen", screenRef, "--project", projectRef, "--json"], cancellationToken);

        if (!result.Success || result.Data is null)
        {
            throw Fail(result, retryableByDefault: true);
        }

        var data = result.Data.Value;

        return new RetrievedScreen(
            ProviderId: Id,
            ProviderScreenId: screenRef,
            Markup: ReadString(data, "html") ?? ReadString(data, "markup"),
            RenderUrl: ReadString(data, "imageUrl") ?? ReadString(data, "renderUrl"),
            RawJson: data.GetRawText());
    }

    public async Task<CapturedRoute> CaptureRouteAsync(
        CaptureRouteRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string> { "capture", request.Url, "-o", request.OutputPath };

        if (!string.IsNullOrWhiteSpace(request.AuthStatePath))
        {
            args.AddRange(["--auth", request.AuthStatePath]);
        }

        args.Add("--json");

        var result = await _runner.RunAsync(args, cancellationToken);

        if (!result.Success || result.Data is null)
        {
            throw Fail(result, retryableByDefault: true);
        }

        var data = result.Data.Value;

        return new CapturedRoute(
            ProviderId: Id,
            OutputPath: ReadString(data, "output") ?? request.OutputPath,
            VerificationImagePath: ReadString(data, "verifyImage"));
    }

    // -----------------------------------------------------------------------
    // Design system projection
    // -----------------------------------------------------------------------

    public ThemeProjection ProjectDesignSystem(string designSystemTokensJson) =>
        StitchThemeProjector.Project(designSystemTokensJson);

    public async Task ApplyDesignSystemAsync(
        string projectRef, ThemeProjection projection, CancellationToken cancellationToken)
    {
        EnsureBillableAllowed(nameof(ApplyDesignSystemAsync));

        var result = await _runner.RunAsync(
            ["edit", "design-system", "--project", projectRef, "--json", projection.NativeThemeJson],
            cancellationToken);

        if (!result.Success)
        {
            throw Fail(result, retryableByDefault: true);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Guards every operation that costs money. Enabled per environment so a
    /// misconfigured local or CI run cannot spend against the account.
    /// </summary>
    private void EnsureBillableAllowed(string operation)
    {
        if (!_options.BillableOperationsEnabled)
        {
            throw new ProviderException(
                Id,
                "BILLABLE_OPERATIONS_DISABLED",
                $"{operation} would incur provider cost, and billable operations are disabled in this " +
                "environment. Set Providers:Stitch:BillableOperationsEnabled to enable it.",
                isRetryable: false);
        }
    }

    /// <summary>
    /// Converts a CLI failure into a <see cref="ProviderException"/>, deciding
    /// retryability from the provider's own error code. Credential and validation
    /// failures are not retried: retrying them wastes attempts and, for billable
    /// calls, money.
    /// </summary>
    private ProviderException Fail(StitchResult result, bool retryableByDefault)
    {
        var code = result.ErrorCode ?? "UNKNOWN";

        var retryable = code switch
        {
            "MISSING_CANVAS_CREDENTIALS" => false,
            "CLI_NOT_FOUND" => false,
            "CLI_UNPARSEABLE_OUTPUT" => false,
            "INVALID_ARGUMENT" => false,
            "PERMISSION_DENIED" => false,
            "CLI_TIMEOUT" => true,
            "RESOURCE_EXHAUSTED" => true,
            "UNAVAILABLE" => true,
            _ => retryableByDefault,
        };

        return new ProviderException(
            Id, code, result.ErrorMessage ?? "The stitch CLI reported a failure.", retryable);
    }

    private static ProviderException Malformed(string field) =>
        new(Id, "CLI_MALFORMED_RESPONSE", $"The stitch response did not contain '{field}'.", isRetryable: false);

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
