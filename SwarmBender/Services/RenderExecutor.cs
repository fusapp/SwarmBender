using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Text;
using System.Text.Json;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>
/// Minimal renderer:
/// - Loads stack template
/// - Applies aliases (template service -> canonical service)
/// - Layers: global baselines (stacks/all/<env>) + service overrides (services/<canonical>/<area>/<env>)
/// - Areas: env, labels, deploy, logging, mounts (secrets/configs attachments)
/// - Adds top-level secrets/configs from stacks/<stack>/(secrets.yml|configs.yml)
/// - Writes to ops/state/last and optionally to ops/state/history
/// </summary>
public sealed class RenderExecutor : IRenderExecutor
{
    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private readonly IYamlLoader _yaml;

    public RenderExecutor(IYamlLoader yaml) => _yaml = yaml;

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(request.RootPath);
        var stackRoot = Path.Combine(root, "stacks", request.StackId);
        if (!Directory.Exists(stackRoot))
            throw new DirectoryNotFoundException($"Stack not found: {stackRoot}");

        var templatePath = Path.Combine(stackRoot, "docker-stack.template.yml");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Template not found: {templatePath}");

        var template = await _yaml.LoadYamlAsync(templatePath, ct);
        if (!template.TryGetValue("services", out var svcNode) || svcNode is not IDictionary<string, object?> templateServices)
            throw new InvalidOperationException("Template missing 'services' section.");

