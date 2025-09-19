namespace SwarmBender.Services.Models;

public sealed record SecretsPolicy
{
    public bool Enabled { get; init; } = true;
    public List<string> Paths { get; init; } = new();
    public string NameTemplate { get; init; } = "sb_{scope}_{env}_{key}_{version}";
    public string VersionMode { get; init; } = "kv-version"; // kv-version | content-sha | hmac
    public string TargetDir { get; init; } = "/run/secrets";
    public string Mode { get; init; } = "0400";
    public Dictionary<string,string> Labels { get; init; } = new();
}