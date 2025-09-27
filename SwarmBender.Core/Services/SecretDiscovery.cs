using System.Collections;
using System.Text.Json;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Providers.Azure;
using SwarmBender.Core.Providers.Infisical;
using SwarmBender.Core.Util;

namespace SwarmBender.Core.Services;

public sealed class SecretDiscovery : ISecretDiscovery
{
    private readonly IFileSystem _fs;
    private readonly IAzureKvCollector _kv;
    private readonly IInfisicalCollector _inf;

    public SecretDiscovery(IFileSystem fs, IAzureKvCollector kv, IInfisicalCollector inf)
    {
        _fs = fs;
        _kv = kv;
        _inf = inf;
    }

    public async Task<IReadOnlyList<DiscoveredSecret>> DiscoverAsync(
        string repoRoot, string stackId, string env, SbConfig cfg, CancellationToken ct)
    {
        // Render ile aynı normalizasyon
        env = env?.Trim().ToLowerInvariant() ?? "dev";

        // 1) Env torbasını düzleştir
        var envBag = await CollectEnvAsync(repoRoot, stackId, env, cfg, ct);
        Console.WriteLine(string.Join(Environment.NewLine, envBag));
        // 2) Secretize filtreleri
        var secretize = cfg.Secretize;
        if (secretize is not { Enabled: true } || secretize.Paths is null || secretize.Paths.Count == 0)
            return Array.Empty<DiscoveredSecret>();

        var matchers = secretize.Paths.Select(SecretUtil.WildcardToRegex).ToArray();

        // 3) Servis listesi (template varsa ondan, yoksa tek "all")
        var serviceNames = TryLoadServiceNames(repoRoot, stackId);
        var targets = serviceNames.Count > 0 ? serviceNames : new List<string> { "all" };

        // 4) Keşif (render ile aynı isimlendirme)
        var discovered = new List<DiscoveredSecret>();
        foreach (var kv in envBag)
        {
            if (!matchers.Any(rx => rx.IsMatch(kv.Key))) continue;
            var keyCanon = SecretUtil.ToComposeCanon(kv.Key);
            foreach (var svc in targets)
            {
                var externalName = SecretUtil.BuildExternalName(
                    cfg.Secrets?.NameTemplate,
                    stackId,
                    svc,
                    env,
                    keyCanon,
                    kv.Value,
                    cfg.Secrets?.VersionMode
                );

                var versionSuffix = SecretUtil.VersionSuffix(kv.Value, cfg.Secrets?.VersionMode);

                discovered.Add(new DiscoveredSecret(
                    Scope: $"{stackId}_{svc}",
                    Key: keyCanon,
                    Value: kv.Value,
                    Version: versionSuffix,
                    ExternalName: externalName
                ));
            }
        }

        return discovered;
    }

    private async Task<Dictionary<string, string>> CollectEnvAsync(
        string root, string stackId, string env, SbConfig cfg, CancellationToken ct)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // stacks/all/{env}/env + stacks/{stackId}/{env}/env + extra
        var dirs = new List<string>
        {
            $"stacks/all/{env}/env",
            $"stacks/{stackId}/{env}/env"
        };
        if (cfg.Providers.File.ExtraJsonDirs is { Count: > 0 })
        {
            foreach (var d in cfg.Providers.File.ExtraJsonDirs)
                dirs.Add(d.Replace("{stackId}", stackId).Replace("{env}", env));
        }

        foreach (var dir in dirs)
        {
            foreach (var f in _fs.GlobFiles(root, $"{dir}/*.json").OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var json = await _fs.ReadAllTextAsync(Path.Combine(root, f), ct);
                using var doc = JsonDocument.Parse(json);
                FlattenJson(doc.RootElement, null, bag);
            }
        }

        // Provider toplama sırası
        var order = cfg.Providers.Order?.Select(o => o.Type).ToList()
                    ?? new List<string> { "file", "env", "azure-kv", "infisical" };

