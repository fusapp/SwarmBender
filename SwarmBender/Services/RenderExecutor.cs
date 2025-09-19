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
/// Renderer v1.9
/// - Root overlays (env scoped):
///     stacks/all/<env>/*.yml|yaml      (lowest)
///     stacks/<stackId>/<env>/*.yml|yaml
/// - Stack overlays with wildcard:
///     stacks/all/<env>/stack/*.yml|yaml
///     stacks/<stackId>/<env>/stack/*.yml|yaml   (highest; last wins)
/// - Keeps special formatting:
///     * healthcheck.test -> flow sequence
///     * environment -> list of "KEY=VALUE"
///     * labels -> list of "key=value"
/// - Token interpolation (${VAR}, ${ENVVARS})
/// - Process env allowlist via use-envvars.json (file > process precedence)
/// </summary>
public sealed class RenderExecutor : IRenderExecutor
{
    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .WithTypeConverter(new FlowSeqYamlConverter()) // keep healthcheck.test as flow array
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

        // Load template into a mutable map
        var template = await _yaml.LoadYamlAsync(templatePath, ct);
        var envs = request.Environments.Select(e => e.ToLowerInvariant()).ToList();
        var outputs = new List<RenderOutput>();

        foreach (var env in envs)
        {
            // Start from template for each env
            IDictionary<string, object?> rendered =
                new Dictionary<string, object?>(template, StringComparer.OrdinalIgnoreCase);


            // 1) ROOT-LEVEL overlays (no wildcard semantics; conflict-checked deep-merge)
            await MergeRootOverlayDirAsync(rendered, Path.Combine(root, "stacks", "all", env), ct);
            await MergeRootOverlayDirAsync(rendered, Path.Combine(stackRoot, env), ct);

            // 2) STACK overlays with wildcard support (services: "*" + per-service)
            rendered = await ApplyOverlaysAsync(root, request.StackId!, env, rendered, request.Quiet, ct);

            if (!rendered.TryGetValue("services", out var servicesNode) ||
                servicesNode is not IDictionary<string, object?> baseServices ||
                baseServices.Count == 0)
            {
                throw new InvalidOperationException("Template/overlays missing 'services' section.");
            }

            // aliases (optional)
            var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var aliasesPath = Path.Combine(stackRoot, "aliases.yml");
            if (File.Exists(aliasesPath))
            {
                var aliases = await _yaml.LoadYamlAsync(aliasesPath, ct);
                foreach (var kv in aliases)
                    aliasMap[kv.Key] = kv.Value?.ToString() ?? kv.Key;
            }

            var finalServices = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // Iterate services after all overlays (includes any added services)
            var serviceKeys = baseServices.Keys.ToList();
            foreach (var svcKey in serviceKeys)
            {
                var tmplSvcName = svcKey;
                var svcMap = baseServices[svcKey] as IDictionary<string, object?> ?? new Dictionary<string, object?>();

                var canonical = aliasMap.TryGetValue(tmplSvcName, out var c) && !string.IsNullOrWhiteSpace(c)
                    ? c
                    : tmplSvcName;

                var composedService = await ComposeServiceAsync(root, request, canonical, env, svcMap, ct);
                finalServices[tmplSvcName] = composedService;
            }

            rendered["services"] = finalServices;

            // Bubble up appsettings-generated configs, then merge static top-level defs
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "secrets.yml"), "secrets", ct);
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "configs.yml"), "configs", ct);

            // Style hints (healthcheck.test -> flow sequence)
            ApplyYamlStyleHints(rendered);

            var yaml = YamlWriter.Serialize(rendered);

            var outDir = Path.IsPathRooted(request.OutDir) ? request.OutDir : Path.Combine(root, request.OutDir);
            var outFile = Path.Combine(outDir, $"{request.StackId}-{env}.stack.yml");

            if (!request.DryRun)
            {
                Directory.CreateDirectory(outDir);
                await File.WriteAllTextAsync(outFile, yaml, new UTF8Encoding(false), ct);

                if (request.WriteHistory)
                {
                    var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var hist = Path.Combine(root, "ops", "state", "history", stamp);
                    Directory.CreateDirectory(hist);
                    var histFile = Path.Combine(hist, $"{request.StackId}-{env}.stack.yml");
                    await File.WriteAllTextAsync(histFile, yaml, new UTF8Encoding(false), ct);
                }
            }

            if (!request.Quiet && request.Preview)
            {
                AnsiConsole.WriteLine($"# ===== Render Preview: {request.StackId} [{env}] =====");
                AnsiConsole.WriteLine(yaml);
            }

            outputs.Add(new RenderOutput(env, outFile));
        }

        return new RenderResult(outputs);
    }

    private async Task<IDictionary<string, object?>> ComposeServiceAsync(
        string root,
        RenderRequest request,
        string canonicalService,
        string env,
        IDictionary<string, object?> svc,
        CancellationToken ct)
    {
        // 3) SERVICE-LEVEL overlays (highest precedence, per service)
        await MergeServiceOverlayDirAsync(Path.Combine(root, "services", canonicalService, env), canonicalService, svc, ct);

        // Capture environment & labels order AFTER overlays
        var templateEnvOrder =
            ExtractEnvKeyOrderFromNode(svc.TryGetValue("environment", out var rawEnvNode) ? rawEnvNode : null);

        var templateLabelsOrder = new List<string>();
        var rawLabelsNode0 = svc.TryGetValue("labels", out var rawLabelsNodeA) ? rawLabelsNodeA : null;
        templateLabelsOrder.AddRange(ExtractLabelOrderFromNode(rawLabelsNode0));

        if (svc.TryGetValue("deploy", out var deployNode0) && deployNode0 is IDictionary<string, object?> dep0 &&
            dep0.TryGetValue("labels", out var depLabelsNode0))
        {
            foreach (var k in ExtractLabelOrderFromNode(depLabelsNode0))
                if (!templateLabelsOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                    templateLabelsOrder.Add(k);
        }

        // Track keys from files (JSON/appsettings) -> file wins over process env
        var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --- ENVIRONMENT (build known keys from current 'environment' + files) ---
        var envVars = MergeHelpers.NormalizeEnvironment(
            svc.TryGetValue("environment", out var initialEnvNode) ? initialEnvNode : null);

        // Merge from global/service JSON (skip appsettings when mode=config)
        var globalEnvDir = Path.Combine(root, "stacks", "all", env, "env");
        await MergeJsonEnvDirAsync(globalEnvDir, envVars,
            skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct,
            fileKeys);

        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);
        await MergeJsonEnvDirAsync(svcEnvDir, envVars,
            skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct,
            fileKeys);

        // appsettings: may add/override env vars (env mode) OR produce config (config mode)
        await ApplyAppSettingsAsync(root, request, canonicalService, env, envVars, svc, fileKeys, ct);

        // --- Process env overlay with allowlist ---
        var allow = await LoadAllowListAsync(root, request.StackId!, canonicalService, env, ct);
        var processEnv = GetProcessEnvironment();

        foreach (var kv in processEnv)
        {
            var key = kv.Key;
            var val = kv.Value ?? string.Empty;

            if (fileKeys.Contains(key))
                continue; // file wins

            if (envVars.ContainsKey(key))
            {
                envVars[key] = val; // override template/overlay value
            }
            else if (allow.Contains(key))
            {
                envVars[key] = val; // allow add
            }
        }

        // --- LABELS (normalize deploy.labels -> service.labels) ---
        var labelDict =
            MergeHelpers.NormalizeLabels(svc.TryGetValue("labels", out var labelsNode2) ? labelsNode2 : null);

        if (svc.TryGetValue("deploy", out var deployNode) && deployNode is IDictionary<string, object?> deployMap &&
            deployMap.TryGetValue("labels", out var depLabelsNode))
        {
            foreach (var kv in MergeHelpers.NormalizeLabels(depLabelsNode))
                labelDict[kv.Key] = kv.Value;
            deployMap.Remove("labels");
        }

        // Write labels as sequence of "key=value" (template order first)
        if (labelDict.Count > 0)
            svc["labels"] = BuildLabelsSequence(labelDict, templateLabelsOrder);
        else
            svc.Remove("labels");

        // ---- Token interpolation vars ----
        var tokenVars = new Dictionary<string, string>(envVars, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in processEnv)
            if (!tokenVars.ContainsKey(kv.Key))
                tokenVars[kv.Key] = kv.Value ?? string.Empty;

        // Special ${ENVVARS}
        tokenVars["ENVVARS"] = BuildEnvVarsInline(tokenVars: envVars);

        // Put env as sequence of "KEY=VALUE" (preserve order from template/overlays first)
        svc["environment"] = BuildEnvironmentSequence(envVars, templateEnvOrder);

        // Expand tokens across the service map (including environment & labels list items)
        var svcExpanded = ExpandMapTokens(svc, tokenVars);
        return svcExpanded;
    }

    // ---------- Overlays helpers ----------

    /// <summary>
    /// Deep-merge without filename-based precedence. If the same scalar path is set
    /// to different values across multiple files in the SAME directory, record a conflict.
    /// Maps are merged recursively; list vs scalar or map vs scalar also counts as conflict.
    /// </summary>
    private static void MergeMapNoConflict(
        IDictionary<string, object?> target,
        IDictionary<string, object?> src,
        string prefix,
        IList<string> conflicts)
    {
        foreach (var kv in src)
        {
            var key = kv.Key;
            var path = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            var srcVal = kv.Value;

            if (srcVal is IDictionary<string, object?> srcMap)
            {
                if (!target.TryGetValue(key, out var tgtVal) || tgtVal is not IDictionary<string, object?> tgtMap)
                {
                    tgtMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    target[key] = tgtMap;
                }

                if (target[key] is IDictionary<string, object?> tgtMap2)
                {
                    MergeMapNoConflict(tgtMap2, srcMap, path, conflicts);
                }
                else
                {
                    conflicts.Add($"{path} (type mismatch)");
                }
            }
            else
            {
                if (target.TryGetValue(key, out var existing))
                {
                    if (existing is IDictionary<string, object?>)
                    {
                        conflicts.Add($"{path} (type mismatch)");
                    }
                    else if (!ObjectsEqual(existing, srcVal))
                    {
                        conflicts.Add($"{path}");
                    }
                }
                else
                {
                    target[key] = srcVal;
                }
            }
        }
    }

    private static bool ObjectsEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is bool ba && b is bool bb2) return ba == bb2;
        if (a is int ia && b is int ib) return ia == ib;
        if (a is long la && b is long lb) return la == lb;
        if (a is double da && b is double db) return da.Equals(db);

        if (a is IEnumerable<object?> ea && b is IEnumerable<object?> eb)
        {
            var listA = ea.ToList();
            var listB = eb.ToList();
            if (listA.Count != listB.Count) return false;
            for (int i = 0; i < listA.Count; i++)
                if (!ObjectsEqual(listA[i], listB[i])) return false;
            return true;
        }

        return a.Equals(b);
    }

    private async Task MergeRootOverlayDirAsync(IDictionary<string, object?> target, string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;

        var conflicts = new List<string>();
        foreach (var file in Directory.GetFiles(dir).Where(f =>
                     f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            var map = await _yaml.LoadYamlAsync(file, ct);
            if (map is null || map.Count == 0) continue;
            MergeMapNoConflict(target, map, prefix: "", conflicts);
        }

        if (conflicts.Count > 0)
        {
            var msg = $"Overlay conflict(s) detected under '{dir}':\n - " +
                      string.Join("\n - ", conflicts.Distinct(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(msg);
        }
    }

    private async Task MergeServiceOverlayDirAsync(string dir, string canonicalService,
        IDictionary<string, object?> svc, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;

        var conflicts = new List<string>();

        foreach (var file in Directory.GetFiles(dir).Where(f =>
                     f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            var map = await _yaml.LoadYamlAsync(file, ct);
            if (map is null || map.Count == 0) continue;

            // Case 1: services:<svc>: subtree
            if (map.TryGetValue("services", out var sNode) && sNode is IDictionary<string, object?> services &&
                services.TryGetValue(canonicalService, out var svcNode) &&
                svcNode is IDictionary<string, object?> svcMap)
            {
                MergeMapNoConflict(svc, svcMap, prefix: "", conflicts);
            }
            // Case 2: direct fragment (deploy:, logging:, labels:, healthcheck:, etc.)
            else
            {
                MergeMapNoConflict(svc, map, prefix: "", conflicts);
            }
        }

        if (conflicts.Count > 0)
        {
            var msg = $"Overlay conflict(s) detected under '{dir}' for service '{canonicalService}':\n - " +
                      string.Join("\n - ", conflicts.Distinct(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(msg);
        }
    }

    // ---------- Stack overlays with wildcard support ----------

    private async Task<IDictionary<string, object?>> ApplyOverlaysAsync(
        string root,
        string stackId,
        string env,
        IDictionary<string, object?> baseDoc,
        bool quiet,
        CancellationToken ct)
    {
        var result = new Dictionary<string, object?>(baseDoc, StringComparer.OrdinalIgnoreCase);

        async Task ApplyDirectoryAsync(string dir)
        {
            if (!Directory.Exists(dir)) return;

            var files = Directory.EnumerateFiles(dir, "*.yml")
                .Concat(Directory.EnumerateFiles(dir, "*.yaml"))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in files)
            {
                var overlay = await _yaml.LoadYamlAsync(file, ct);
                if (overlay is null || overlay.Count == 0)
                    continue;

                // Merge non-services root keys directly
                if (overlay.TryGetValue("services", out _))
                {
                    var nonServices = overlay
                        .Where(kv => !string.Equals(kv.Key, "services", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    DeepMergeRoot(result, nonServices);
                }
                else
                {
                    DeepMergeRoot(result, overlay);
                }

                // Merge services with wildcard support
                if (overlay.TryGetValue("services", out var servicesNode) &&
                    servicesNode is IDictionary<string, object?> ovServices)
                {
                    if (!result.TryGetValue("services", out var baseServicesNode) ||
                        baseServicesNode is not IDictionary<string, object?> baseServices)
                    {
                        baseServices = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        result["services"] = baseServices;
                    }

                    ApplyServicesOverlay(baseServices, ovServices);
                }

                if (!quiet)
                    AnsiConsole.MarkupLine($"[grey]overlay applied:[/] {file}");
            }
        }

        // global stack overlays first, then stack-specific (last wins)
        await ApplyDirectoryAsync(Path.Combine(root, "stacks", "all", env, "stack"));
        await ApplyDirectoryAsync(Path.Combine(root, "stacks", stackId, env, "stack"));

        return result;
    }

    private static void DeepMergeRoot(
        IDictionary<string, object?> target,
        IDictionary<string, object?> source)
    {
        foreach (var (k, v) in source)
        {
            if (v is IDictionary<string, object?> sMap &&
                target.TryGetValue(k, out var tVal) &&
                tVal is IDictionary<string, object?> tMap)
            {
                DeepMergeRoot(tMap, sMap);
            }
            else
            {
                target[k] = v; // lists/scalars: replace
            }
        }
    }

    private static void ApplyServicesOverlay(
        IDictionary<string, object?> baseServices,
        IDictionary<string, object?> overlayServices)
    {
        // 1) wildcard first
        if (overlayServices.TryGetValue("*", out var wild) &&
            wild is IDictionary<string, object?> wildMap)
        {
            foreach (var svcName in baseServices.Keys.ToList())
            {
                var baseMap = baseServices[svcName] as IDictionary<string, object?>
                              ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                DeepMergeRoot(baseMap, wildMap);
                baseServices[svcName] = baseMap;
            }
        }

        // 2) explicit services
        foreach (var kv in overlayServices)
        {
            if (kv.Key == "*") continue;

            var ovMap = kv.Value as IDictionary<string, object?>
                        ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!baseServices.TryGetValue(kv.Key, out var baseNode) ||
                baseNode is not IDictionary<string, object?> baseMap)
            {
                baseMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                baseServices[kv.Key] = baseMap;
            }

            DeepMergeRoot(baseMap, ovMap);
        }
    }

    // ---------- environment helpers ----------

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

    private static IList<object?> BuildEnvironmentSequence(
        IDictionary<string, string> envVars,
        IList<string>? preferredOrder)
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

    // ---------- labels helpers ----------

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

    private static IList<object?> BuildLabelsSequence(
        IDictionary<string, string> labels,
        IList<string>? preferredOrder)
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

    // ---------- style hints (healthcheck.test -> flow seq) ----------

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

    // ---------- top-level helpers ----------

    private async Task MergeTopLevelAsync(
        IDictionary<string, object?> rendered,
        string filePath,
        string topKey,
        CancellationToken ct)
    {
        // bubble up appsettings-generated configs (from services)
        if (rendered.TryGetValue("services", out var node) && node is IDictionary<string, object?> services)
        {
            foreach (var svc in services.Values.OfType<IDictionary<string, object?>>())
            {
                if (svc.TryGetValue("x-sb-top-config", out var x) && x is IDictionary<string, object?> m)
                {
                    var dict = rendered.TryGetValue(topKey, out var existing) &&
                               existing is IDictionary<string, object?> ex
                        ? ex
                        : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

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
            ? exmap
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in top)
            target[kv.Key] = kv.Value;

        rendered[topKey] = target;
    }

    private async Task MergeJsonEnvDirAsync(
        string dir,
        IDictionary<string, string> env,
        bool skipAppSettings,
        CancellationToken ct,
        ISet<string>? fileKeys = null)
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

    // ---------- token expansion ----------

    private static Dictionary<string, string> GetProcessEnvironment()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;
            dict[key!] = de.Value?.ToString() ?? string.Empty;
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

        return node switch
        {
            string s => ExpandTokens(s, vars),
            IDictionary<string, object?> map => ExpandMapTokens(map, vars),
            IEnumerable<object?> list => list.Select(item => ExpandNodeTokens(item, vars)).ToList(),
            _ => node
        };
    }

    private static IDictionary<string, object?> ExpandMapTokens(
        IDictionary<string, object?> map,
        IDictionary<string, string> vars)
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

    private async Task<HashSet<string>> LoadAllowListAsync(
        string root,
        string stackId,
        string canonicalService,
        string env,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Highest specificity first
        await ReadAllowListFileAsync(Path.Combine(root, "services", canonicalService, "env", env, "use-envvars.json"), result, ct);
        await ReadAllowListFileAsync(Path.Combine(root, "services", canonicalService, "use-envvars.json"), result, ct);
        await ReadAllowListFileAsync(Path.Combine(root, "stacks", stackId, "use-envvars.json"), result, ct);
        await ReadAllowListFileAsync(Path.Combine(root, "stacks", "all", env, "env", "use-envvars.json"), result, ct); // env-scoped global allowlist

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
            // ignore malformed allowlist
        }
    }
}