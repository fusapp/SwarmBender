using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>Implements secret rotation: create new engine secret names, update secrets-map, prune old versions.</summary>
public sealed class SecretsRotateService : ISecretsRotateService
{
    private readonly IYamlLoader _yaml;
    private readonly ISecretsProviderFactory _providers;
    private readonly ISecretNameStrategy _nameStrategy;

    public SecretsRotateService(IYamlLoader yaml, ISecretsProviderFactory providers, ISecretNameStrategy nameStrategy)
    {
        _yaml = yaml;
        _providers = providers;
        _nameStrategy = nameStrategy;
    }

    public async Task<SecretsRotateResult> RotateAsync(SecretsRotateRequest req, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(req.RootPath);
        var mapPath = Path.Combine(root, "ops", "vars", $"secrets-map.{req.Env}.yml");

        // Load provider (e.g., docker-cli)
        var providerCfgPath = Path.Combine(root, "ops", "vars", "secrets-provider.yml");
        var client = await _providers.CreateClientAsync(providerCfgPath, ct);

        // Load current map (may be empty)
        var map = await _yaml.LoadYamlAsync(mapPath, ct);
        var mapDict = map.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        // Read values source (JSON)
        var values = await ReadValuesAsync(req.ValuesJsonPath, req.ReadValuesFromStdin, ct);
        if (values.Count == 0)
            throw new InvalidOperationException("No values provided. Use --values <json-file> or --stdin with a JSON object like {\"KEY\":\"value\"}.");

        // Resolve selection (keys + optional matcher)
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in req.Keys) selectedKeys.Add(k);
        if (!string.IsNullOrWhiteSpace(req.Match))
            foreach (var key in values.Keys)
                if (MatchKey(key, req.Match!))
                    selectedKeys.Add(key);

        if (selectedKeys.Count == 0)
            throw new InvalidOperationException("No keys selected to rotate. Provide --key and/or --match that exist in the provided JSON.");

        var scopeToken = NormalizeScope(req.Scope);
        var items = new List<RotatedSecretItem>();

        foreach (var key in selectedKeys)
        {
            if (!values.TryGetValue(key, out var raw)) continue;

            var content = raw ?? string.Empty;
            var secretName = _nameStrategy.BuildName(scopeToken, req.Env, key, req.VersionMode, content);

            // create on engine
            var created = false;
            if (!req.DryRun)
                created = await client.EnsureCreatedAsync(secretName, content, BuildLabels(scopeToken, req, key), ct);
            else
                created = !(await client.ListNamesAsync(ct)).Contains(secretName);

            // update map
            var mapUpdated = false;
            if (!mapDict.TryGetValue(key, out var existing) || !string.Equals(existing, secretName, StringComparison.Ordinal))
            {
                mapDict[key] = secretName;
                mapUpdated = true;
            }

            // prune old versions by prefix
            var prunedCount = 0;
            if (!req.DryRun && req.Keep >= 0)
            {
                var prefix = BuildPrefix(scopeToken, req.Env, key); // <— local helper
                var allNames = await client.ListNamesAsync(ct);
                var candidates = allNames
                    .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                    .Where(n => !string.Equals(n, secretName, StringComparison.Ordinal))
                    .ToList();

                var keepOthers = candidates
                    .OrderByDescending(x => x, StringComparer.Ordinal)
                    .Take(Math.Max(0, req.Keep))
                    .ToHashSet(StringComparer.Ordinal);

                var toRemove = candidates.Where(n => !keepOthers.Contains(n)).ToList();
                foreach (var old in toRemove)
                    if (await client.RemoveAsync(old, ct)) prunedCount++;
            }

            items.Add(new RotatedSecretItem(key, secretName, created, mapUpdated, prunedCount));
        }

        // write map back
        if (!req.DryRun)
            await WriteMapAsync(mapPath, mapDict, ct);

        if (!req.Quiet)
            RenderSummary(items, mapPath, req.DryRun);

        return new SecretsRotateResult(items, mapPath);
    }

    // ---- helpers --------------------------------------------------------------

    private static string NormalizeScope(string s)
        => s.Equals("service", StringComparison.OrdinalIgnoreCase) ? "svc"
         : s.Equals("stack", StringComparison.OrdinalIgnoreCase)   ? "stack"
         : "global";

    // Docker secret adlarımız: sb_{scope}_{env}_{key}_{version}
    // Prefix: sb_{scope}_{env}_{key}_
    private static string BuildPrefix(string scope, string env, string key)
        => $"sb_{scope}_{env}_{key}_";

    private static IDictionary<string,string> BuildLabels(string scope, SecretsRotateRequest req, string key)
        => new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = "swarmbender",
            ["sb.env"] = req.Env,
            ["sb.scope"] = scope,
            ["sb.key"] = key,
            ["sb.stack"] = req.StackId ?? string.Empty,
            ["sb.service"] = req.ServiceName ?? string.Empty
        };

    private static async Task<Dictionary<string,string>> ReadValuesAsync(string? path, bool stdin, CancellationToken ct)
    {
        if (stdin)
        {
            using var input = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct);
            ms.Position = 0;
            using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: ct);
            return JsonObjectToDict(doc.RootElement);
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            using var fs = File.OpenRead(path!);
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
            return JsonObjectToDict(doc.RootElement);
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string,string> JsonObjectToDict(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Values JSON must be an object like { \"KEY\": \"value\" }.");
        var d = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateObject())
            d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? string.Empty) : p.Value.ToString();
        return d;
    }

    private static bool MatchKey(string key, string matcher)
    {
        if (matcher.StartsWith("/") && matcher.EndsWith("/") && matcher.Length > 2)
            return new Regex(matcher[1..^1], RegexOptions.IgnoreCase | RegexOptions.Compiled).IsMatch(key);

        var glob = "^" + Regex.Escape(matcher).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(key, glob, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private async Task WriteMapAsync(string path, IDictionary<string,string> map, CancellationToken ct)
    {
        var lines = new List<string> { "# KEY -> engine secret name" };
        foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{kv.Key}: {kv.Value}");
        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
    }

    private static void RenderSummary(IReadOnlyList<RotatedSecretItem> items, string mapPath, bool dry)
    {
        var t = new Table().Expand();
        t.AddColumn("Key");
        t.AddColumn("New Secret");
        t.AddColumn("Created");
        t.AddColumn("Map Updated");
        t.AddColumn("Pruned");

        foreach (var i in items)
            t.AddRow(i.Key, i.SecretName, i.Created ? "yes" : "no", i.MapUpdated ? "yes" : "no", i.OldVersionsPruned.ToString());

        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"Map: {(dry ? "[italic]dry-run[/]" : mapPath)}");
    }
}