        foreach (var type in order)
        {
            switch (type.Trim().ToLowerInvariant())
            {
                case "file":
                    break; // zaten okundu
                case "env":
                    MergeFromProcessEnvironment(root, stackId, env, cfg, bag);
                    break;
                case "azure-kv":
                    if (cfg.Providers.AzureKv is { Enabled: true } akv)
                        foreach (var it in await _kv.CollectAsync(akv, $"{stackId}/{env}", ct))
                            bag[it.Key] = it.Value;
                    break;
                case "infisical":
                    if (cfg.Providers.Infisical is { Enabled: true } inf)
                        foreach (var it in await _inf.CollectAsync(inf, stackId, env, ct))
                            bag[it.Key] = it.Value;
                    break;
            }
        }

        return bag;
    }

    private static void FlattenJson(JsonElement el, string? prefix, IDictionary<string, string> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    FlattenJson(p.Value, string.IsNullOrEmpty(prefix) ? p.Name : $"{prefix}.{p.Name}", sink);
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in el.EnumerateArray())
                    FlattenJson(item, string.IsNullOrEmpty(prefix) ? $"{i++}" : $"{prefix}.{i++}", sink);
                break;

            case JsonValueKind.String:
                Emit(prefix!, el.GetString() ?? "", sink);
                break;

            case JsonValueKind.Number:
                Emit(prefix!, el.ToString(), sink);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                Emit(prefix!, el.GetBoolean().ToString(), sink);
                break;
        }

        static void Emit(string key, string val, IDictionary<string, string> sink)
        {
            if (string.IsNullOrEmpty(key)) return;
            sink[key] = val;
            sink[key.Replace(".", "__")] = val; // "__" eşleniğini de ekle
        }
    }

    private static void MergeFromProcessEnvironment(
        string root, string stackId, string env, SbConfig cfg, Dictionary<string, string> bag)
    {
        var patterns = new List<string>();
        var searchSpecs = cfg.Providers.Env.AllowlistFileSearch ?? new();

        foreach (var spec in searchSpecs)
        {
            var resolved = spec.Replace("{stackId}", stackId).Replace("{env}", env);
            var full = Path.IsPathRooted(resolved) ? resolved : Path.Combine(root, resolved);

            if (Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*.json", SearchOption.TopDirectoryOnly))
                    TryReadAllowlistFile(file, patterns);
                continue;
            }

            if (File.Exists(full))
            {
                TryReadAllowlistFile(full, patterns);
                continue;
            }

            var dir = Path.GetDirectoryName(full);
            var mask = Path.GetFileName(full);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(mask) && Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, mask, SearchOption.TopDirectoryOnly))
                    TryReadAllowlistFile(file, patterns);
            }
        }

        if (patterns.Count == 0) return;

        var regs = patterns.Select(SecretUtil.WildcardToRegex).ToArray();
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            if (de.Key is string k && regs.Any(r => r.IsMatch(k)))
                bag[k] = de.Value?.ToString() ?? "";
        }

        static void TryReadAllowlistFile(string file, List<string> sink)
        {
            try
            {
                var json = File.ReadAllText(file);
                var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                if (arr is null) return;

                foreach (var p in arr)
                    if (!string.IsNullOrWhiteSpace(p))
                        sink.Add(p.Trim());
            }
            catch
            {
                // bozuk allowlist dosyasını yoksay
            }
        }
    }

    private static List<string> TryLoadServiceNames(string root, string stackId)
    {
        var tplYml = Path.Combine(root, "stacks", stackId, "docker-stack.template.yml");
        var tplYaml = Path.Combine(root, "stacks", stackId, "docker-stack.template.yaml");
        var file = File.Exists(tplYml) ? tplYml : File.Exists(tplYaml) ? tplYaml : null;
        if (file is null) return new();

        var names = new List<string>();
        var text = File.ReadAllText(file);

        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd();
            if (t.StartsWith("services:", StringComparison.Ordinal)) continue;
            if (t.StartsWith("#")) continue;

            var idx = t.IndexOf(':');
            // Örn: "  api:" gibi; boşluk içermeyen yalın başlık
            if (idx > 0 && !t.Contains(" "))
            {
                var n = t[..idx].Trim();
                if (!string.IsNullOrEmpty(n))
                    names.Add(n);
            }
        }

        return names;
    }
}