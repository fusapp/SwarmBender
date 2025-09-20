using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>
/// Infisical plaintext batch uploader (v4 /api/v4/secrets/batch).
/// - Reads ops/vars/providers/infisical.yml
/// - Filters & transforms flattened keys
/// - Uses bearer token from env (tokenEnvVar) or optional inline token
/// </summary>
public sealed class InfisicalClient : IInfisicalClient
{
    private readonly IYamlLoader _yaml;

    public InfisicalClient(IYamlLoader yaml) => _yaml = yaml;

    public async Task<InfisicalUploadResult> UploadAsync(InfisicalUploadRequest request, CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(request.RootPath, ct)
                  ?? throw new InvalidOperationException("Infisical config not found at ops/vars/providers/infisical.yml");

        var token = ResolveToken(cfg);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Infisical token not found. Set env var '{cfg.TokenEnvVar ?? "INFISICAL_TOKEN"}'.");

        var envSlug = MapEnv(cfg, request.Env);
        var path = string.IsNullOrWhiteSpace(cfg.PathTemplate) ? "/" : cfg.PathTemplate!;
        path = path.Replace("{env}", envSlug, StringComparison.Ordinal)
                   .Replace("{scope}", request.Scope ?? "global", StringComparison.Ordinal);

        // Choose include set: CLI override > config include
        var includes = (request.IncludeOverride?.Count > 0)
            ? request.IncludeOverride
            : cfg.Include;

        // Build upload list: include + map/transform
        var toUpload = new List<(string FlatKey, string InfKey, string Value)>();
        foreach (var (flat, value) in request.Items)
        {
            if (!Included(includes, flat)) continue;

            string infKey;
            if (cfg.Map.TryGetValue(flat, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            {
                infKey = mapped!;
            }
            else
            {
                infKey = TransformKey(cfg, flat, request.Scope);
            }

            toUpload.Add((flat, infKey, value));
        }

        // Prepare result rows
        var rows = new List<InfisicalUploadItem>();
        if (toUpload.Count == 0)
        {
            return new InfisicalUploadResult(request.DryRun, rows);
        }

        if (request.DryRun)
        {
            foreach (var it in toUpload)
                rows.Add(new InfisicalUploadItem(it.FlatKey, it.InfKey, "plan: upsert"));
            return new InfisicalUploadResult(true, rows);
        }

        // v4 plaintext batch endpoint
        var baseUrl = (cfg.BaseUrl?.TrimEnd('/')) ?? "https://app.infisical.com";
        var endpoint = string.IsNullOrWhiteSpace(cfg.UploadEndpoint)
            ? "api/v4/secrets/batch"
            : cfg.UploadEndpoint!.TrimStart('/');

        var url = $"{baseUrl}/{endpoint}";

        var payload = new
        {
            projectId = cfg.ProjectId ?? cfg.ProjectSlug, // prefer ID; some instances accept slug
            environment = envSlug,
            secretPath = path, // e.g. "/global"
            secrets = toUpload.Select(x => new
            {
                secretKey = x.InfKey,
                secretValue = x.Value
            }).ToArray()
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(reqMsg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Infisical upload failed ({(int)resp.StatusCode}): {body}");

        foreach (var it in toUpload)
            rows.Add(new InfisicalUploadItem(it.FlatKey, it.InfKey, "upserted"));

        return new InfisicalUploadResult(false, rows);
    }

    // ---------- config loading ----------

    private sealed class InfisicalClientConfig
    {
        public string? BaseUrl { get; init; }
        public string? UploadEndpoint { get; init; } // default: api/v4/secrets/batch
        public string? ProjectId { get; init; }
        public string? ProjectSlug { get; init; }
        public string? PathTemplate { get; init; } // tokens: {env}, {scope}
        public string? TokenEnvVar { get; init; }
        public string? Token { get; init; } // optional inline token (local dev)

        public List<string> Include { get; } = new();
        public string? StripPrefix { get; init; }
        public Dictionary<string, string> Replace { get; } = new(StringComparer.Ordinal);
        public string? KeyTemplate { get; init; } // e.g. "{key}" or "SB__{key}"
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EnvMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<InfisicalClientConfig?> LoadConfigAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", "providers", "infisical.yml");
        if (!File.Exists(path)) return null;
        var map = await _yaml.LoadYamlAsync(path, ct);

        string? S(string k) => map.TryGetValue(k, out var v) ? v?.ToString() : null;

        var cfg = new InfisicalClientConfig
        {
            BaseUrl = S("baseUrl") ?? "https://app.infisical.com",
            UploadEndpoint = S("uploadEndpoint") ?? "api/v4/secrets/batch",
            ProjectId = S("projectId"),
            ProjectSlug = S("projectSlug"),
            PathTemplate = S("path") ?? "/",
            TokenEnvVar = S("tokenEnvVar") ?? "INFISICAL_TOKEN",
            Token = S("token"),
            StripPrefix = S("stripPrefix"),
            KeyTemplate = S("keyTemplate") ?? "{key}",
        };

        if (map.TryGetValue("include", out var inc) && inc is IEnumerable<object?> incList)
            foreach (var i in incList)
                if (i?.ToString() is { } s && !string.IsNullOrWhiteSpace(s))
                    cfg.Include.Add(s);

        if (map.TryGetValue("replace", out var repl) && repl is IDictionary<string, object?> replMap)
            foreach (var kv in replMap)
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    cfg.Replace[kv.Key] = kv.Value?.ToString() ?? string.Empty;

        if (map.TryGetValue("map", out var m) && m is IDictionary<string, object?> mMap)
            foreach (var kv in mMap)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value?.ToString() is { } v && !string.IsNullOrWhiteSpace(v))
                    cfg.Map[kv.Key] = v;

        if (map.TryGetValue("envMap", out var em) && em is IDictionary<string, object?> emMap)
            foreach (var kv in emMap)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value?.ToString() is { } v && !string.IsNullOrWhiteSpace(v))
                    cfg.EnvMap[kv.Key] = v;

        return cfg;
    }

    // ---------- helpers ----------

    private static string? ResolveToken(InfisicalClientConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Token))
            return cfg.Token; // local-only convenience (do not commit)

        var envName = string.IsNullOrWhiteSpace(cfg.TokenEnvVar) ? "INFISICAL_TOKEN" : cfg.TokenEnvVar!;
        var token = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static bool Included(IReadOnlyList<string> include, string key)
    {
        if (include.Count == 0) return true;
        foreach (var pat in include)
            if (Glob(pat, key)) return true;
        return false;
    }

    private static bool Glob(string pattern, string text)
    {
        // simple case-insensitive * matcher
        var pi = 0; var ti = 0;
        int star = -1, match = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti])))
            { pi++; ti++; continue; }

            if (pi < pattern.Length && pattern[pi] == '*')
            { star = pi++; match = ti; continue; }

            if (star != -1)
            { pi = star + 1; ti = ++match; continue; }

            return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    private static string TransformKey(InfisicalClientConfig cfg, string flatKey, string? scope)
    {
        var k = flatKey;

        if (!string.IsNullOrEmpty(cfg.StripPrefix) &&
            k.StartsWith(cfg.StripPrefix, StringComparison.Ordinal))
            k = k.Substring(cfg.StripPrefix.Length);

        foreach (var kv in cfg.Replace)
            k = k.Replace(kv.Key, kv.Value, StringComparison.Ordinal);

        var template = string.IsNullOrWhiteSpace(cfg.KeyTemplate) ? "{key}" : cfg.KeyTemplate!;
        k = template.Replace("{key}", k, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(scope))
            k = k.Replace("{scope}", scope, StringComparison.Ordinal);

        return k;
    }

    private static string MapEnv(InfisicalClientConfig cfg, string env)
        => (cfg.EnvMap.TryGetValue(env, out var mapped) && !string.IsNullOrWhiteSpace(mapped)) ? mapped! : env;
}