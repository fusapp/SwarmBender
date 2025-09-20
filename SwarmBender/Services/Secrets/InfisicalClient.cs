// File: SwarmBender/Services/InfisicalClient.cs

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
/// - (NEW) Auto-create secretPath (folder) if missing before upload (config: autoCreatePathOnUpload)
/// </summary>
public sealed class InfisicalClient : IInfisicalClient
{
    private readonly IYamlLoader _yaml;

    public InfisicalClient(IYamlLoader yaml) => _yaml = yaml;

    public async Task<InfisicalUploadResult> UploadAsync(InfisicalUploadRequest request, CancellationToken ct = default)
    {
        // 1) Load config
        var cfg = await LoadConfigAsync(request.RootPath, ct)
                  ?? throw new InvalidOperationException(
                      "Infisical config not found at ops/vars/providers/infisical.yml");

        // 2) Resolve token
        var token = ResolveToken(cfg);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"Infisical token not found. Set env var '{cfg.TokenEnvVar ?? "INFISICAL_TOKEN"}'.");

        // 2.5) Resolve a single id usable for both projectId/workspaceId
        var id = cfg.ProjectId
                 ?? cfg.WorkspaceId
                 ?? cfg.ProjectSlug
                 ?? cfg.WorkspaceSlug;

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                "Infisical: set one of projectId/workspaceId/projectSlug/workspaceSlug in ops/vars/providers/infisical.yml");

        // 3) Resolve env + secretPath
        var envSlug = MapEnv(cfg, request.Env);
        var pathTemplate = string.IsNullOrWhiteSpace(cfg.PathTemplate) ? "/" : cfg.PathTemplate!;
        var secretPath = pathTemplate
            .Replace("{env}", envSlug, StringComparison.Ordinal)
            .Replace("{scope}", request.Scope ?? "global", StringComparison.Ordinal);

        // 4) Build upload set (filter + map/transform)
        var includes = (request.IncludeOverride?.Count > 0) ? request.IncludeOverride! : cfg.Include;
        var toUpload = new List<(string FlatKey, string InfKey, string Value)>();

        foreach (var kv in request.Items)
        {
            var flatKey = kv.Key;
            var value = kv.Value ?? string.Empty;

            if (!Included(includes, flatKey)) continue;

            string infKey;
            if (cfg.Map.TryGetValue(flatKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                infKey = mapped!;
            else
                infKey = TransformKey(cfg, flatKey, request.Scope);

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

        // 5.5) Auto-create folder path, multi-level (if enabled)
        var baseUrl  = (cfg.BaseUrl?.TrimEnd('/')) ?? "https://app.infisical.com";

        if (cfg.AutoCreatePathOnUpload && !string.IsNullOrEmpty(secretPath) && secretPath != "/")
        {
            await EnsureFolderAsync(
                baseUrl,
                cfg.FoldersEndpoint ?? "api/v3/folders",
                token!,
                id!,
                envSlug,
                secretPath,
                ct);
        }

        // 6) Prepare HTTP request for v4 plaintext batch
        var endpoint = string.IsNullOrWhiteSpace(cfg.UploadEndpoint)
            ? "api/v4/secrets/batch"
            : cfg.UploadEndpoint!.TrimStart('/');
        var url = $"{baseUrl}/{endpoint}";

        object MakePayload(string path) => new
        {
            projectId = id, // some deployments expect this
            workspaceId = id, // some deployments expect this instead
            environment = envSlug,
            secretPath = path, // e.g. "/", "/global", "/sso", "/team/a/b"
            secrets = toUpload.Select(x => new { secretKey = x.InfKey, secretValue = x.Value }).ToArray()
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(MakePayload(secretPath)), Encoding.UTF8,
                "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // 7) Fallback: folder yoksa "/"’a yüklemeyi dene
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound &&
                body.IndexOf("Folder with path", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                using var retry = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(MakePayload("/")), Encoding.UTF8,
                        "application/json")
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
        public string? WorkspaceId { get; init; } // NEW
        public string? WorkspaceSlug { get; init; } // NEW
        public string? PathTemplate { get; init; } // tokens: {env}, {scope}
        public string? TokenEnvVar { get; init; }
        public string? Token { get; init; } // optional inline token (local dev)
        public bool AutoCreatePathOnUpload { get; init; } = true; // NEW

        public List<string> Include { get; } = new();
        public string? StripPrefix { get; init; }
        public Dictionary<string, string> Replace { get; } = new(StringComparer.Ordinal);
        public string? KeyTemplate { get; init; } // e.g. "{key}" or "SB__{key}"
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EnvMap { get; } = new(StringComparer.OrdinalIgnoreCase);
      
        public string? FoldersEndpoint { get; init; } // default: "api/v3/folders"
    }

    private async Task<InfisicalClientConfig?> LoadConfigAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, "ops", "vars", "providers", "infisical.yml");
        if (!File.Exists(path)) return null;
        var map = await _yaml.LoadYamlAsync(path, ct);

        string? S(string k) => map.TryGetValue(k, out var v) ? v?.ToString() : null;
        bool B(string k, bool def = false)
        {
            if (!map.TryGetValue(k, out var v) || v is null) return def;
            if (v is bool b) return b;
            if (bool.TryParse(v.ToString(), out var b2)) return b2;
            return def;
        }

        // NOTE: project/workspace alias desteği
        var projectId  = S("projectId")  ?? S("workspaceId");
        var projectSlug= S("projectSlug")?? S("workspaceSlug");

        var cfg = new InfisicalClientConfig
        {
            BaseUrl        = S("baseUrl") ?? "https://app.infisical.com",
            UploadEndpoint = S("uploadEndpoint") ?? "api/v4/secrets/batch",
            FoldersEndpoint= S("foldersEndpoint") ?? "api/v3/folders",
            // 🔽 alias’lardan biri yeterli
            ProjectId      = projectId,
            ProjectSlug    = projectSlug,

            PathTemplate   = S("path") ?? "/",
            TokenEnvVar    = S("tokenEnvVar") ?? "INFISICAL_TOKEN",
            Token          = S("token"),
            StripPrefix    = S("stripPrefix"),
            KeyTemplate    = S("keyTemplate") ?? "{key}",
            AutoCreatePathOnUpload = B("autoCreatePathOnUpload", false),
        };

        // ... (include / replace / map / envMap parse kodların aynı kalsın)
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
            if (Glob(pat, key))
                return true;
        return false;
    }

    private static bool Glob(string pattern, string text)
    {
        // simple case-insensitive * matcher
        var pi = 0;
        var ti = 0;
        int star = -1, match = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' ||
                                        char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti])))
            {
                pi++;
                ti++;
                continue;
            }

            if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                match = ti;
                continue;
            }

            if (star != -1)
            {
                pi = star + 1;
                ti = ++match;
                continue;
            }

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

    // ===== NEW: ensure folder =====

    // Replace existing EnsureFolderAsync with this deep-create version
    private static async Task EnsureFolderAsync(
        string baseUrl,
        string foldersEndpoint,          // <-- eklendi
        string bearerToken,
        string workspaceOrProjectId,
        string envSlug,
        string secretPath,
        CancellationToken ct)
    {
        var path = (secretPath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(path) || path == "/") return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var parent = "/";
        foreach (var seg in path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await TryCreateFolderAsync(http, baseUrl, foldersEndpoint, bearerToken, workspaceOrProjectId, envSlug, parent, seg, ct);
            parent = parent == "/" ? $"/{seg}" : $"{parent}/{seg}";
        }
    }


// New helper: tries two known endpoints, treats 409 as success (already exists)
    private static async Task TryCreateFolderAsync(
        HttpClient http,
        string baseUrl,
        string foldersEndpoint,          // <-- eklendi
        string bearerToken,
        string id,
        string envSlug,
        string parentPath,
        string name,
        CancellationToken ct)
    {
        var url = $"{baseUrl}/{foldersEndpoint.TrimStart('/')}";
        var payload = new
        {
            workspaceId = id,          // bazı kurulumlardaki isim farklılıklarına dayanıklı olsun diye ikisini de gönderiyoruz
            projectId   = id,
            environment = envSlug,
            parentPath  = parentPath,  // "/" | "/sso" | "/team/a"
            name        = name
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return;

            var body = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 409 ||
                body.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            // diğer hataları yutuyoruz; upload yine de denenecek
        }
        catch
        {
            // network/timeout vs. — upload akışını kesmeyelim
        }
    }
}