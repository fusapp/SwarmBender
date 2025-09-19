namespace SwarmBender.Services.Models;


public sealed record SecretsRotateRequest(
    string RootPath,
    string Env,
    string Scope,                 // "global" | "stack" | "service"
    string? StackId,              // required when Scope = "stack" or "service"
    string? ServiceName,          // required when Scope = "service"
    IReadOnlyList<string> Keys,   // explicit keys to rotate (can be empty if Match is used)
    string? Match,                // optional glob (e.g. "ConnectionStrings.*") or "/regex/"
    string? ValuesJsonPath,       // JSON file: { "KEY":"value", ... }
    bool ReadValuesFromStdin,     // read JSON from STDIN if true
    string VersionMode,           // "content-sha" | "timestamp" | "serial"
    int Keep,                     // how many old versions to keep on engine (>=0)
    bool DryRun,
    bool Quiet
);