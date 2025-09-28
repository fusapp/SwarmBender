using Infisical.Sdk;
using Infisical.Sdk.Model;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Util;

namespace SwarmBender.Core.Providers.Infisical;

public sealed class InfisicalCollector : IInfisicalCollector
{
    /// <summary>
    /// Infisical’dan sırları toplar:
    /// - routes.readPaths (varsa) üzerinden çoklu path okur; yoksa PathTemplate + "{stackId}_all" fallback.
    /// - include eşleşmesini hem orijinal hem de replace edilmiş formlarda yapar.
    /// - replace -> keyTemplate uygular; ÇIKTI anahtarı compose kanonuna çevirir ('.' -> '__').
    /// Auth: Universal Auth (INFISICAL_CLIENT_ID / INFISICAL_CLIENT_SECRET).
    /// </summary>
    public async Task<Dictionary<string, string>> CollectAsync(
        ProvidersInfisical cfg, string stackId, string env, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg is null || !cfg.Enabled) return result;

        // env haritalaması (dev/prod vs)
        var envSlug = (cfg.EnvMap != null && cfg.EnvMap.TryGetValue(env, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            ? mapped : env;

        // SDK & Auth
        var settingsBuilder = new InfisicalSdkSettingsBuilder();
        if (!string.IsNullOrWhiteSpace(cfg.BaseUrl))
            settingsBuilder = settingsBuilder.WithHostUri(cfg.BaseUrl);
        var client = new InfisicalClient(settingsBuilder.Build());

        var clientId = Environment.GetEnvironmentVariable("INFISICAL_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("INFISICAL_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return result;

        await client.Auth().UniversalAuth().LoginAsync(clientId, clientSecret).ConfigureAwait(false);

        // 1) Okunacak path listesi
        var readPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.Routes is { Count: > 0 })
        {
            foreach (var r in cfg.Routes)
                if (r.ReadPaths is { Count: > 0 })
                    foreach (var rp in r.ReadPaths)
                        readPaths.Add(BuildPath(cfg.PathTemplate, stackId, envSlug, rp));
        }
        if (readPaths.Count == 0)
            readPaths.Add(BuildPath(cfg.PathTemplate, stackId, envSlug, "{stackId}_all"));

        // 2) include desenleri (boşsa tümü)
        var includes = (cfg.Include != null && cfg.Include.Count > 0)
            ? cfg.Include
            : new List<string> { "*" };

        // 3) Her path’i tara → birleştir (last-wins)
        foreach (var rp in readPaths)
        {
            ct.ThrowIfCancellationRequested();

            var secretPath = NormalizePath(rp);
            var options = new ListSecretsOptions
            {
                EnvironmentSlug = envSlug,
                SecretPath = secretPath,
                ProjectId = cfg.WorkspaceId ?? string.Empty,
                Recursive = true,
                ExpandSecretReferences = true,
                ViewSecretValue = true,
                SetSecretsAsEnvironmentVariables = false
            };

            Secret[]? secrets;
            try
            {
                secrets = await client.Secrets().ListAsync(options).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // path yok/erişilemedi → kısa uyarı ve devam
                System.Console.Error.WriteLine($"[infisical] skip path '{secretPath}' env='{envSlug}': {ex.Message}");
                continue;
            }

            if (secrets is null || secrets.Length == 0) continue;

            foreach (var s in secrets)
            {
                ct.ThrowIfCancellationRequested();

                var keyOrig = s.SecretKey ?? string.Empty;   // Infisical anahtarı
                var val     = s.SecretValue ?? string.Empty;

                // ——— include eşleşmesi (4 varyasyon) ———
                // key varyasyonları
                var keyRepl     = ApplyReplace(cfg.Replace, keyOrig);              // "__" -> "_" gibi
                var keyRev      = ReverseDoubleUnderscore(keyOrig, cfg);           // "_" -> "__" (geri çevirme)
                var keyRevRepl  = ApplyReplace(cfg.Replace, keyRev);

                bool included = false;
                foreach (var patt in includes)
                {
                    // pattern varyasyonları
                    var pattOrig = patt;
                    var pattRepl = ApplyReplace(cfg.Replace, pattOrig);

                    if (Globber.IsMatch(keyOrig, pattOrig) ||
                        Globber.IsMatch(keyRev, pattOrig)  ||
                        Globber.IsMatch(keyRepl, pattRepl) ||
                        Globber.IsMatch(keyRevRepl, pattRepl))
                    {
                        included = true;
                        break;
                    }
                }
                if (!included) continue;

                // ——— anahtar üretimi: replace + keyTemplate ———
                var mappedKey = keyRepl; // replace sonrası key
                var templated = (cfg.KeyTemplate ?? "{key}").Replace("{key}", mappedKey);

                // ÇIKTI: "__" geri çevir + compose kanonu ('.' -> '__')
                var reversed = ReverseDoubleUnderscore(templated, cfg);
                var finalKey = SecretUtil.ToComposeCanon(reversed);

                // last-wins
                result[finalKey] = val;
            }
        }

        return result;
    }

    // ---- helpers ----

    private static string ReverseDoubleUnderscore(string input, ProvidersInfisical cfg)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // cfg.Replace["__"] örn "_" ise, geri çevir: "_" -> "__"
        if (cfg.Replace != null && cfg.Replace.TryGetValue("__", out var token) && !string.IsNullOrEmpty(token))
            return input.Replace(token, "__");
        return input;
    }

    private static string BuildPath(string? template, string stackId, string envSlug, string scopeOrPath)
    {
        if (!string.IsNullOrWhiteSpace(scopeOrPath) && scopeOrPath.StartsWith("/"))
            return scopeOrPath;

        var t = string.IsNullOrWhiteSpace(template) ? "/{stackId}_all" : template;
        t = t.Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
             .Replace("{env}", envSlug, StringComparison.OrdinalIgnoreCase)
             .Replace("{scope}", scopeOrPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        return t;
    }

    private static string NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "/";
        var s = p.Trim();
        if (!s.StartsWith("/")) s = "/" + s;
        s = System.Text.RegularExpressions.Regex.Replace(s, "/{2,}", "/");
        return s;
    }

    private static string ApplyReplace(Dictionary<string, string> replace, string input)
    {
        if (replace == null || replace.Count == 0 || string.IsNullOrEmpty(input))
            return input;

        var output = input;
        foreach (var kv in replace)
            if (!string.IsNullOrEmpty(kv.Key))
                output = output.Replace(kv.Key, kv.Value ?? string.Empty, StringComparison.Ordinal);
        return output;
    }
}