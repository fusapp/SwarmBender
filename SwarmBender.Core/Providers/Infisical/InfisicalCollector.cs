using Infisical.Sdk;
using Infisical.Sdk.Model;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Util;

namespace SwarmBender.Core.Providers.Infisical;

public sealed class InfisicalCollector : IInfisicalCollector
{
    /// <summary>
    /// Infisical’dan sırları toplar:
    /// - routes.readPaths (varsa) üzerinden ÇOKLU path okur; yoksa {stackId}_all fallback.
    /// - include -> replace -> keyTemplate sırasını uygular.
    /// Auth: Universal Auth (INFISICAL_CLIENT_ID / INFISICAL_CLIENT_SECRET)
    /// </summary>
    public async Task<Dictionary<string, string>> CollectAsync(
        ProvidersInfisical cfg, string stackId, string env, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg is null || !cfg.Enabled) return result;

        // env 'dev'/'prod' haritalaması
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

        // 1) Okunacak path listesini çıkar
        var readPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (cfg.Routes is { Count: > 0 })
        {
            foreach (var r in cfg.Routes)
                if (r.ReadPaths is { Count: > 0 })
                    foreach (var rp in r.ReadPaths)
                        readPaths.Add(BuildPath(cfg.PathTemplate, stackId, envSlug, rp)); // absolute ise direkt döner
        }

        // fallback: PathTemplate + {stackId}_all
        if (readPaths.Count == 0)
            readPaths.Add(BuildPath(cfg.PathTemplate, stackId, envSlug, "{stackId}_all"));

        // include patternleri
        var includes = (cfg.Include != null && cfg.Include.Count > 0)
            ? cfg.Include
            : new List<string> { "*" };

        // 2) Her path için ListAsync + birleştir
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
            catch
            {
                // path yok/erişilemedi: sessizce geç
                continue;
            }

            if (secrets is null || secrets.Length == 0) continue;

            foreach (var s in secrets)
            {
                ct.ThrowIfCancellationRequested();

                var key = s.SecretKey ?? string.Empty;
                var val = s.SecretValue ?? string.Empty;

                if (!includes.Any(p => Globber.IsMatch(key, p)))
                    continue;

                // replace + keyTemplate
                var mappedKey = ApplyReplace(cfg.Replace, key);
                var finalKey = (cfg.KeyTemplate ?? "{key}").Replace("{key}", mappedKey);

                // last-wins
                result[finalKey] = val;
            }
        }

        return result;
    }

    // ---- helpers ----

    private static string BuildPath(string? template, string stackId, string envSlug, string scopeOrPath)
    {
        // Absolute path ise doğrudan kullan (örn: "/shared/db")
        if (!string.IsNullOrWhiteSpace(scopeOrPath) && scopeOrPath.StartsWith("/"))
            return scopeOrPath;

        // Değilse template uygula (default: "/{stackId}_all")
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
        // // -> / normalize
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