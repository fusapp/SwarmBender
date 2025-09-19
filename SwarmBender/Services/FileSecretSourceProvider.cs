using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Reads file-backed secrets from secrets/files/{scope}/{env}/* as KeyPerFile.</summary>
public sealed class FileSecretSourceProvider : ISecretSourceProvider
{
    public string Type => "file";

    public Task<IDictionary<string, string>> GetAsync(string rootPath, string scope, string env, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var baseDir = Path.Combine(rootPath, "secrets", "files", scope, env);
        if (!Directory.Exists(baseDir)) return Task.FromResult<IDictionary<string,string>>(result);

        foreach (var file in Directory.EnumerateFiles(baseDir, "*.secret", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var content = File.ReadAllText(file);
            result[name] = content;
        }
        return Task.FromResult<IDictionary<string, string>>(result);
    }
}