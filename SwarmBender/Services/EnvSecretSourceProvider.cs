using System.Collections;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Reads secrets from current process environment. Only variables starting with SB__ are considered by default.</summary>
public sealed class EnvSecretSourceProvider : ISecretSourceProvider
{
    public string Type => "env";

    public Task<IDictionary<string, string>> GetAsync(string rootPath, string scope, string env, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(key)) continue;

            // Convention: SB__<KEY> holds the value for <KEY>
            if (key.StartsWith("SB__", StringComparison.Ordinal))
            {
                var logical = key.Substring(4); // drop SB__
                dict[logical] = de.Value?.ToString() ?? string.Empty;
            }
        }
        return Task.FromResult<IDictionary<string, string>>(dict);
    }
}