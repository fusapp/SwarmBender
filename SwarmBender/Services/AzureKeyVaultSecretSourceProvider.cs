// File: SwarmBender/Services/Secrets/Sources/AzureKeyVaultSecretSourceProvider.cs

using System.Text;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>
/// Secret values provider backed by Azure Key Vault (AKV).
/// - Config:  ops/vars/providers/azure-key-vault.yml
/// - Inputs:  ops/vars/secrets-map.{env}.yml  (flattened keys as candidates)
/// - Output:  Dictionary{flattenKey -> value} (only keys found in AKV)
/// Credentials: DefaultAzureCredential (Managed Identity, az login, env vars)
/// </summary>
public sealed class AzureKeyVaultSecretSourceProvider : ISecretSourceProvider
{
    public string Type => "azure-key-vault";

    private readonly IYamlLoader _yaml;

    public AzureKeyVaultSecretSourceProvider(IYamlLoader yaml) => _yaml = yaml;

    public async Task<IDictionary<string, string>> GetAsync(
        string rootPath,
        string scope,   // e.g., "global"
        string env,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(rootPath, ct);
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.VaultUri))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var candidates = await LoadCandidateKeysAsync(rootPath, env, ct);
        if (candidates.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var client = CreateClient(cfg);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flatKey in candidates)
        {
            if (!IsIncluded(cfg, flatKey))
                continue;

            var secretName = ResolveSecretName(cfg, env, scope, flatKey);

            try
            {
                var secret = await client.GetSecretAsync(secretName, cancellationToken: ct);
                result[flatKey] = secret.Value.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // not found in AKV – skip
            }
        }

        return result;
    }

    // ---------- helpers ----------

    private static SecretClient CreateClient(AkvConfig cfg)
    {
        var cred = new DefaultAzureCredential();
        return new SecretClient(new Uri(cfg.VaultUri!), cred);
    }

    private static bool IsIncluded(AkvConfig cfg, string flatKey)
    {
        if (cfg.Include.Count == 0) return true;
        foreach (var pat in cfg.Include)
        {
            if (WildcardMatch(flatKey, pat)) return true;
        }
        return false;
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        // simple '*' wildcard support
        if (string.IsNullOrEmpty(pattern)) return text == pattern;
        var parts = pattern.Split('*', StringSplitOptions.None);
        if (parts.Length == 1) return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);

        var index = 0;
        var first = true;
        foreach (var part in parts)
        {
            if (part.Length == 0) { first = false; continue; }
            var found = text.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            if (first && !pattern.StartsWith("*") && found != 0) return false;
            index = found + part.Length;
            first = false;
        }
        return pattern.EndsWith("*") || index == text.Length;
    }

    private static string ResolveSecretName(AkvConfig cfg, string env, string scope, string flatKey)
    {
        // explicit map wins
        if (cfg.Map.TryGetValue(flatKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return ApplyPrefix(cfg, NormalizeForAkv(mapped));

        // apply replace rules on flattened key (e.g., "__" -> "--")
        var keyName = flatKey;
        foreach (var kv in cfg.Replace)
            keyName = keyName.Replace(kv.Key, kv.Value, StringComparison.Ordinal);

        // template tokens: {env}, {scope}, {key}
        var template = string.IsNullOrWhiteSpace(cfg.NameTemplate) ? "{key}" : cfg.NameTemplate!;
        var rendered = template
            .Replace("{env}", env ?? string.Empty, StringComparison.Ordinal)
            .Replace("{scope}", scope ?? string.Empty, StringComparison.Ordinal)
            .Replace("{key}", keyName, StringComparison.Ordinal);

        return ApplyPrefix(cfg, NormalizeForAkv(rendered));
    }

    private static string ApplyPrefix(AkvConfig cfg, string name)
        => string.IsNullOrWhiteSpace(cfg.Prefix) ? name : $"{cfg.Prefix}{name}";

    private static string NormalizeForAkv(string s)
    {
        // Azure KV: letters, digits, '-', max 127
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
                sb.Append(ch);
            else
                sb.Append('-');
        }
        var result = sb.ToString().Trim('-');
        return result.Length > 127 ? result[..127] : result;
    }

    private async Task<AkvConfig?> LoadConfigAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", "providers", "azure-key-vault.yml");
        if (!File.Exists(path)) return null;

        var map = await _yaml.LoadYamlAsync(path, ct);

        var cfg = new AkvConfig
        {
            VaultUri = GetString(map, "vaultUri"),
            Prefix = GetString(map, "prefix"),
            NameTemplate = GetString(map, "nameTemplate")
        };

        if (map.TryGetValue("include", out var includeNode) && includeNode is IEnumerable<object?> incList)
        {
            foreach (var item in incList)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    cfg.Include.Add(s!);
            }
        }

        if (map.TryGetValue("replace", out var replNode) && replNode is IDictionary<string, object?> replDict)
        {
            foreach (var kv in replDict)
            {
                var from = kv.Key;
                var to = kv.Value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(from))
                    cfg.Replace[from] = to;
            }
        }

        if (map.TryGetValue("map", out var mNode) && mNode is IDictionary<string, object?> mDict)
        {
            foreach (var kv in mDict)
            {
                var k = kv.Key;
                var v = kv.Value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                    cfg.Map[k] = v;
            }
        }

        return cfg;
    }

    private static string? GetString(IDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var obj) ? obj?.ToString() : null;

    private async Task<List<string>> LoadCandidateKeysAsync(string rootPath, string env, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", $"secrets-map.{env}.yml");
        if (!File.Exists(path)) return new List<string>();

        var yaml = await _yaml.LoadYamlAsync(path, ct);
        // keys of map are flattened keys
        return yaml.Keys.ToList();
    }

    private sealed class AkvConfig
    {
        public string? VaultUri { get; init; }
        public string? Prefix { get; init; }
        public string? NameTemplate { get; init; }

        public List<string> Include { get; } = new();
        public Dictionary<string, string> Replace { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}