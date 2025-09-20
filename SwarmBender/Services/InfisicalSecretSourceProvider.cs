// File: SwarmBender/Services/InfisicalSecretSourceProvider.cs
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

public sealed class InfisicalSecretSourceProvider : ISecretSourceProvider
{
    public string Type => "infisical";

    private readonly IYamlLoader _yaml;
    public InfisicalSecretSourceProvider(IYamlLoader yaml) => _yaml = yaml;

    public async Task<IDictionary<string, string>> GetAsync(string rootPath, string scope, string env, CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(rootPath, ct)
                  ?? throw new InvalidOperationException("Infisical config not found at ops/vars/providers/infisical.yml");

        var token = ResolveToken(cfg);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Infisical token not found. Set env var '{cfg.TokenEnvVar ?? "INFISICAL_TOKEN"}'.");

        var envSlug   = MapEnv(cfg, env);
        var baseUrl   = (cfg.BaseUrl?.TrimEnd('/')) ?? "https://app.infisical.com";
        var endpoint  = string.IsNullOrWhiteSpace(cfg.DownloadEndpoint) ? "api/v3/secrets/raw" : cfg.DownloadEndpoint!.TrimStart('/');
        var url       = $"{baseUrl}/{endpoint}";

        // hedef path (örn: "/sso"), yoksa "/"’a fallback
        var pathTpl   = string.IsNullOrWhiteSpace(cfg.PathTemplate) ? "/" : cfg.PathTemplate!;
        var secretPath = pathTpl.Replace("{env}", envSlug, StringComparison.Ordinal)
                                .Replace("{scope}", string.IsNullOrWhiteSpace(scope) ? "global" : scope, StringComparison.Ordinal);

        // 1. deneme: belirtilen path
        var list = await FetchAsync(url, token, cfg, envSlug, secretPath, ct);

        // path bulunamadıysa fallback "/"
        if (list is null)
            list = await FetchAsync(url, token, cfg, envSlug, "/", ct) ?? new List<(string Key, string Value)>();

        // Flatten forma geri çevir (ConnectionStrings_Mongo_SSO -> ConnectionStrings__Mongo_SSO)
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Doğrudan map’in tersi (yüksek öncelik)
        var reverseMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfg.Map) // Map: FlatKey -> InfKey (upload sırasında)
            if (!string.IsNullOrWhiteSpace(kv.Value))
                reverseMap[kv.Value] = kv.Key;

        foreach (var (infKey, val) in list)
        {
            string flatKey;

            if (reverseMap.TryGetValue(infKey, out var mappedBack))
            {
                flatKey = mappedBack; // explicit eşleme kazanır
            }
            else
            {
                // Template tersine çevirme (KeyTemplate = "{key}" olduğu sürece no-op)
                var k = infKey;

                // replace’i tersine uygula: upload’da "__" -> "_" yaptıysak, download’da "_" -> "__"
                foreach (var kv in cfg.Replace)
                {
                    var from = kv.Key ?? string.Empty;
                    var to   = kv.Value ?? string.Empty;
                    if (to.Length > 0)
                        k = k.Replace(to, from, StringComparison.Ordinal);
                }

                // stripPrefix’in tam tersi normalde bilinmez (tek yönlüdür); bizde {key} olduğu için dokunmuyoruz.
                flatKey = k;
            }

            result[flatKey] = val ?? string.Empty;
        }

