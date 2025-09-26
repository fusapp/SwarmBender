using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Abstractions;

public interface ISecretDiscovery
{
    Task<IReadOnlyList<DiscoveredSecret>> DiscoverAsync(
        string repoRoot, string stackId, string env, SbConfig cfg, CancellationToken ct);
}

public sealed record DiscoveredSecret(
    string Scope,           // "<stack>_<service>"  (render ile aynı)
    string Key,             // flatten anahtar (örn: ConnectionStrings__Main)
    string Value,           // içerik
    string Version,         // "content-sha" → 16 hex ya da "v1"
    string ExternalName     // sb_<scope>_<env>_<key>_<version>
);