        // aliases
        var aliases = await _yaml.LoadYamlAsync(Path.Combine(stackRoot, "aliases.yml"), ct);
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in aliases)
            aliasMap[kv.Key] = kv.Value?.ToString() ?? kv.Key;

        // Outputs
        var outputs = new List<RenderOutput>();

        foreach (var env in request.Environments)
        {
            var envLower = env.ToLowerInvariant();

            // Start from template clone
            var rendered = new Dictionary<string, object?>(template, StringComparer.OrdinalIgnoreCase);

            // Compose services with layering
            var finalServices = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in templateServices)
            {
                var tmplSvcName = svc.Key;
                var svcMap = svc.Value as IDictionary<string, object?> ?? new Dictionary<string, object?>();
                var canonical = aliasMap.TryGetValue(tmplSvcName, out var c) ? (string.IsNullOrWhiteSpace(c) ? tmplSvcName : c) : tmplSvcName;

                var composedService = await ComposeServiceAsync(root, canonical, envLower, svcMap, ct);
                finalServices[tmplSvcName] = composedService; // preserve template service key; canonical only for lookups
            }

            rendered["services"] = finalServices;

            // Top-level secrets/configs union from stack files if present
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "secrets.yml"), "secrets", ct);
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "configs.yml"), "configs", ct);

            // Serialize
            var yaml = YamlWriter.Serialize(rendered);

            // Write/preview
            var outDir = Path.IsPathRooted(request.OutDir) ? request.OutDir : Path.Combine(root, request.OutDir);
            var outFile = Path.Combine(outDir, $"{request.StackId}-{envLower}.stack.yml");

            if (!request.DryRun)
            {
                Directory.CreateDirectory(outDir);
                await File.WriteAllTextAsync(outFile, yaml, new UTF8Encoding(false), ct);

                if (request.WriteHistory)
                {
                    var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var hist = Path.Combine(root, "ops", "state", "history", stamp);
                    Directory.CreateDirectory(hist);
                    var histFile = Path.Combine(hist, $"{request.StackId}-{envLower}.stack.yml");
                    await File.WriteAllTextAsync(histFile, yaml, new UTF8Encoding(false), ct);
                }
            }

            if (!request.Quiet && request.Preview)
            {
                AnsiConsole.WriteLine($"# ===== Render Preview: {request.StackId} [{envLower}] =====");
                AnsiConsole.WriteLine(yaml);
            }

            outputs.Add(new RenderOutput(envLower, outFile));
        }

        return new RenderResult(outputs);
    }

    private async Task<IDictionary<string, object?>> ComposeServiceAsync(string root, string canonicalService, string env, IDictionary<string, object?> templateSvc, CancellationToken ct)
    {
        // Clone template service
        var svc = new Dictionary<string, object?>(templateSvc, StringComparer.OrdinalIgnoreCase);

        // --- ENVIRONMENT ---
        var envVars = MergeHelpers.NormalizeEnvironment(svc.TryGetValue("environment", out var envNode) ? envNode : null);

        // global baseline env
        var globalEnvDir = Path.Combine(root, "stacks", "all", env, "env");
        await MergeJsonEnvDirAsync(globalEnvDir, envVars, ct);

        // service baseline env
        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);
        await MergeJsonEnvDirAsync(svcEnvDir, envVars, ct);

        if (envVars.Count > 0)
            svc["environment"] = MergeHelpers.EnvironmentToYaml(envVars);

        // --- LABELS ---
        var labelDict = MergeHelpers.NormalizeLabels(svc.TryGetValue("labels", out var labelsNode) ? labelsNode : null);

        // deploy.labels (merge into same set)
        if (svc.TryGetValue("deploy", out var deployNode) && deployNode is IDictionary<string, object?> deployMap &&
            deployMap.TryGetValue("labels", out var depLabelsNode))
        {
            foreach (var kv in MergeHelpers.NormalizeLabels(depLabelsNode))
                labelDict[kv.Key] = kv.Value;
            deployMap.Remove("labels"); // normalize to service-level labels
        }

        // global baseline labels
        await MergeYamlLabelDirAsync(Path.Combine(root, "stacks", "all", env, "labels"), labelDict, ct);

        // service baseline labels
        await MergeYamlLabelDirAsync(Path.Combine(root, "services", canonicalService, "labels", env), labelDict, ct);

        if (labelDict.Count > 0)
            svc["labels"] = MergeHelpers.LabelsToYaml(labelDict);

        // --- DEPLOY ---
        var deploy = svc.TryGetValue("deploy", out var d) && d is IDictionary<string, object?> dm ? new Dictionary<string, object?>(dm, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await MergeYamlAreaAsync(Path.Combine(root, "stacks", "all", env, "deploy"), deploy, ct);
        await MergeYamlAreaAsync(Path.Combine(root, "services", canonicalService, "deploy", env), deploy, ct);
        if (deploy.Count > 0) svc["deploy"] = deploy;

        // --- LOGGING ---
        var logging = svc.TryGetValue("logging", out var lg) && lg is IDictionary<string, object?> lgm ? new Dictionary<string, object?>(lgm, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await MergeYamlAreaAsync(Path.Combine(root, "stacks", "all", env, "logging"), logging, ct);
        await MergeYamlAreaAsync(Path.Combine(root, "services", canonicalService, "logging", env), logging, ct);
        if (logging.Count > 0) svc["logging"] = logging;

        // --- MOUNTS (secrets/configs attachments) ---
        var mounts = svc.TryGetValue("mounts", out var mn) && mn is IDictionary<string, object?> mm ? new Dictionary<string, object?>(mm, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await MergeYamlAreaAsync(Path.Combine(root, "stacks", "all", env, "mounts"), mounts, ct);
        await MergeYamlAreaAsync(Path.Combine(root, "services", canonicalService, "mounts", env), mounts, ct);

        // If mounts contains secrets/configs arrays, project them to service-level 'secrets'/'configs'
        ProjectMountsToService(mounts, svc);

        return svc;
    }

    private static void ProjectMountsToService(IDictionary<string, object?> mounts, IDictionary<string, object?> svc)
    {
        if (mounts.TryGetValue("secrets", out var s) && s is IEnumerable<object?> secrets)
            svc["secrets"] = secrets.ToList();

        if (mounts.TryGetValue("configs", out var c) && c is IEnumerable<object?> configs)
            svc["configs"] = configs.ToList();
    }

    private async Task MergeTopLevelAsync(IDictionary<string, object?> rendered, string filePath, string topKey, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;
        var map = await _yaml.LoadYamlAsync(filePath, ct);
        if (!map.TryGetValue(topKey, out var node) || node is not IDictionary<string, object?> top) return;

        var dict = rendered.TryGetValue(topKey, out var existing) && existing is IDictionary<string, object?> ex
            ? ex : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in top)
            dict[kv.Key] = kv.Value;

        rendered[topKey] = dict;
    }

    private async Task MergeYamlAreaAsync(string dir, IDictionary<string, object?> target, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.yml"))
        {
            var map = await _yaml.LoadYamlAsync(file, ct);
            foreach (var kv in map)
            {
                if (kv.Value is IDictionary<string, object?> m)
                {
                    if (!target.TryGetValue(kv.Key, out var ex) || ex is not IDictionary<string, object?> exm)
                        target[kv.Key] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    target[kv.Key] = MergeHelpers.DeepMerge((IDictionary<string, object?>)target[kv.Key]!, m);
                }
                else
                {
                    target[kv.Key] = kv.Value;
                }
            }
        }
    }

    private async Task MergeYamlLabelDirAsync(string dir, IDictionary<string, string> labels, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.yml"))
        {
            var map = await _yaml.LoadYamlAsync(file, ct);
            foreach (var kv in MergeHelpers.NormalizeLabels(map))
                labels[kv.Key] = kv.Value;
        }
    }

    private async Task MergeJsonEnvDirAsync(string dir, IDictionary<string, string> env, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var s = File.OpenRead(file);
                var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                foreach (var key in MergeHelpers.ReadJsonKeys(doc.RootElement))
                    env[key] = doc.RootElement.GetPropertyChainValue(key) ?? string.Empty;
            }
            catch
            {
                // ignore invalid json
            }
        }
    }
}

internal static class JsonExtensions
{
    public static string? GetPropertyChainValue(this JsonElement el, string chain)
    {
        var parts = chain.Split(new[] { "__" }, StringSplitOptions.None);
        JsonElement cur = el;
        foreach (var p in parts)
        {
            if (!cur.TryGetProperty(p, out var next)) return null;
            cur = next;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => cur.GetRawText()
        };
    }
}
