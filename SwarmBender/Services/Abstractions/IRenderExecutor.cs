using SwarmBender.Services.Models;

namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Renders a final stack.yml for a given stack and one or more environments
/// by layering template + baselines + service overrides.
/// </summary>
public interface IRenderExecutor
{
    Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct = default);
}
