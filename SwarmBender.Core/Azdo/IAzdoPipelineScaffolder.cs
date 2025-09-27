using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Azdo;


public interface IAzdoPipelineScaffolder
{
    Task<string> GenerateAsync(
        string repoRoot,
        string stackId,
        SbConfig cfg,
        AzdoPipelineInitOptions opts,
        CancellationToken ct = default);
}