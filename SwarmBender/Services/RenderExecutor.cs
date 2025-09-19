// File: SwarmBender/Services/RenderExecutor.cs
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>
/// Renderer v1.7
/// - Layered composition (template + stacks/all + services/<svc>)
/// - appsettings*.json: env (flatten) | config (swarm config + mount)
/// - ${VAR} token interpolation (keys & string values)
/// - Process ENV with allowlist (use-envvars.json)
/// - Special ${ENVVARS}
/// - Healthcheck.test -> flow sequence
/// - Environment -> block sequence of "KEY=VALUE" (template order first)
/// - NEW: Labels -> block sequence of "key=value" (template order first, then alphabetic)
/// </summary>
public sealed class RenderExecutor : IRenderExecutor
{
    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .WithTypeConverter(new FlowSeqYamlConverter()) // healthcheck.test flow array
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly Regex TokenRx = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

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

        // aliases (optional)
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliasesPath = Path.Combine(stackRoot, "aliases.yml");
        if (File.Exists(aliasesPath))
        {
            var aliases = await _yaml.LoadYamlAsync(aliasesPath, ct);
            foreach (var kv in aliases)
                aliasMap[kv.Key] = kv.Value?.ToString() ?? kv.Key;
        }

        var outputs = new List<RenderOutput>();

        foreach (var env in request.Environments)
        {
            var envLower = env.ToLowerInvariant();

            var rendered = new Dictionary<string, object?>(template, StringComparer.OrdinalIgnoreCase);

            var finalServices = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in templateServices)
            {
                var tmplSvcName = svc.Key;
                var svcMap = svc.Value as IDictionary<string, object?> ?? new Dictionary<string, object?>();
                var canonical = aliasMap.TryGetValue(tmplSvcName, out var c) ? (string.IsNullOrWhiteSpace(c) ? tmplSvcName : c) : tmplSvcName;

                var composedService = await ComposeServiceAsync(root, request, canonical, envLower, svcMap, ct);
                finalServices[tmplSvcName] = composedService;
            }

