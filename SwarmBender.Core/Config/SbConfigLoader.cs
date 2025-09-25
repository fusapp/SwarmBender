using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Config;

public sealed class SbConfigLoader : ISbConfigLoader
{
    private readonly IYamlEngine _yaml;
    public SbConfigLoader(IYamlEngine yaml) => _yaml = yaml;

    public async Task<SbConfig> LoadAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "sb.yml");
        var cfg = File.Exists(path)
            ? (await _yaml.LoadYamlAsync<SbConfig>(path, ct)) ?? new SbConfig()
            : new SbConfig();
        return cfg;
    }
}