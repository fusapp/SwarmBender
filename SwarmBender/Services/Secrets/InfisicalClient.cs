using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>
/// Infisical plaintext batch uploader (v4 /api/v4/secrets/batch).
/// - Reads ops/vars/providers/infisical.yml
/// - Filters & transforms flattened keys
/// - Uses bearer token from env (tokenEnvVar) or optional inline token
/// - Long-term reversible mapping supported via `replace` (e.g., "__" -> "::").
/// </summary>
public sealed class InfisicalClient : IInfisicalClient
{
    private readonly IYamlLoader _yaml;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public InfisicalClient(IYamlLoader yaml) => _yaml = yaml;

    public async Task<InfisicalUploadResult> UploadAsync(InfisicalUploadRequest request, CancellationToken ct = default)
    {
        // 1) Load config
        var cfg = await LoadConfigAsync(request.RootPath, ct)
                  ?? throw new InvalidOperationException("Infisical config not found at ops/vars/providers/infisical.yml");

        // 2) Resolve token
        var token = ResolveToken(cfg);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Infisical token not found. Set env var '{cfg.TokenEnvVar ?? "INFISICAL_TOKEN"}'.");

        // 3) Resolve env + secretPath
        var wsId = cfg.WorkspaceId ?? cfg.ProjectId ?? cfg.ProjectSlug;
        var envSlug = MapEnv(cfg, request.Env);
        var pathTemplate = string.IsNullOrWhiteSpace(cfg.PathTemplate) ? "/" : cfg.PathTemplate!;
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "global" : request.Scope!;
        var secretPath = pathTemplate
            .Replace("{env}", envSlug, StringComparison.Ordinal)
            .Replace("{scope}", scope, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(secretPath)) secretPath = "/";

        // 4) Build upload set (filter + map/transform)
        var includes = (request.IncludeOverride?.Count > 0) ? request.IncludeOverride! : cfg.Include;
        var toUpload = new List<(string FlatKey, string InfKey, string Value)>();

        // Defensive: ensure dictionary
        var items = request.Items ?? new Dictionary<string, string>();

        foreach (var kv in items)
        {
            var flatKey = kv.Key;
            var value   = kv.Value ?? string.Empty;

            if (!Included(includes, flatKey)) continue;

            string infKey;
            if (cfg.Map.TryGetValue(flatKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            {
                // Explicit override wins
                infKey = mapped!;
            }
            else
            {
                // Reversible long-term mapping, e.g. "__" -> "::"
                infKey = TransformKey(cfg, flatKey, scope);
            }

            // Skip empty keys just in case
            if (string.IsNullOrWhiteSpace(infKey))
                continue;

            toUpload.Add((flatKey, infKey, value));
        }

        var rows = new List<InfisicalUploadItem>();
        if (toUpload.Count == 0)
            return new InfisicalUploadResult(request.DryRun, rows);

        // 5) Dry-run preview
        if (request.DryRun)
        {
            foreach (var it in toUpload)
                rows.Add(new InfisicalUploadItem(it.FlatKey, it.InfKey, "plan: upsert"));
            return new InfisicalUploadResult(true, rows);
        }

        // 6) Prepare HTTP request for v4 plaintext batch
        var baseUrl  = (cfg.BaseUrl?.TrimEnd('/')) ?? "https://app.infisical.com";
        var endpoint = string.IsNullOrWhiteSpace(cfg.UploadEndpoint) ? "api/v4/secrets/batch" : cfg.UploadEndpoint!.TrimStart('/');
        var url      = $"{baseUrl}/{endpoint}";

        object MakePayload(string path) => new
        {
            projectId   = cfg.ProjectId ?? cfg.ProjectSlug ?? wsId, // uyumluluk
            workspaceId = wsId,                                     // bazı kurulumlar bunu ister
            environment = envSlug,
            secretPath  = path,                                     // "/", "/global" vs
            secrets     = toUpload.Select(x => new { secretKey = x.InfKey, secretValue = x.Value }).ToArray()
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        // First try with configured path
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(MakePayload(secretPath), JsonOpts), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // 7) Fallback: if folder not found, retry at root "/"
        if (!resp.IsSuccessStatusCode)
        {
            var needsRootRetry =
                resp.StatusCode == System.Net.HttpStatusCode.NotFound &&
                body.IndexOf("Folder with path", StringComparison.OrdinalIgnoreCase) >= 0;

            if (needsRootRetry && !string.Equals(secretPath, "/", StringComparison.Ordinal))
            {
                using var retry = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(MakePayload("/"), JsonOpts), Encoding.UTF8, "application/json")
                };
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                retry.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var resp2 = await http.SendAsync(retry, ct);
                var body2 = await resp2.Content.ReadAsStringAsync(ct);
                if (!resp2.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Infisical upload failed ({(int)resp2.StatusCode}): {body2}");
            }
            else
            {
                throw new InvalidOperationException($"Infisical upload failed ({(int)resp.StatusCode}): {body}");
            }
        }

        // 8) Success
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
        
        public string? WorkspaceId { get; init; }

        // Reversible mapping e.g. "__" -> "::"
        // Upload applies forward (key.Replace("__", "::")),
        // Download (in provider) should apply reverse ("::" -> "__").
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
            BaseUrl       = S("baseUrl") ?? "https://app.infisical.com",
            UploadEndpoint= S("uploadEndpoint") ?? "api/v4/secrets/batch",
            ProjectId     = S("projectId"),
            ProjectSlug   = S("projectSlug"),
            PathTemplate  = S("path") ?? "/",
            TokenEnvVar   = S("tokenEnvVar") ?? "INFISICAL_TOKEN",
            Token         = S("token"),
            StripPrefix   = S("stripPrefix"),
            KeyTemplate   = S("keyTemplate") ?? "{key}",
            WorkspaceId = S("workspaceId"),
        };

        if (map.TryGetValue("include", out var inc) && inc is IEnumerable<object?> incList)
        {
            foreach (var i in incList)
                if (i?.ToString() is { } s && !string.IsNullOrWhiteSpace(s))
                    cfg.Include.Add(s);
        }

        if (map.TryGetValue("replace", out var repl) && repl is IDictionary<string, object?> replMap)
        {
            // Preserve insertion order for predictability (Dictionary keeps insertion order in .NET)
            foreach (var kv in replMap)
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    cfg.Replace[kv.Key] = kv.Value?.ToString() ?? string.Empty;
        }

        if (map.TryGetValue("map", out var m) && m is IDictionary<string, object?> mMap)
        {
            foreach (var kv in mMap)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value?.ToString() is { } v && !string.IsNullOrWhiteSpace(v))
                    cfg.Map[kv.Key] = v;
        }

        if (map.TryGetValue("envMap", out var em) && em is IDictionary<string, object?> emMap)
        {
            foreach (var kv in emMap)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value?.ToString() is { } v && !string.IsNullOrWhiteSpace(v))
                    cfg.EnvMap[kv.Key] = v;
        }

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
        // Simple case-insensitive glob matcher: '*', '?'
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

        // --- sanitize: Infisical key'te ':' yasak, genel olarak [A-Za-z0-9_.-] dışını '_' yapalım
        k = Regex.Replace(k, @"[^A-Za-z0-9_.\-]", "_");

        // opsiyonel: birden fazla '_' yan yana geldiyse sadeleştir
        k = Regex.Replace(k, "_{2,}", "_").Trim('_');

        return k;
    }

    private static string MapEnv(InfisicalClientConfig cfg, string env)
        => (cfg.EnvMap.TryGetValue(env, out var mapped) && !string.IsNullOrWhiteSpace(mapped)) ? mapped! : env;
}