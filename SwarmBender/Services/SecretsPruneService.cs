using System.Text.RegularExpressions;
using SwarmBender.Services.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwarmBender.Services;


/// <summary>Deletes older secret versions by name pattern and/or label ownership.</summary>
public sealed class SecretsPruneService : ISecretsPruneService
{
    private readonly IDockerSecretClient _engine;

    // sb_{scope}_{env}_{key}_{version}
    private static readonly Regex NameRx = new(@"^sb_(?<scope>[A-Za-z0-9\-]+)_(?<env>[A-Za-z0-9\-]+)_(?<key>[^_]+)_(?<ver>v[0-9a-f]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SecretsPruneService(IDockerSecretClient engine) => _engine = engine;

    public async Task<SecretsPruneResult> PruneAsync(SecretsPruneRequest r, CancellationToken ct = default)
    {
        var detailed = await _engine.ListDetailedAsync(ct);

        // Filter only our secrets (owner label or name pattern)
        var ours = detailed
            .Where(s => (s.Labels.TryGetValue("owner", out var v) && string.Equals(v, "swarmbender", StringComparison.OrdinalIgnoreCase))
                        || NameRx.IsMatch(s.Name))
            .ToList();

        // Optional narrowing by scope/env
        if (!string.IsNullOrWhiteSpace(r.Scope))
            ours = ours.Where(s => NameRx.IsMatch(s.Name) && NameRx.Match(s.Name).Groups["scope"].Value.Equals(r.Scope, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrWhiteSpace(r.Environment))
            ours = ours.Where(s => NameRx.IsMatch(s.Name) && NameRx.Match(s.Name).Groups["env"].Value.Equals(r.Environment, StringComparison.OrdinalIgnoreCase)).ToList();

        // group by (scope, env, key)
        var groups = ours
            .Select(s =>
            {
                var m = NameRx.Match(s.Name);
                var scope = m.Success ? m.Groups["scope"].Value : "";
                var env = m.Success ? m.Groups["env"].Value : "";
                var key = m.Success ? m.Groups["key"].Value : s.Name;
                return (scope, env, key, s);
            })
            .GroupBy(x => (x.scope, x.env, x.key));

        var keep = Math.Max(1, r.KeepLatest);
        var removed = new List<string>();
        var kept = new List<string>();

        foreach (var g in groups)
        {
            // sort by CreatedAt desc; fallback to name desc
            var ordered = g.OrderByDescending(x => x.s.CreatedAt ?? DateTimeOffset.MinValue)
                           .ThenByDescending(x => x.s.Name, StringComparer.Ordinal)
                           .ToList();

            var toKeep = ordered.Take(keep).Select(x => x.s.Name).ToHashSet(StringComparer.Ordinal);
            var toRemove = ordered.Skip(keep).Select(x => x.s.Name).ToList();

            kept.AddRange(toKeep);

            if (!r.DryRun)
            {
                foreach (var name in toRemove)
                    if (await _engine.RemoveAsync(name, ct))
                        removed.Add(name);
            }
            else
            {
                removed.AddRange(toRemove); // simulated
            }
        }

        // write a small YAML report into ops/reports if is desired
        if (!string.IsNullOrWhiteSpace(r.ReportPath))
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var report = new
            {
                scope = r.Scope, environment = r.Environment, keep_latest = keep,
                kept, removed, generated_at = DateTimeOffset.UtcNow
            };
            Directory.CreateDirectory(Path.GetDirectoryName(r.ReportPath)!);
            File.WriteAllText(r.ReportPath, serializer.Serialize(report));
        }

        return new SecretsPruneResult(kept, removed, r.DryRun);
    }
}