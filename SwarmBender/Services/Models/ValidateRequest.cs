namespace SwarmBender.Services.Models;

/// <summary>Request for validation.</summary>
public sealed record ValidateRequest(
    string RootPath,
    string? StackId,
    IEnumerable<string> Environments,
    bool Quiet,
    string? OutFile);
