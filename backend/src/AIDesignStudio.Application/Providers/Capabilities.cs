namespace AIDesignStudio.Application.Providers;

public enum TargetDevice
{
    Mobile = 1,
    Desktop = 2,
    Tablet = 3,
    Agnostic = 4,
}

/// <summary>
/// Generates a new screen from a text prompt. Billable.
/// </summary>
public interface IScreenGenerationCapability : IProviderCapability
{
    Task<GeneratedScreen> GenerateScreenAsync(GenerateScreenRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Validates and resolves a request without contacting the provider or
    /// incurring cost. Used by tests and by the API's preflight check so an
    /// invalid request is rejected before a job is enqueued.
    /// </summary>
    Task<GenerationPlan> PlanScreenAsync(GenerateScreenRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Explores variants of an existing provider-side screen. Billable.
/// </summary>
public interface IScreenVariantCapability : IProviderCapability
{
    Task<IReadOnlyList<GeneratedScreen>> GenerateVariantsAsync(
        GenerateVariantsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Uploads an existing artifact (screenshot or markup snapshot) to the provider.
///
/// This is the correct entry point for redesign work. Importing is explicitly
/// *not* generation: see ADR-0006 and SPIKE-03.
/// </summary>
public interface IScreenImportCapability : IProviderCapability
{
    Task<ImportedScreen> ImportScreenAsync(ImportScreenRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Retrieves the current artifact for a provider-side screen.
/// </summary>
public interface IScreenRetrievalCapability : IProviderCapability
{
    Task<RetrievedScreen> GetScreenAsync(string projectRef, string screenRef, CancellationToken cancellationToken);
}

/// <summary>
/// Projects our approved Design System onto the provider's native theme model.
///
/// The projection is lossy in one direction only: our token set is the source of
/// truth and the provider receives a reduction of it. <see cref="ThemeProjection.Warnings"/>
/// enumerates what could not be expressed, and those warnings are surfaced to
/// reviewers rather than swallowed.
/// </summary>
public interface IDesignSystemProjectionCapability : IProviderCapability
{
    ThemeProjection ProjectDesignSystem(string designSystemTokensJson);

    Task ApplyDesignSystemAsync(
        string projectRef, ThemeProjection projection, CancellationToken cancellationToken);
}

/// <summary>
/// Captures a route from a running application as a self-contained artifact.
/// </summary>
public interface IRouteCaptureCapability : IProviderCapability
{
    Task<CapturedRoute> CaptureRouteAsync(CaptureRouteRequest request, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------
// Request / response shapes
// ---------------------------------------------------------------------------

public sealed record GenerateScreenRequest(
    string Prompt,
    TargetDevice Device,
    string? ProjectRef,
    string? NewProjectTitle,
    string? Title,
    string? ModelId);

public sealed record GenerateVariantsRequest(
    string ProjectRef,
    string ScreenRef,
    string Prompt,
    int Count,
    string? CreativeRange,
    IReadOnlyList<string> Aspects);

public sealed record ImportScreenRequest(
    string ProjectRef,
    string FilePath,
    string? Route,
    string? Title);

public sealed record CaptureRouteRequest(
    string Url,
    string OutputPath,
    string? AuthStatePath);

/// <summary>
/// A screen produced by a provider.
/// </summary>
/// <param name="ProviderId">Adapter that produced it.</param>
/// <param name="ProviderScreenId">Provider-side screen identifier.</param>
/// <param name="ProviderProjectId">Provider-side project identifier.</param>
/// <param name="CanvasUrl">Deep link to the provider's editor, resolved by the provider (never constructed).</param>
public sealed record GeneratedScreen(
    string ProviderId,
    string ProviderScreenId,
    string ProviderProjectId,
    string? CanvasUrl);

public sealed record ImportedScreen(
    string ProviderId,
    string ProviderScreenId,
    string ProviderProjectId,
    string? CanvasUrl);

/// <param name="Markup">HTML markup when the provider exposes it; null otherwise.</param>
/// <param name="RenderUrl">URL of a rendered preview when available.</param>
public sealed record RetrievedScreen(
    string ProviderId,
    string ProviderScreenId,
    string? Markup,
    string? RenderUrl,
    string RawJson);

public sealed record CapturedRoute(
    string ProviderId,
    string OutputPath,
    string? VerificationImagePath);

/// <summary>
/// The provider-native theme payload plus an explicit account of what our token
/// set could not express in it.
/// </summary>
/// <param name="ProviderId">Target adapter.</param>
/// <param name="NativeThemeJson">Payload in the provider's own schema.</param>
/// <param name="Warnings">
/// One entry per token or rule that was dropped, approximated, or snapped to a
/// provider enum. Non-empty is the normal case, not an error.
/// </param>
public sealed record ThemeProjection(
    string ProviderId,
    string NativeThemeJson,
    IReadOnlyList<ProjectionWarning> Warnings);

/// <param name="TokenPath">JSON pointer into our token set, e.g. <c>/typography/body/fontFamily</c>.</param>
/// <param name="RequestedValue">What the approved design system specifies.</param>
/// <param name="AppliedValue">What the provider will actually receive, or null if dropped.</param>
/// <param name="Reason">Why it could not be carried across verbatim.</param>
public sealed record ProjectionWarning(
    string TokenPath,
    string RequestedValue,
    string? AppliedValue,
    string Reason);

/// <summary>
/// A validated, cost-free preview of what a generation request would do.
/// </summary>
/// <param name="ProviderId">Adapter that produced the plan.</param>
/// <param name="Steps">Ordered provider operations that would be performed.</param>
/// <param name="IsBillable">Whether executing the plan would incur provider cost.</param>
public sealed record GenerationPlan(
    string ProviderId,
    IReadOnlyList<PlannedStep> Steps,
    bool IsBillable);

public sealed record PlannedStep(string Operation, IReadOnlyDictionary<string, string> Arguments);
