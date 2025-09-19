namespace SwarmBender.Services.Abstractions;

/// <summary>Resolves providers defined in ops/providers.yml and returns active provider instances.</summary>
public interface ISecretProvidersHub
{
    Task<IReadOnlyList<ISecretSourceProvider>> ResolveAsync(string rootPath, CancellationToken ct = default);
}