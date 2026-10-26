namespace AIDesignStudio.Application.Providers;

/// <summary>
/// Marker for a unit of provider functionality. Capabilities are discovered at
/// runtime rather than assumed, because providers differ substantially in what
/// they expose and the same provider's surface changes between releases.
/// </summary>
public interface IProviderCapability
{
}

/// <summary>
/// An AI design provider adapter.
///
/// Deliberately *not* a wide interface with optional members. A provider exposes
/// only the capabilities it actually implements, and callers must ask for one
/// before using it. That makes "Stitch cannot do X" a compile-time-shaped,
/// testable condition instead of a <c>NotSupportedException</c> discovered in
/// production.
///
/// See docs/02-architecture/decisions/ADR-0003-capability-based-provider-adapters.md.
/// </summary>
public interface IDesignProvider
{
    /// <summary>Stable identifier persisted on artifacts, e.g. <c>google-stitch</c>.</summary>
    string ProviderId { get; }

    /// <summary>Capabilities this adapter implements, for diagnostics and UI gating.</summary>
    IReadOnlySet<Type> Capabilities { get; }

    /// <summary>
    /// Returns the requested capability, or null when unsupported.
    /// </summary>
    TCapability? GetCapability<TCapability>() where TCapability : class, IProviderCapability;

    /// <summary>
    /// Reports whether the adapter is configured and able to reach the provider.
    /// Must never throw and must never perform a billable operation.
    /// </summary>
    Task<ProviderReadiness> CheckReadinessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Result of a non-billable readiness probe.
/// </summary>
/// <param name="ProviderId">The probed adapter.</param>
/// <param name="IsInstalled">The adapter's runtime dependency (CLI, SDK) is present.</param>
/// <param name="IsAuthenticated">Credentials are present and accepted.</param>
/// <param name="Detail">Human-readable diagnosis; safe to log and show to operators.</param>
/// <param name="MissingCredentialNames">
/// Names of absent credentials, e.g. <c>STITCH_API_KEY</c>. Names only — never values.
/// </param>
public sealed record ProviderReadiness(
    string ProviderId,
    bool IsInstalled,
    bool IsAuthenticated,
    string Detail,
    IReadOnlyList<string> MissingCredentialNames)
{
    public bool IsUsable => IsInstalled && IsAuthenticated;

    public static ProviderReadiness Unavailable(string providerId, string detail) =>
        new(providerId, IsInstalled: false, IsAuthenticated: false, detail, []);
}

/// <summary>
/// Resolves adapters by id. Registered as a singleton; adapters are stateless.
/// </summary>
public interface IDesignProviderRegistry
{
    IReadOnlyList<IDesignProvider> All { get; }

    IDesignProvider? Find(string providerId);

    /// <summary>Adapters implementing a given capability, for routing a request.</summary>
    IReadOnlyList<IDesignProvider> SupportingCapability<TCapability>()
        where TCapability : class, IProviderCapability;
}

/// <summary>
/// Raised when a provider call fails. <see cref="Code"/> carries the provider's
/// own machine-readable error code where one exists (e.g. Stitch's
/// <c>MISSING_CANVAS_CREDENTIALS</c>), so job retry policy can branch on it
/// rather than on message text.
/// </summary>
public sealed class ProviderException : Exception
{
    public ProviderException(string providerId, string code, string message, bool isRetryable)
        : base(message)
    {
        ProviderId = providerId;
        Code = code;
        IsRetryable = isRetryable;
    }

    public string ProviderId { get; }
    public string Code { get; }
    public bool IsRetryable { get; }
}
