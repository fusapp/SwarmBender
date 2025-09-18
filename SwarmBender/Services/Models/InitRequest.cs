namespace SwarmBender.Services.Models;

/// <summary>Request for initializing scaffolds.</summary>
public sealed record InitRequest(
    string? StackId,
    IEnumerable<string> EnvNames,
    string RootPath,
    bool NoGlobalDefs,
    bool NoDefs,
    bool NoAliases,
    bool DryRun,
    bool Quiet);
