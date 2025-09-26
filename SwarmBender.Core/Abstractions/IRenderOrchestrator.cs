using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Abstractions;

public interface IRenderOrchestrator
{
    Task<RenderResult> RunAsync(RenderRequest request, CancellationToken ct = default);
}