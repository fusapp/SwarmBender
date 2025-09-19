using System.Text.RegularExpressions;
using SwarmBender.Services.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwarmBender.Services;

/// <summary>Checks consistency between secrets-map.<env>.yml and Swarm engine.</summary>
public sealed class SecretsDoctorService : ISecretsDoctorService
{
    private readonly IDockerSecretClient _engine;

    private static readonly Regex NameRx = new(@"^sb_(?<scope>[A-Za-z0-9\-]+)_(?<env>[A-Za-z0-9\-]+)_(?<key>[^_]+)_(?<ver>v[0-9a-f]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SecretsDoctorService(IDockerSecretClient engine) => _engine = engine;

    public async Task<SecretsDoctorResult> CheckAsync(SecretsDoctorRequest r, CancellationToken ct = default)
    {
        var engineNames = await _engine.ListNamesAsync(ct);
        var engineSet = new HashSet<string>(engineNames, StringComparer.Ordinal);

        // load map: ops/vars/secrets-map.<env>.yml
        var mapPath = r.MapPath ?? Path.Combine(r.RootPath, "ops", "vars", $"secrets-map.{r.Environment}.yml");
        if (!File.Exists(mapPath)) throw new FileNotFoundException("secrets map not found", mapPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var map = deserializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mapPath))
                  ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var missingOnEngine = new List<string>();
        var orphanedOnEngine = new List<string>();
        var multiVersions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // expected -> engine check
        foreach (var kv in map)
        {
            var expectedName = kv.Value;
            if (!engineSet.Contains(expectedName))
                missingOnEngine.Add(expectedName);
        }

        // engine -> “belongs to our env/scope” ve map’te yoksa orphan
        foreach (var name in engineSet)
        {
            var m = NameRx.Match(name);
            if (!m.Success) continue;

            var env = m.Groups["env"].Value;
            if (!string.Equals(env, r.Environment, StringComparison.OrdinalIgnoreCase)) continue;

            if (!map.Values.Contains(name, StringComparer.Ordinal))
                orphanedOnEngine.Add(name);
        }

        // same key with multiple versions present (engine-side)
        var engineDetailed = await _engine.ListDetailedAsync(ct);
        var byKey = engineDetailed
            .Select(s =>
            {
                var m = NameRx.Match(s.Name);
                if (!m.Success) return (ok:false, key:"", name:s.Name);
                var env = m.Groups["env"].Value;
                var key = m.Groups["key"].Value;
                return (ok: string.Equals(env, r.Environment, StringComparison.OrdinalIgnoreCase), key, name:s.Name);
            })
            .Where(x => x.ok)
            .GroupBy(x => x.key);

        foreach (var g in byKey)
        {
            var names = g.Select(x => x.name).ToList();
            if (names.Count > 1) multiVersions[g.Key] = names;
        }

        return new SecretsDoctorResult(mapPath, missingOnEngine, orphanedOnEngine, multiVersions);
    }
}