        return result;
    }

    // --- HTTP GET (v3 raw). 404 -> null, başarı -> secrets listesi.
    private static async Task<List<(string Key, string Value)>?> FetchAsync(
        string url, string token, InfCfg cfg, string envSlug, string secretPath, CancellationToken ct)
    {
        var ws = cfg.WorkspaceId ?? cfg.ProjectId ?? cfg.ProjectSlug;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var query = new Dictionary<string, string?>
        {
            ["workspaceId"] = ws,
            ["projectId"]   = cfg.ProjectId ?? cfg.ProjectSlug ?? ws,
            ["environment"] = envSlug,
            ["secretPath"]  = secretPath,
            ["expandSecretReferences"] = "true",
            ["include_imports"]        = "true"
        };

        var reqUrl = AppendQuery(url, query);
        using var req = new HttpRequestMessage(HttpMethod.Get, reqUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // fallback'e izin ver

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Infisical fetch failed ({(int)resp.StatusCode}): {body}");

        // response şekilleri farklı olabilir:
        // { "secrets":[ {"secretKey":"K","secretValue":"V"}, ... ] }
        // veya { "secrets":[ {"key":"K","value":"V"}, ... ] }
        var list = new List<(string Key, string Value)>();

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("secrets", out var secrets) && secrets.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in secrets.EnumerateArray())
            {
                string? k = null, v = null;
                if (el.TryGetProperty("secretKey", out var sk) && sk.ValueKind == JsonValueKind.String) k = sk.GetString();
                if (el.TryGetProperty("secretValue", out var sv) && sv.ValueKind == JsonValueKind.String) v = sv.GetString();

                if (k is null && el.TryGetProperty("key", out var k2) && k2.ValueKind == JsonValueKind.String) k = k2.GetString();
                if (v is null && el.TryGetProperty("value", out var v2) && v2.ValueKind == JsonValueKind.String) v = v2.GetString();

                if (!string.IsNullOrWhiteSpace(k))
                    list.Add((k!, v ?? string.Empty));
            }
        }

        return list;
    }

    private static string AppendQuery(string baseUrl, IDictionary<string, string?> q)
    {
        var first = true;
        var sb = new System.Text.StringBuilder(baseUrl);
        foreach (var kv in q)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value!));
        }
        return sb.ToString();
    }

    // ---- config & helpers ----
    private sealed class InfCfg
    {
        public string? BaseUrl { get; init; }
        public string? DownloadEndpoint { get; init; }
        public string? ProjectId { get; init; }
        public string? ProjectSlug { get; init; }
        public string? WorkspaceId { get; init; }
        public string? PathTemplate { get; init; }
        public string? TokenEnvVar { get; init; }
        public string? Token { get; init; }

        public string? StripPrefix { get; init; }
        public Dictionary<string, string> Replace { get; } = new(StringComparer.Ordinal);
        public string? KeyTemplate { get; init; }
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EnvMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<InfCfg?> LoadConfigAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", "providers", "infisical.yml");
        if (!File.Exists(path)) return null;
        var map = await _yaml.LoadYamlAsync(path, ct);

        string? S(string k) => map.TryGetValue(k, out var v) ? v?.ToString() : null;

        var cfg = new InfCfg
        {
            BaseUrl         = S("baseUrl") ?? "https://app.infisical.com",
            DownloadEndpoint= S("downloadEndpoint") ?? S("endpoint") ?? "api/v3/secrets/raw",
            ProjectId       = S("projectId"),
            ProjectSlug     = S("projectSlug"),
            WorkspaceId     = S("workspaceId"),
            PathTemplate    = S("path") ?? "/",
            TokenEnvVar     = S("tokenEnvVar") ?? "INFISICAL_TOKEN",
            Token           = S("token"),
            StripPrefix     = S("stripPrefix"),
            KeyTemplate     = S("keyTemplate") ?? "{key}",
        };

        if (map.TryGetValue("replace", out var repl) && repl is IDictionary<string, object?> replMap)
            foreach (var kv in replMap)
                cfg.Replace[kv.Key] = kv.Value?.ToString() ?? string.Empty;

        if (map.TryGetValue("map", out var m) && m is IDictionary<string, object?> mMap)
            foreach (var kv in mMap)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value?.ToString() is { } v && !string.IsNullOrWhiteSpace(v))
                    cfg.Map[kv.Key] = v;

        if (map.TryGetValue("envMap", out var em) && em is IDictionary<string, object?> emMap)
            foreach (var kv in emMap)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value?.ToString() is { } v2 && !string.IsNullOrWhiteSpace(v2))
                    cfg.EnvMap[kv.Key] = v2;

        return cfg;
    }

    private static string? ResolveToken(InfCfg cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Token)) return cfg.Token;
        var envName = string.IsNullOrWhiteSpace(cfg.TokenEnvVar) ? "INFISICAL_TOKEN" : cfg.TokenEnvVar!;
        var token = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string MapEnv(InfCfg cfg, string env)
        => (cfg.EnvMap.TryGetValue(env, out var mapped) && !string.IsNullOrWhiteSpace(mapped)) ? mapped! : env;
}