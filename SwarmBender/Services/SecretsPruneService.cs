using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

public sealed class SecretsPruneService : ISecretsPruneService
{
    public Task<int> PruneAsync(string rootPath, string env, string scope, int retain, bool dryRun, bool quiet, CancellationToken ct = default)
    {
        // TODO: Implement based on IDockerSecretClient + labels
        return Task.FromResult(0);
    }
}