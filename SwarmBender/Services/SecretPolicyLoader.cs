using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>Loads ops/policies/secrets.yml into SecretsPolicy.</summary>
public sealed class SecretPolicyLoader
{
    private readonly IYamlLoader _yaml;
    public SecretPolicyLoader(IYamlLoader yaml) => _yaml = yaml;

    public async Task<SecretsPolicy> LoadAsync(string rootPath, CancellationToken ct = default)
    {
        var path = Path.Combine(rootPath, "ops", "policies", "secrets.yml");
        var map = await _yaml.LoadYamlAsync(path, ct);
        if (map.Count == 0)
            return new SecretsPolicy(); // defaults

        if (!map.TryGetValue("secretize", out var node) || node is not IDictionary<string, object?> m)
            return new SecretsPolicy();

        var p = new SecretsPolicy();

        if (m.TryGetValue("enabled", out var v) && v is bool b)
            p = p with { Enabled = b };

        if (m.TryGetValue("paths", out var vv) && vv is IEnumerable<object?> list)
        {
            var paths = new List<string>();
            foreach (var x in list)
            {
                var s = x?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) paths.Add(s!);
            }
            p = p with { Paths = paths };
        }

        if (m.TryGetValue("name_template", out var nt))
            p = p with { NameTemplate = nt?.ToString() ?? p.NameTemplate };

        if (m.TryGetValue("version_mode", out var vm))
            p = p with { VersionMode = vm?.ToString() ?? p.VersionMode };

        if (m.TryGetValue("target_dir", out var td))
            p = p with { TargetDir = td?.ToString() ?? p.TargetDir };

        if (m.TryGetValue("mode", out var md))
            p = p with { Mode = md?.ToString() ?? p.Mode };

        if (m.TryGetValue("labels", out var lb) && lb is IDictionary<string, object?> lm)
        {
            var labels = new Dictionary<string,string>();
            foreach (var kv in lm) labels[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            p = p with { Labels = labels };
        }

        return p;
    }
}