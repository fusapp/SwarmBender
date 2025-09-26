using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Abstractions;

/// <summary>Single step in the render pipeline. Mutates the context.</summary>
public interface IRenderStage
{
    int Order { get; }
    Task ExecuteAsync(RenderContext ctx, CancellationToken ct);
}