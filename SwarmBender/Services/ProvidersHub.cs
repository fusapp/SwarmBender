using SwarmBender.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SwarmBender.Services;

/// <summary>
/// Resolves secret *source providers* (env/file) and creates the *engine provider* (Docker CLI)
/// using ops/vars/secrets-provider.yml. Environment variable SB_SECRETS_ENGINE overrides the file.
/// </summary>
public sealed class ProvidersHub : ISecretProvidersHub, ISecretsProviderFactory
{
    private readonly IEnumerable<ISecretSourceProvider> _providers;
    private readonly IYamlLoader _yaml;

    public ProvidersHub(IEnumerable<ISecretSourceProvider> providers, IYamlLoader yaml)
    {
        _providers = providers;
        _yaml = yaml;
    }

    /// <summary>Returns all registered secret source providers (env/file, etc.).</summary>
    public Task<IReadOnlyList<ISecretSourceProvider>> ResolveAsync(string rootPath, CancellationToken ct = default)
    {
        // Minimal v1: return all registered providers.
        var list = _providers.ToList();
        return Task.FromResult<IReadOnlyList<ISecretSourceProvider>>(list);
    }

    /// <summary>
    /// Creates a secrets engine client (currently docker-cli).
    /// Config file layout (ops/vars/secrets-provider.yml):
    /// 
    /// secrets:
    ///   provider: docker-cli
    ///   args:
    ///     dockerPath: docker
    ///     dockerHost: unix:///var/run/docker.sock
    /// 
    /// SB_SECRETS_ENGINE env var overrides 'provider' (e.g., docker-cli).
    /// </summary>
    public async Task<IDockerSecretClient> CreateClientAsync(string providerConfigPath, CancellationToken ct = default)
    {
        var provider = "docker-cli";                  // default
        var dockerPath = "docker";                    // default CLI name
        string? dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST"); // optional

        // Read YAML if present
        if (!string.IsNullOrWhiteSpace(providerConfigPath) && File.Exists(providerConfigPath))
        {
            var y = await _yaml.LoadYamlAsync(providerConfigPath, ct);
            if (y.TryGetValue("secrets", out var sNode) && sNode is IDictionary<string, object?> secrets)
            {
                if (secrets.TryGetValue("provider", out var pVal) && pVal is string pStr && !string.IsNullOrWhiteSpace(pStr))
                    provider = pStr.Trim();

                if (secrets.TryGetValue("args", out var aNode) && aNode is IDictionary<string, object?> args)
                {
                    if (args.TryGetValue("dockerPath", out var dp) && dp is string dpStr && !string.IsNullOrWhiteSpace(dpStr))
                        dockerPath = dpStr.Trim();

                    if (args.TryGetValue("dockerHost", out var dh) && dh is string dhStr && !string.IsNullOrWhiteSpace(dhStr))
                        dockerHost = dhStr.Trim();
                }
            }
        }

        // Environment override (e.g., SB_SECRETS_ENGINE=docker-cli)
        var envOverride = Environment.GetEnvironmentVariable("SB_SECRETS_ENGINE");
        if (!string.IsNullOrWhiteSpace(envOverride))
            provider = envOverride!.Trim();

        switch (provider.ToLowerInvariant())
        {
            case "docker-cli":
            case "docker":
                // Expecting a ctor like: DockerCliSecretClient(string dockerPath, string? dockerHost)
                return new DockerCliSecretClient(dockerPath, dockerHost);

            default:
                throw new NotSupportedException(
                    $"Unknown secrets provider '{provider}'. Only 'docker-cli' is supported in this build.");
        }
    }
}