using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Pipeline;

namespace SwarmBender.Core.Abstractions;

public interface IRenderOrchestrator
{
    Task<RenderResult> RunAsync(RenderRequest request,PipelineMode mode, CancellationToken ct = default);
}