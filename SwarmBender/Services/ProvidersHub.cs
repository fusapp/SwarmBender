using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Resolves providers based on ops/providers.yml. For now supports ordering via 'sources: [infisical, file, env]'.</summary>
public sealed class ProvidersHub : ISecretProvidersHub, ISecretsProviderFactory
{
    private readonly IEnumerable<ISecretSourceProvider> _providers;
    private readonly IYamlLoader _yaml;

    public ProvidersHub(IEnumerable<ISecretSourceProvider> providers, IYamlLoader yaml)
        => (_providers, _yaml) = (providers, yaml);

    public async Task<IReadOnlyList<ISecretSourceProvider>> ResolveAsync(string rootPath, CancellationToken ct = default)
    {
        var order = await LoadOrderAsync(rootPath, ct);
        var map = _providers.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);

        if (order.Count == 0)
            return _providers.ToList(); // fallback: all registered

        var list = new List<ISecretSourceProvider>();
        foreach (var t in order)
            if (map.TryGetValue(t, out var prov))
                list.Add(prov);
        return list;
    }

    public Task<IDockerSecretClient> CreateClientAsync(string providerConfigPath, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    private async Task<List<string>> LoadOrderAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", "providers", "providers.yml");
        if (!File.Exists(path)) return new List<string>();
        var y = await _yaml.LoadYamlAsync(path, ct);
        if (y.TryGetValue("sources", out var node) && node is IEnumerable<object?> list)
        {
            return list.Select(x => x?.ToString() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        return new List<string>();
    }
}