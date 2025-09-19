using SwarmBender.Services.Models;

namespace SwarmBender.Services.Abstractions;

/// <summary>Synchronizes secrets from providers into Docker Swarm and writes the secrets map file.</summary>
public interface ISecretsSyncService
{
    Task<SecretsSyncResult> SyncAsync(SecretsSyncRequest request, CancellationToken ct = default);
}