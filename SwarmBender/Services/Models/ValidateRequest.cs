namespace SwarmBender.Services.Models;

public sealed record ValidateRequest(
    string RootPath,
    string? StackId,
    IEnumerable<string> Environments,
    bool Quiet,
    string? OutFile,
    // NEW:
    string AppSettingsMode = "env",                 // "env" or "config"
    string AppSettingsTarget = "/app/appsettings.json" // used when AppSettingsMode == "config"
);