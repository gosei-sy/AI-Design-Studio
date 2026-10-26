using AIDesignStudio.Application.Providers;

namespace AIDesignStudio.Infrastructure.Providers;

/// <summary>
/// Resolves provider adapters by id and by capability.
/// </summary>
public sealed class DesignProviderRegistry(IEnumerable<IDesignProvider> providers) : IDesignProviderRegistry
{
    private readonly Dictionary<string, IDesignProvider> _byId =
        providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IDesignProvider> All => [.. _byId.Values];

    public IDesignProvider? Find(string providerId) =>
        providerId is not null && _byId.TryGetValue(providerId, out var provider) ? provider : null;

    public IReadOnlyList<IDesignProvider> SupportingCapability<TCapability>()
        where TCapability : class, IProviderCapability =>
        [.. _byId.Values.Where(p => p.Capabilities.Contains(typeof(TCapability)))];
}
