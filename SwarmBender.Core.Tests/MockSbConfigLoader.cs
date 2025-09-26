using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Tests;

public class MockSbConfigLoader : ISbConfigLoader
{
    public async Task<SbConfig> LoadAsync(string rootPath, CancellationToken ct)
    {
        return new SbConfig();
    }
}