using System.Security.Cryptography;
using System.Text;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwarmBender.Services;


public sealed class SecretsSyncService : ISecretsSyncService
{
    private readonly SecretPolicyLoader _policyLoader;
    private readonly ISecretProvidersHub _providers;
    private readonly ISecretNameStrategy _namer;
    private readonly IDockerSecretClient _engine;

    public SecretsSyncService(SecretPolicyLoader policyLoader, ISecretProvidersHub providers,
        ISecretNameStrategy namer, IDockerSecretClient engine)
        => (_policyLoader, _providers, _namer, _engine) = (policyLoader, providers, namer, engine);

    public async Task<SecretsSyncResult> SyncAsync(SecretsSyncRequest request, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(request.RootPath);
        var policy = await _policyLoader.LoadAsync(root, ct);
        if (!policy.Enabled)
            return new SecretsSyncResult(0, 0, "", Array.Empty<string>());

        var providers = await _providers.ResolveAsync(root, ct);

        // Aggregate from providers (later add include filters from ops/providers.yml)
        var aggregated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            var items = await p.GetAsync(root, request.Scope, request.Env, ct);
            foreach (var kv in items)
                aggregated[kv.Key] = kv.Value;
        }

        // Filter keys against policy paths (supports 'ConnectionStrings.*' and flat 'Redis__*' etc.)
        bool match(string key)
        {
            foreach (var pattern in policy.Paths)
            {
                if (pattern.EndsWith(".*", StringComparison.Ordinal))
                {
                    var prefix = pattern.Substring(0, pattern.Length - 2);
                    // normalize ':' or '.' to '__'
                    var flatPrefix = prefix.Replace(":", "__").Replace(".", "__");
                    if (key.StartsWith(flatPrefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.Contains("__", StringComparison.Ordinal) || pattern.Contains(":", StringComparison.Ordinal))
                {
                    var flat = pattern.Replace(":", "__");
                    if (string.Equals(flat, key, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        var candidates = aggregated
            .Where(kv => match(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // Versioning
        string ComputeVersion(string key, string value)
        {
            var mode = request.VersionModeOverride ?? policy.VersionMode;
            return mode switch
            {
                "content-sha" => ShortSha(value),
                "kv-version" => ShortSha(value), // placeholder until a KV provider injects a version id
                "hmac" => ShortSha(value),       // placeholder; later add salt-based HMAC
                _ => ShortSha(value)
            };
        }

        string ShortSha(string s)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..6];
        }

        var entries = new List<string>();
        var created = 0;
        var skipped = 0;

        var labels = new Dictionary<string, string>(policy.Labels, StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = policy.Labels.TryGetValue("owner", out var o) && !string.IsNullOrEmpty(o) ? o : "swarmbender",
            ["scope"] = request.Scope,
            ["env"] = request.Env
        };

        var existing = await _engine.ListNamesAsync(ct);

        foreach (var kv in candidates)
        {
            var key = kv.Key;
            var val = kv.Value;
            var version = ComputeVersion(key, val);
            var name = _namer.BuildName(request.Scope, request.Env, key, "v" + version, policy.NameTemplate);

            var didCreate = false;
            if (!request.DryRun && !existing.Contains(name))
            {
                didCreate = await _engine.EnsureCreatedAsync(name, val, labels, ct);
            }

            if (didCreate) created++; else skipped++;
            entries.Add($"{key}: {name}");
        }

        // Write secrets-map.<env>.yml
        var mapDir = Path.Combine(root, "ops", "vars");
        Directory.CreateDirectory(mapDir);
        var mapPath = Path.Combine(mapDir, $"secrets-map.{request.Env}.yml");

        var map = candidates.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var version = ComputeVersion(kv.Key, kv.Value);
                return _namer.BuildName(request.Scope, request.Env, kv.Key, "v" + version, policy.NameTemplate);
            },
            StringComparer.OrdinalIgnoreCase);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var yaml = serializer.Serialize(map);
        if (!request.DryRun)
            await File.WriteAllTextAsync(mapPath, yaml, ct);

        return new SecretsSyncResult(created, skipped, request.DryRun ? "(dry-run)" : mapPath, entries);
    }
}