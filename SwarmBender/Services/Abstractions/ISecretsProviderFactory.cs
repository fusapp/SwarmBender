namespace SwarmBender.Services.Abstractions;

public interface ISecretsProviderFactory
{
    /// <summary>
    /// Creates a secrets engine client using provider config (e.g. ops/vars/secrets-provider.yml).
    /// </summary>
    Task<IDockerSecretClient> CreateClientAsync(string providerConfigPath, CancellationToken ct = default);
}