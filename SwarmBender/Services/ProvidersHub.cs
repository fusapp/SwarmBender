using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Resolves providers based on ops/providers.yml. For now returns built-ins (env, file).</summary>
public sealed class ProvidersHub : ISecretProvidersHub
{
    private readonly IEnumerable<ISecretSourceProvider> _providers;
    public ProvidersHub(IEnumerable<ISecretSourceProvider> providers) => _providers = providers;

    public Task<IReadOnlyList<ISecretSourceProvider>> ResolveAsync(string rootPath, CancellationToken ct = default)
    {
        // Minimal v1: return all registered providers. Later: parse ops/providers.yml for include filters.
        var list = _providers.ToList();
        return Task.FromResult<IReadOnlyList<ISecretSourceProvider>>(list);
    }
}