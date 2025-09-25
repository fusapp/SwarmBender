using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Providers.Azure;


/// <summary>
/// Reads secrets from Azure Key Vault using the official SDK.
/// </summary>
public interface IAzureKvCollector
{
    public interface IAzureKvCollector
    {
        // Eski: CollectAsync(SbConfig.ProvidersSection.AzureKv cfg, ...)
        Task<Dictionary<string, string>> CollectAsync(
            ProvidersAzureKv cfg,
            string env,
            CancellationToken ct);
    }
}