            rendered["services"] = finalServices;

            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "secrets.yml"), "secrets", ct);
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "configs.yml"), "configs", ct);

            // style hints (healthcheck.test -> flow seq)
            ApplyYamlStyleHints(rendered);

            var yaml = YamlWriter.Serialize(rendered);

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

    private async Task<IDictionary<string, object?>> ComposeServiceAsync(
        string root,
        RenderRequest request,
        string canonicalService,
        string env,
        IDictionary<string, object?> templateSvc,
        CancellationToken ct)
    {
        var svc = new Dictionary<string, object?>(templateSvc, StringComparer.OrdinalIgnoreCase);

        // --- capture template env order (if any) before normalization ---
        var templateEnvOrder = ExtractEnvKeyOrderFromNode(templateSvc.TryGetValue("environment", out var rawEnvNode) ? rawEnvNode : null);

        // --- capture template labels preferred order (service + deploy.labels) ---
        var templateLabelsOrder = new List<string>();
        var rawLabelsNode = templateSvc.TryGetValue("labels", out var labelsNode) ? labelsNode : null;
        templateLabelsOrder.AddRange(ExtractLabelOrderFromNode(rawLabelsNode));
        if (templateSvc.TryGetValue("deploy", out var deployNode0) && deployNode0 is IDictionary<string, object?> dep0 &&
            dep0.TryGetValue("labels", out var depLabelsNode0))
        {
            foreach (var k in ExtractLabelOrderFromNode(depLabelsNode0))
                if (!templateLabelsOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                    templateLabelsOrder.Add(k);
        }

        // Track keys sourced from files (JSON/appsettings) -> file wins over process env
        var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --- ENVIRONMENT (template base) ---
        var envVars = MergeHelpers.NormalizeEnvironment(rawEnvNode);

        // Merge from global/service JSON (skip appsettings when mode=config)
        var globalEnvDir = Path.Combine(root, "stacks", "all", env, "env");
        await MergeJsonEnvDirAsync(globalEnvDir, envVars, skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct, fileKeys);

        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);
        await MergeJsonEnvDirAsync(svcEnvDir, envVars, skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct, fileKeys);

        // appsettings: may add/override env vars (env mode)
        await ApplyAppSettingsAsync(root, request, canonicalService, env, envVars, svc, fileKeys, ct);

        // --- Process env overlay with allowlist ---
        var allow = await LoadAllowListAsync(root, request.StackId, canonicalService, env, ct);
        var processEnv = GetProcessEnvironment();

        foreach (var kv in processEnv)
        {
            var key = kv.Key;
            var val = kv.Value ?? string.Empty;

            if (fileKeys.Contains(key))
                continue; // file wins (do not override)

            if (envVars.ContainsKey(key))
            {
                // Key exists from template but not from files -> allow override
                envVars[key] = val;
            }
            else if (allow.Contains(key))
            {
                // Add new key only if allowlisted
                envVars[key] = val;
            }
        }

        // --- LABELS (normalize deploy.labels -> service.labels) ---
        var labelDict = MergeHelpers.NormalizeLabels(svc.TryGetValue("labels", out var labelsNode2) ? labelsNode2 : null);

        if (svc.TryGetValue("deploy", out var deployNode) && deployNode is IDictionary<string, object?> deployMap &&
            deployMap.TryGetValue("labels", out var depLabelsNode))
        {
            foreach (var kv in MergeHelpers.NormalizeLabels(depLabelsNode))
                labelDict[kv.Key] = kv.Value;
            deployMap.Remove("labels");
        }

        await MergeYamlLabelDirAsync(Path.Combine(root, "stacks", "all", env, "labels"), labelDict, ct);
        await MergeYamlLabelDirAsync(Path.Combine(root, "services", canonicalService, "labels", env), labelDict, ct);

        // >>> Write labels as sequence of "key=value" (template order first)
        if (labelDict.Count > 0)
            svc["labels"] = BuildLabelsSequence(labelDict, templateLabelsOrder);
        else
            svc.Remove("labels");

        // --- DEPLOY ---
        var deploy2 = svc.TryGetValue("deploy", out var d) && d is IDictionary<string, object?> dm ? new Dictionary<string, object?>(dm, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await MergeYamlAreaAsync(Path.Combine(root, "stacks", "all", env, "deploy"), deploy2, ct);
        await MergeYamlAreaAsync(Path.Combine(root, "services", canonicalService, "deploy", env), deploy2, ct);
        if (deploy2.Count > 0) svc["deploy"] = deploy2;

        // --- LOGGING ---
        var logging = svc.TryGetValue("logging", out var lg) && lg is IDictionary<string, object?> lgm ? new Dictionary<string, object?>(lgm, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await MergeYamlAreaAsync(Path.Combine(root, "stacks", "all", env, "logging"), logging, ct);
        await MergeYamlAreaAsync(Path.Combine(root, "services", canonicalService, "logging", env), logging, ct);
        if (logging.Count > 0) svc["logging"] = logging;

        // --- MOUNTS (secrets/configs attachments) ---
        var mounts = svc.TryGetValue("mounts", out var mn) && mn is IDictionary<string, object?> mm ? new Dictionary<string, object?>(mm, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await MergeYamlAreaAsync(Path.Combine(root, "stacks", "all", env, "mounts"), mounts, ct);
        await MergeYamlAreaAsync(Path.Combine(root, "services", canonicalService, "mounts", env), mounts, ct);
        ProjectMountsToService(mounts, svc);

        // ---- Token interpolation vars ----
        var tokenVars = new Dictionary<string, string>(envVars, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in processEnv)
            if (!tokenVars.ContainsKey(kv.Key))
                tokenVars[kv.Key] = kv.Value ?? string.Empty;

        // Special ${ENVVARS}
        tokenVars["ENVVARS"] = BuildEnvVarsInline(tokenVars: envVars);

        // Put env as sequence of "KEY=VALUE" (preserve template order first)
        svc["environment"] = BuildEnvironmentSequence(envVars, templateEnvOrder);

        // Expand tokens across the service map (including environment & labels list items)
        var svcExpanded = ExpandMapTokens(svc, tokenVars);
        return svcExpanded;
    }

    // ---- environment helpers ----

    private static List<string> ExtractEnvKeyOrderFromNode(object? envNode)
    {
        var order = new List<string>();
        if (envNode is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                var s = item?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(s)) continue;
                var idx = s.IndexOf('=');
                var key = idx >= 0 ? s[..idx] : s;
                if (!string.IsNullOrWhiteSpace(key))
                    order.Add(key);
            }
        }
        else if (envNode is IDictionary<string, object?> map)
        {
            foreach (var k in map.Keys)
                if (!string.IsNullOrWhiteSpace(k))
                    order.Add(k);
        }
        return order;
    }

    private static IList<object?> BuildEnvironmentSequence(IDictionary<string, string> envVars, IList<string>? preferredOrder)
    {
        var result = new List<object?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (preferredOrder is not null)
        {
            foreach (var k in preferredOrder)
            {
                if (envVars.TryGetValue(k, out var v))
                {
                    result.Add($"{k}={v}");
                    seen.Add(k);
                }
            }
        }

        foreach (var k in envVars.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(k)) continue;
            result.Add($"{k}={envVars[k]}");
        }

        return result;
    }

    // ---- labels helpers ----

    private static IEnumerable<string> ExtractLabelOrderFromNode(object? labelsNode)
    {
        if (labelsNode is null) yield break;

        if (labelsNode is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                var s = item?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(s)) continue;
                var idx = s.IndexOf('=');
                var key = idx >= 0 ? s[..idx] : s;
                if (!string.IsNullOrWhiteSpace(key))
                    yield return key;
            }
        }
        else if (labelsNode is IDictionary<string, object?> map)
        {
            foreach (var k in map.Keys)
                if (!string.IsNullOrWhiteSpace(k))
                    yield return k;
        }
    }

    private static IList<object?> BuildLabelsSequence(IDictionary<string, string> labels, IList<string>? preferredOrder)
    {
        var result = new List<object?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (preferredOrder is not null)
        {
            foreach (var k in preferredOrder)
            {
                if (labels.TryGetValue(k, out var v))
                {
                    result.Add($"{k}={v}");
                    seen.Add(k);
                }
            }
        }

        foreach (var k in labels.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(k)) continue;
            result.Add($"{k}={labels[k]}");
        }

        return result;
    }

    private static string BuildEnvVarsInline(IDictionary<string, string> tokenVars)
    {
        var parts = tokenVars.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                             .Select(kv => $"{kv.Key}={kv.Value}");
        return string.Join(' ', parts);
    }

    // ---- style hints (healthcheck.test -> flow seq) ----
    private static void ApplyYamlStyleHints(IDictionary<string, object?> root)
    {
        if (!root.TryGetValue("services", out var svcsNode) || svcsNode is not IDictionary<string, object?> services)
            return;

        foreach (var svc in services.Values.OfType<IDictionary<string, object?>>())
        {
            if (!svc.TryGetValue("healthcheck", out var hcNode) || hcNode is not IDictionary<string, object?> hc)
                continue;

            if (hc.TryGetValue("test", out var testNode))
            {
                if (testNode is IEnumerable<object?> seq && testNode is not FlowSeq)
                {
                    hc["test"] = new FlowSeq(seq);
                }
            }
        }
    }

    private async Task<HashSet<string>> LoadAllowListAsync(
        string root,
        string stackId,
        string canonicalService,
        string env,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await ReadAllowListFileAsync(Path.Combine(root, "services", canonicalService, "env", env, "use-envvars.json"), result, ct);
        await ReadAllowListFileAsync(Path.Combine(root, "services", canonicalService, "use-envvars.json"), result, ct);
        await ReadAllowListFileAsync(Path.Combine(root, "stacks", stackId, "use-envvars.json"), result, ct);
        await ReadAllowListFileAsync(Path.Combine(root, "stacks", "all", "use-envvars.json"), result, ct);

        return result;
    }

    private static async Task ReadAllowListFileAsync(string path, ISet<string> acc, CancellationToken ct)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var s = File.OpenRead(path);
            var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var name = el.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) acc.Add(name!);
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var v = prop.Value;
                    var accept = v.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
                        JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                        _ => false
                    };
                    if (accept && !string.IsNullOrWhiteSpace(prop.Name))
                        acc.Add(prop.Name);
                }
            }
        }
        catch
        {
            // ignore malformed file
        }
    }

    private async Task ApplyAppSettingsAsync(
        string root,
        RenderRequest request,
        string canonicalService,
        string env,
        IDictionary<string, string> envVars,
        IDictionary<string, object?> svc,
        ISet<string> fileKeys,
        CancellationToken ct)
    {
        var files = new List<string>();
        var globalEnvDir = Path.Combine(root, "stacks", "all", env, "env");
        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);

        void addIfExists(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "appsettings*.json"))
                files.Add(f);
        }

        addIfExists(globalEnvDir);
        addIfExists(svcEnvDir);

        if (files.Count == 0) return;

        JsonElement merged = default;
        bool initialized = false;

        foreach (var f in files)
        {
            try
            {
                using var s = File.OpenRead(f);
                var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                if (!initialized)
                {
                    merged = doc.RootElement.Clone();
                    initialized = true;
                }
                else
                {
                    merged = MergeHelpers.DeepMergeJson(merged, doc.RootElement);
                }
            }
            catch
            {
                // ignore invalid json
            }
        }

        if (!initialized) return;

        if (request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            var outDir = Path.IsPathRooted(request.OutDir) ? request.OutDir : Path.Combine(root, request.OutDir);
            var cfgDir = Path.Combine(outDir, "configs");
            Directory.CreateDirectory(cfgDir);
            var cfgName = $"{request.StackId}-{canonicalService}-{env}-appsettings.json";
            var cfgPath = Path.Combine(cfgDir, cfgName);

            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cfgPath, json, new UTF8Encoding(false), ct);

            var confNameKey = $"{request.StackId}_{canonicalService}_{env}_appsettings";
            var svcConfigs = svc.TryGetValue("configs", out var cnode) && cnode is IEnumerable<object?> list
                ? new List<object?>(list)
                : new List<object?>();

            svcConfigs.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = confNameKey,
                ["target"] = request.AppSettingsTarget,
                ["mode"] = "0444"
            });

            svc["configs"] = svcConfigs;

            svc["x-sb-top-config"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [confNameKey] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["file"] = Path.GetRelativePath(root, cfgPath)
                }
            };
        }
        else
        {
            var flattened = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeHelpers.FlattenJson(merged, flattened);
            foreach (var kv in flattened)
            {
                envVars[kv.Key] = kv.Value;
                fileKeys.Add(kv.Key); // mark as provided by files
            }
        }
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
        // Bubble up appsettings-generated configs
        if (rendered.TryGetValue("services", out var node) && node is IDictionary<string, object?> services)
        {
            foreach (var svc in services.Values.OfType<IDictionary<string, object?>>())
            {
                if (svc.TryGetValue("x-sb-top-config", out var x) && x is IDictionary<string, object?> m)
                {
                    var dict = rendered.TryGetValue(topKey, out var existing) && existing is IDictionary<string, object?> ex
                        ? ex : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in m)
                        dict[kv.Key] = kv.Value;

                    rendered[topKey] = dict;
                    svc.Remove("x-sb-top-config");
                }
            }
        }

        if (!File.Exists(filePath)) return;
        var map = await _yaml.LoadYamlAsync(filePath, ct);
        if (!map.TryGetValue(topKey, out var n) || n is not IDictionary<string, object?> top) return;

        var target = rendered.TryGetValue(topKey, out var ex2) && ex2 is IDictionary<string, object?> exmap
            ? exmap : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in top)
            target[kv.Key] = kv.Value;

        rendered[topKey] = target;
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

    private async Task MergeJsonEnvDirAsync(string dir, IDictionary<string, string> env, bool skipAppSettings, CancellationToken ct, ISet<string>? fileKeys = null)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var name = Path.GetFileName(file).ToLowerInvariant();
            var isApp = name.StartsWith("appsettings") && name.EndsWith(".json");
            if (isApp && skipAppSettings)
                continue;

            try
            {
                using var s = File.OpenRead(file);
                var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

                var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                MergeHelpers.FlattenJson(doc.RootElement, flat);
                foreach (var kv in flat)
                {
                    env[kv.Key] = kv.Value;
                    fileKeys?.Add(kv.Key); // mark as provided by files
                }
            }
            catch
            {
                // ignore invalid json
            }
        }
    }

    // ---- Process ENV helpers & token expansion ----
    private static Dictionary<string, string> GetProcessEnvironment()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;
            dict[key] = de.Value?.ToString() ?? string.Empty;
        }
        return dict;
    }

    private static string ExpandTokens(string input, IDictionary<string, string> vars)
        => TokenRx.Replace(input, m =>
        {
            var key = m.Groups[1].Value;
            return vars.TryGetValue(key, out var val) ? (val ?? string.Empty) : m.Value;
        });

    private static object? ExpandNodeTokens(object? node, IDictionary<string, string> vars)
    {
        if (node is null) return null;

        switch (node)
        {
            case string s:
                return ExpandTokens(s, vars);
            case IDictionary<string, object?> map:
                return ExpandMapTokens(map, vars);
            case IEnumerable<object?> list:
                var newList = new List<object?>();
                foreach (var item in list)
                    newList.Add(ExpandNodeTokens(item, vars));
                return newList;
            default:
                return node;
        }
    }

    private static IDictionary<string, object?> ExpandMapTokens(IDictionary<string, object?> map, IDictionary<string, string> vars)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            var newKey = ExpandTokens(kv.Key, vars);
            var newVal = ExpandNodeTokens(kv.Value, vars);
            result[newKey] = newVal;
        }
        return result;
    }
}