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
        services.AddSingleton<ISecretsProviderFactory, ProvidersHub>();

        // Providers
        services.AddSingleton<ISecretSourceProvider, EnvSecretSourceProvider>();
        services.AddSingleton<ISecretSourceProvider, FileSecretSourceProvider>();
        services.AddSingleton<ISecretSourceProvider, AzureKeyVaultSecretSourceProvider>();
        services.AddSingleton<ISecretSourceProvider, InfisicalSecretSourceProvider>();

        // Engine (placeholder; implement Docker SDK adapter in the next step)
        services.AddSingleton<IDockerSecretClient, StubDockerSecretClient>();
        services.AddSingleton<IInfisicalClient, InfisicalClient>();

        // Services
        services.AddSingleton<ISecretsSyncService, SecretsSyncService>();
        services.AddSingleton<ISecretsPruneService, SecretsPruneService>();
        services.AddSingleton<ISecretsDoctorService, SecretsDoctorService>();
        services.AddSingleton<ISecretsRotateService, SecretsRotateService>(); 

        // Engine selection via ENV:
        //   SB_SECRETS_ENGINE=docker-cli
        //   SB_DOCKER_PATH=/usr/local/bin/docker (optional)
        //   SB_DOCKER_HOST=unix:///var/run/docker.sock (optional; or Windows npipe)
        var engine = (Environment.GetEnvironmentVariable("SB_SECRETS_ENGINE") ?? "stub").Trim();

        if (string.Equals(engine, "docker-cli", StringComparison.OrdinalIgnoreCase))
        {
            var path = Environment.GetEnvironmentVariable("SB_DOCKER_PATH");
            var host = Environment.GetEnvironmentVariable("SB_DOCKER_HOST");
            services.AddSingleton<IDockerSecretClient>(_ => new DockerCliSecretClient(path, host));
        }
        else
        {
            services.AddSingleton<IDockerSecretClient, StubDockerSecretClient>();
        }

        // Services
        services.AddSingleton<ISecretsSyncService, SecretsSyncService>();
        services.AddSingleton<ISecretsPruneService, SecretsPruneService>();
        services.AddSingleton<ISecretsDoctorService, SecretsDoctorService>();
        
        return services;
    }
}
internal sealed class StubDockerSecretClient : IDockerSecretClient
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset CreatedAt, Dictionary<string,string> Labels)> _meta
        = new(StringComparer.Ordinal);

    public Task<IReadOnlyCollection<string>> ListNamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<string>>(_names.ToList());

    public Task<IReadOnlyCollection<DockerSecretInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        var list = _names.Select(n =>
        {
            if (_meta.TryGetValue(n, out var m))
                return new DockerSecretInfo(n, m.CreatedAt, m.Labels);
            return new DockerSecretInfo(n, DateTimeOffset.UtcNow, new Dictionary<string,string>());
        }).ToList();
        return Task.FromResult<IReadOnlyCollection<DockerSecretInfo>>(list);
    }

    public Task<bool> EnsureCreatedAsync(string name, string content, IDictionary<string, string>? labels, CancellationToken ct = default)
    {
        if (_names.Contains(name)) return Task.FromResult(false);
        _names.Add(name);
        _meta[name] = (DateTimeOffset.UtcNow, new Dictionary<string, string>(labels ?? new Dictionary<string,string>(), StringComparer.OrdinalIgnoreCase));
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        var removed = _names.Remove(name);
        _meta.Remove(name);
        return Task.FromResult(removed);
    }
}