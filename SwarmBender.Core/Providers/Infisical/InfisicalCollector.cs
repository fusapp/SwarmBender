using Infisical.Sdk;
using Infisical.Sdk.Model;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Util;

namespace SwarmBender.Core.Providers.Infisical;

public sealed class InfisicalCollector : IInfisicalCollector
{
    /// <summary>
    /// Infisical’dan sırları çeker; include pattern’lerine göre filtreler, replace ve keyTemplate uygular.
    /// Auth: Universal Auth (Machine Identity) — env varlardan: INFISICAL_CLIENT_ID / INFISICAL_CLIENT_SECRET
    /// </summary>
    public async Task<Dictionary<string, string>> CollectAsync(ProvidersInfisical cfg, string env, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (cfg is null || !cfg.Enabled)
            return result;

       
        var envSlug = cfg.EnvMap.TryGetValue(env, out var mapped) ? mapped : env;

        var settingsBuilder = new InfisicalSdkSettingsBuilder();
        if (!string.IsNullOrWhiteSpace(cfg.BaseUrl))
            settingsBuilder = settingsBuilder.WithHostUri(cfg.BaseUrl);

        var settings = settingsBuilder.Build();
        var client = new InfisicalClient(settings);

        var clientId = Environment.GetEnvironmentVariable("INFISICAL_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("INFISICAL_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return result;
        }

        // Login
        await client.Auth().UniversalAuth().LoginAsync(clientId, clientSecret).ConfigureAwait(false);

       
        var path = cfg.PathTemplate;
        if (string.IsNullOrWhiteSpace(path)) path = "/";
        path = path.Replace("{scope}", "/");

        var options = new ListSecretsOptions
        {
            EnvironmentSlug = envSlug,
            SecretPath = path,
            ProjectId = cfg.WorkspaceId ?? string.Empty,
            Recursive = true,
            ExpandSecretReferences = true,
            ViewSecretValue = true,
            SetSecretsAsEnvironmentVariables = false
        };

        var secrets = await client.Secrets().ListAsync(options).ConfigureAwait(false);
        if (secrets == null || secrets.Length == 0)
            return result;

        // include pattern’leri hazırlanır (boşsa tümünü kabul et)
        var includes = (cfg.Include != null && cfg.Include.Count > 0)
            ? cfg.Include
            : new List<string> { "*" };

        foreach (var s in secrets)
        {
            ct.ThrowIfCancellationRequested();

            var key = s.SecretKey ?? string.Empty;
            var val = s.SecretValue ?? string.Empty;

            if (!includes.Any(p => Globber.IsMatch(key, p)))
                continue;

            var mappedKey = ApplyReplace(cfg.Replace, key);

            var finalKey = (cfg.KeyTemplate ?? "{key}").Replace("{key}", mappedKey);

            result[finalKey] = val;
        }

        return result;
    }

    private static string ApplyReplace(Dictionary<string, string> replace, string input)
    {
        if (replace == null || replace.Count == 0 || string.IsNullOrEmpty(input))
            return input;

        var output = input;
        foreach (var kv in replace)
        {
            if (!string.IsNullOrEmpty(kv.Key))
                output = output.Replace(kv.Key, kv.Value ?? string.Empty, StringComparison.Ordinal);
        }

        return output;
    }
}