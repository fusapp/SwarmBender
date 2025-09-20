namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Interactive wizard that creates/updates ops/providers/infisical.yml.
/// </summary>
public interface IInfisicalConfigWizard
{
    /// <summary>
    /// Runs an interactive wizard and writes the config file.
    /// Returns the absolute path of the written config.
    /// </summary>
    Task<string> RunAsync(string rootPath, string? configPath = null, bool forceOverwrite = false, CancellationToken ct = default);
}