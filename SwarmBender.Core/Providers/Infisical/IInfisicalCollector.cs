using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Providers.Infisical;


/// <summary>
/// Reads plaintext secrets from Infisical using the official SDK (machine identity / token).
/// </summary>
public interface IInfisicalCollector
{
    Task<Dictionary<string, string>> CollectAsync(
        ProvidersInfisical cfg,
        string stackId,
        string env,
        CancellationToken ct);
}