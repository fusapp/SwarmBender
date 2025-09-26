using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Config;


/// <summary>
/// Loads ops/sb.yml into SbConfig using IYamlEngine.
/// </summary>
public interface ISbConfigLoader
{
    Task<SbConfig> LoadAsync(string rootPath, CancellationToken ct);
}