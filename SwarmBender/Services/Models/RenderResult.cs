namespace SwarmBender.Services.Models;

/// <summary>Per-environment render outcome.</summary>
public sealed record RenderOutput(string Environment, string OutputPath);

/// <summary>Render executor result.</summary>
public sealed record RenderResult(IReadOnlyList<RenderOutput> Outputs);
