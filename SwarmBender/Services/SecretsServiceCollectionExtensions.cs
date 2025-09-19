using Microsoft.Extensions.DependencyInjection;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

public static class SecretsServiceCollectionExtensions
{
    public static IServiceCollection AddSwarmBenderSecrets(this IServiceCollection services)
    {
        services.AddSingleton<SecretPolicyLoader>();
        services.AddSingleton<ISecretNameStrategy, SecretNameStrategy>();
        services.AddSingleton<ISecretProvidersHub, ProvidersHub>();

        // Providers
        services.AddSingleton<ISecretSourceProvider, EnvSecretSourceProvider>();
        services.AddSingleton<ISecretSourceProvider, FileSecretSourceProvider>();

        // Engine (placeholder; implement Docker SDK adapter in the next step)
        services.AddSingleton<IDockerSecretClient, StubDockerSecretClient>();

        // Services
        services.AddSingleton<ISecretsSyncService, SecretsSyncService>();
        services.AddSingleton<ISecretsPruneService, SecretsPruneService>();
        services.AddSingleton<ISecretsDoctorService, SecretsDoctorService>();

        return services;
    }
}

/// <summary>Temporary stub until Docker SDK adapter is added.</summary>
internal sealed class StubDockerSecretClient : IDockerSecretClient
{
    private readonly HashSet<string> _existing = new(System.StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyCollection<string>> ListNamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<string>>(_existing);

    public Task<bool> EnsureCreatedAsync(string name, string content, IDictionary<string,string>? labels, CancellationToken ct = default)
    {
        if (_existing.Contains(name)) return Task.FromResult(false);
        _existing.Add(name);
        return Task.FromResult(true);
    }
}