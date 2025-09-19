namespace SwarmBender.Services.Abstractions;

/// <summary>Prunes old versioned secrets based on retain policy.</summary>
public interface ISecretsPruneService
{
    Task<int> PruneAsync(string rootPath, string env, string scope, int retain, bool dryRun, bool quiet, CancellationToken ct = default);
}