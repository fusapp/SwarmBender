namespace SwarmBender.Services.Models;

/// <summary>Render request parameters.</summary>
public sealed record RenderRequest(
    string RootPath,
    string StackId,
    IEnumerable<string> Environments,
    string OutDir,         // default: ops/state/last
    bool WriteHistory,     // default: true
    bool Preview,          // print to stdout
    bool DryRun,
    bool Quiet);
