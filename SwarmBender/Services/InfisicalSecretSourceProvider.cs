using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>
/// Secret values provider backed by Infisical.
/// - Config:  ops/vars/providers/infisical.yml
/// - Inputs:  ops/vars/secrets-map.{env}.yml  (flattened keys as candidates)
/// - Output:  Dictionary{flattenKey -> value} (only keys found in Infisical)
///
/// Auth: Service Token (recommended). Token kaynağı:
///   - env var (default: INFISICAL_TOKEN), veya
///   - ops/vars/providers/infisical.yml içindeki tokenEnvVar ile adını belirtebilirsin.
///
/// Not: Endpoint varsayılanı Infisical v3 raw secrets içindir:
///   POST {baseUrl}/api/v3/secrets/raw
///   Body: { environment, workspaceId (projectId), secretPath, expandSecretReferences }
/// Dönen yanıtta "secrets": [ { "key": "...", "value": "..." }, ... ] beklenir.
/// </summary>
public sealed class InfisicalSecretSourceProvider : ISecretSourceProvider
{
    public string Type => "infisical";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly IYamlLoader _yaml;

    public InfisicalSecretSourceProvider(IYamlLoader yaml) => _yaml = yaml;

    public async Task<IDictionary<string, string>> GetAsync(
        string rootPath,
        string scope,   // e.g., "global"
        string env,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(rootPath, ct);
        if (cfg is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var token = ResolveToken(cfg);
        if (string.IsNullOrWhiteSpace(token)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var infEnv = cfg.EnvMap.TryGetValue(env, out var mappedEnv) && !string.IsNullOrWhiteSpace(mappedEnv)
            ? mappedEnv!
            : env;

        var pathRendered = (cfg.PathTemplate ?? "/")
            .Replace("{env}", infEnv, StringComparison.Ordinal)
            .Replace("{scope}", scope ?? string.Empty, StringComparison.Ordinal);

        // 1) Aday flattened key’ler (secrets-map.<env>.yml anahtarları)
        var candidates = await LoadCandidateKeysAsync(rootPath, env, ct);
        if (candidates.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 2) Infisical’dan path altındaki tüm secret’ları çek
        var baseUrl = (cfg.BaseUrl?.TrimEnd('/')) ?? "https://app.infisical.com";
        var endpoint = (cfg.Endpoint?.TrimStart('/')) ?? "api/v3/secrets/raw";

        var requestUri = $"{baseUrl}/{endpoint}";
        var reqObj = new
        {
            environment = infEnv,
            workspaceId = cfg.ProjectId,
            secretPath = pathRendered,
            expandSecretReferences = true
        };

        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(reqObj), Encoding.UTF8, "application/json")
        };
        reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await Http.SendAsync(reqMsg, ct);
        if (!resp.IsSuccessStatusCode)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // Esnek parse: "secrets": [ {key,value}... ]
        var infDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("secrets", out var secretsNode) &&
            secretsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in secretsNode.EnumerateArray())
            {
                var key = el.TryGetProperty("key", out var k) ? k.GetString() : null;
                var val = el.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key) && val is not null)
                    infDict[key!] = val!;
            }
        }

        // 3) Aday flattened key’leri include filtresinden geçir, ad çöz ve değer eşle
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flatKey in candidates)
        {
            if (!IsIncluded(cfg, flatKey)) continue;

            var infKey = ResolveInfisicalKey(cfg, flatKey);
            if (infDict.TryGetValue(infKey, out var value))
                result[flatKey] = value;
        }

        return result;
    }

    // ---------- helpers ----------

    private static bool IsIncluded(AkvLikeIncludeMap cfg, string flatKey)
    {
        if (cfg.Include.Count == 0) return true;
        foreach (var pat in cfg.Include)
            if (WildcardMatch(flatKey, pat)) return true;
        return false;
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return text == pattern;
        var parts = pattern.Split('*');
        if (parts.Length == 1) return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);

        var idx = 0; var first = true;
        foreach (var part in parts)
        {
            if (part.Length == 0) { first = false; continue; }
            var found = text.IndexOf(part, idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            if (first && !pattern.StartsWith("*") && found != 0) return false;
            idx = found + part.Length; first = false;
        }
        return pattern.EndsWith("*") || idx == text.Length;
    }

    private static string ResolveInfisicalKey(InfisicalConfig cfg, string flatKey)
    {
        // explicit map wins
        if (cfg.Map.TryGetValue(flatKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped!;

        var k = flatKey;

        if (!string.IsNullOrWhiteSpace(cfg.StripPrefix) && k.StartsWith(cfg.StripPrefix, StringComparison.Ordinal))
            k = k.Substring(cfg.StripPrefix.Length);

        foreach (var kv in cfg.Replace)
            k = k.Replace(kv.Key, kv.Value, StringComparison.Ordinal);

        // template: {key}
        var template = string.IsNullOrWhiteSpace(cfg.KeyTemplate) ? "{key}" : cfg.KeyTemplate!;
        return template.Replace("{key}", k, StringComparison.Ordinal);
    }

    private async Task<List<string>> LoadCandidateKeysAsync(string rootPath, string env, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", $"secrets-map.{env}.yml");
        if (!File.Exists(path)) return new List<string>();
        var yaml = await _yaml.LoadYamlAsync(path, ct);
        return yaml.Keys.ToList(); // flatten keys
    }

    private static string? ResolveToken(InfisicalConfig cfg)
    {
        var envName = string.IsNullOrWhiteSpace(cfg.TokenEnvVar) ? "INFISICAL_TOKEN" : cfg.TokenEnvVar!;
        var token = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private async Task<InfisicalConfig?> LoadConfigAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", "providers", "infisical.yml");
        if (!File.Exists(path)) return null;

        var map = await _yaml.LoadYamlAsync(path, ct);

        var cfg = new InfisicalConfig
        {
            BaseUrl = GetString(map, "baseUrl") ?? "https://app.infisical.com",
            Endpoint = GetString(map, "endpoint") ?? "api/v3/secrets/raw",
            ProjectId = GetString(map, "projectId"), // workspaceId
            PathTemplate = GetString(map, "path") ?? "/",
            TokenEnvVar = GetString(map, "tokenEnvVar") ?? "INFISICAL_TOKEN",
            StripPrefix = GetString(map, "stripPrefix"),
            KeyTemplate = GetString(map, "keyTemplate") ?? "{key}"
        };

        // include: [ "ConnectionStrings__*", "Redis__*" ]
        if (map.TryGetValue("include", out var includeNode) && includeNode is IEnumerable<object?> incList)
        {
            foreach (var item in incList)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    cfg.Include.Add(s!);
            }
        }

        // replace: { "__": "_", ":": "_" }
        if (map.TryGetValue("replace", out var replNode) && replNode is IDictionary<string, object?> repl)
        {
            foreach (var kv in repl)
            {
                var from = kv.Key;
                var to = kv.Value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(from))
                    cfg.Replace[from] = to;
            }
        }

        // map: { "ConnectionStrings__MSSQL_Master": "MSSQL_Master" }
        if (map.TryGetValue("map", out var mNode) && mNode is IDictionary<string, object?> m)
        {
            foreach (var kv in m)
            {
                var k = kv.Key;
                var v = kv.Value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                    cfg.Map[k] = v;
            }
        }

        // envMap:
        //   prod: prod
        //   dev: development
        if (map.TryGetValue("envMap", out var envNode) && envNode is IDictionary<string, object?> envMap)
        {
            foreach (var kv in envMap)
            {
                var k = kv.Key;
                var v = kv.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                    cfg.EnvMap[k] = v!;
            }
        }

        return cfg;
    }

    private static string? GetString(IDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var obj) ? obj?.ToString() : null;

    // ---- config types

    private abstract class AkvLikeIncludeMap
    {
        public List<string> Include { get; } = new();
        public Dictionary<string, string> Replace { get; } = new(StringComparer.Ordinal);
    }

    private sealed class InfisicalConfig : AkvLikeIncludeMap
    {
        public string? BaseUrl { get; init; }
        public string? Endpoint { get; init; }
        public string? ProjectId { get; init; }          // workspaceId
        public string? PathTemplate { get; init; }       // default "/"
        public string? TokenEnvVar { get; init; }        // default "INFISICAL_TOKEN"

        // key transform
        public string? StripPrefix { get; init; }        // e.g., "ConnectionStrings__"
        public string? KeyTemplate { get; init; }        // e.g., "{key}"
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

        // env map (Swarm env -> Infisical environment)
        public Dictionary<string, string> EnvMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}