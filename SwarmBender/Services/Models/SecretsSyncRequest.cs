namespace SwarmBender.Services.Models;

public sealed class SecretsSyncRequest
{
    public required string RootPath { get; init; }
    public required string Env { get; init; }
    public required string Scope { get; init; } // 'global' or tenant
    public string? StackId { get; init; }
    public IReadOnlyList<string> Services { get; init; } = new List<string>();
    public string? VersionModeOverride { get; init; }
    public bool DryRun { get; init; }
    public bool Quiet { get; init; }
}