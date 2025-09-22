// File: SwarmBender/Services/RenderExecutor.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>
/// Renderer v2.0
/// - Generic YAML overlays (no filename prefixes, last-write wins with conflict guard):
///     stacks/all/&lt;env&gt;/*.yml|yaml   (lower)
///     stacks/&lt;stackId&gt;/&lt;env&gt;/*.yml|yaml
///     services/&lt;svc&gt;/&lt;env&gt;/*.yml|yaml (service-scoped)
/// - Keeps special formatting:
///     * healthcheck.test -> flow sequence (["CMD","curl","-f",...])
///     * environment -> list of "KEY=VALUE"
///     * labels -> list of "key=value"
/// - Token interpolation (${VAR}, ${ENVVARS})
/// - Process env allow-list via use-envvars.json (file > process precedence)
/// - Appsettings mode:
///     * env: flattens appsettings.json -> env vars
///     * config: writes merged appsettings to file + attaches as Swarm config
/// - NEW: secrets-map integration:
///     * reads ops/vars/secrets-map.&lt;env&gt;.yml
///     * service.x-sb-secrets -> service.secrets[] + bubble top-level secrets (external: true)
/// </summary>
public sealed class RenderExecutor : IRenderExecutor
{
    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .WithTypeConverter(new FlowSeqYamlConverter()) // keep healthcheck.test flow style
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

        var envs = request.Environments.Select(e => e.ToLowerInvariant()).ToList();
        var outputs = new List<RenderOutput>();

        foreach (var env in envs)
        {
            // start fresh from template per env
            var rendered = new Dictionary<string, object?>(template, StringComparer.OrdinalIgnoreCase);

            // overlays (global -> stack)
            await MergeRootOverlayDirAsync(rendered, Path.Combine(root, "stacks", "all", env), ct);
            await MergeRootOverlayDirAsync(rendered, Path.Combine(stackRoot, env), ct);
            // ADD THESE:
            await MergeRootOverlayDirAsync(rendered, Path.Combine(root, "stacks", "all", env, "stack"), ct);
            await MergeRootOverlayDirAsync(rendered, Path.Combine(stackRoot, env, "stack"), ct);

            if (!rendered.TryGetValue("services", out var servicesNode) ||
                servicesNode is not IDictionary<string, object?> baseServices)
                throw new InvalidOperationException("Template/overlays missing 'services' section.");
            // Apply wildcard overlay under services ("*") to all services, then remove it.
            if (baseServices.TryGetValue("*", out var wildNode) && wildNode is IDictionary<string, object?> wildMap)
            {
                foreach (var svcName in baseServices.Keys.Where(n => !string.Equals(n, "*", StringComparison.Ordinal))
                             .ToList())
                {
                    if (baseServices[svcName] is IDictionary<string, object?> svcMap)
                    {
                        DeepMergeRoot(svcMap, wildMap);
                    }
                    else
                    {
                        var newMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        DeepMergeRoot(newMap, wildMap);
                        baseServices[svcName] = newMap;
                    }
                }

                baseServices.Remove("*");
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


            // secrets-map for this env (may be empty, warn if missing when not quiet)
            var secretsMap = await LoadSecretsMapAsync(root, env, warnIfMissing: !request.Quiet, ct);

            // compose services
            var finalServices = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            var serviceKeys = baseServices.Keys.ToList();
            foreach (var svcKey in serviceKeys)
            {
                var tmplSvcName = svcKey; // name that will appear in output
                var svcMap = baseServices[svcKey] as IDictionary<string, object?>
                             ?? new Dictionary<string, object?>();

                var canonical = aliasMap.TryGetValue(tmplSvcName, out var c) && !string.IsNullOrWhiteSpace(c)
                    ? c
                    : tmplSvcName;

                // NOTE: pass the output service name for SB_SERVICE_NAME interpolation
                var composedService = await ComposeServiceAsync(
                    root, request, canonicalService: canonical, env, svcMap,
                    secretsMap: secretsMap, serviceName: tmplSvcName, ct);

                finalServices[tmplSvcName] = composedService;
            }

            rendered["services"] = finalServices;

            // bubble-up from services, then merge static top-level defs
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "secrets.yml"), "secrets", ct);
            await MergeTopLevelAsync(rendered, Path.Combine(stackRoot, "configs.yml"), "configs", ct);

            // style hints (healthcheck.test -> flow)
            ApplyYamlStyleHints(rendered);

            // write
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
        IReadOnlyDictionary<string, string> secretsMap,
        string serviceName, // <-- output YAML key for this service
        CancellationToken ct)
    {
        // 1) Service-level overlays (highest precedence)
        await MergeServiceOverlayDirAsync(
            Path.Combine(root, "services", canonicalService, env),
            canonicalService, svc, ct);

        // 2) Capture current environment & labels order AFTER overlays
        var templateEnvOrder =
            ExtractEnvKeyOrderFromNode(svc.TryGetValue("environment", out var rawEnvNode) ? rawEnvNode : null);

// --- Non-destructive labels capture (keep originals if we can't normalize) ---

// Service-level labels: keep original node so we can restore if normalization fails
        object? rawSvcLabels = null;
        var serviceLabelsOrder = new List<string>();
        if (svc.TryGetValue("labels", out var tmpRawSvcLabels))
        {
            rawSvcLabels = tmpRawSvcLabels;
            serviceLabelsOrder.AddRange(ExtractLabelOrderFromNode(rawSvcLabels));
        }

// Deploy-level labels: capture map + original node + order
        IDictionary<string, object?>? deployMap = null;
        object? rawDepLabels = null;
        var deployLabelsOrder = new List<string>();
        if (svc.TryGetValue("deploy", out var deployNode) && deployNode is IDictionary<string, object?> dm)
        {
            deployMap = dm;
            if (dm.TryGetValue("labels", out var tmpRawDepLabels))
            {
                rawDepLabels = tmpRawDepLabels;
                deployLabelsOrder.AddRange(ExtractLabelOrderFromNode(rawDepLabels));
            }
        }

        // 3) Track keys from files (JSON/appsettings) -> file wins over process env
        var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 4) ENVIRONMENT gather (template -> global -> stack -> service)
        var envVars = MergeHelpers.NormalizeEnvironment(
            svc.TryGetValue("environment", out var envNode) ? envNode : null);

        var globalEnvDir = Path.Combine(root, "stacks", "all", env, "env");
        await MergeJsonEnvDirAsync(globalEnvDir, envVars,
            skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct,
            fileKeys);

        var stackEnvDir = Path.Combine(root, "stacks", request.StackId!, env, "env");
        await MergeJsonEnvDirAsync(stackEnvDir, envVars,
            skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct,
            fileKeys);

        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);
        await MergeJsonEnvDirAsync(svcEnvDir, envVars,
            skipAppSettings: request.AppSettingsMode.Equals("config", StringComparison.OrdinalIgnoreCase), ct,
            fileKeys);

        // 5) appsettings.*.json => env or config
        await ApplyAppSettingsAsync(root, request, canonicalService, env, envVars, svc, fileKeys, ct);

        // 6) Process environment overlay with allowlist
        var allow = await LoadAllowListAsync(root, request.StackId!, canonicalService, env, ct);
        var processEnv = GetProcessEnvironment();
        foreach (var kv in processEnv)
        {
            var key = kv.Key;
            var val = kv.Value ?? string.Empty;

            if (fileKeys.Contains(key))
                continue; // file wins

            if (envVars.ContainsKey(key))
                envVars[key] = val;
            else if (allow.Contains(key))
                envVars[key] = val;
        }

        // 7) Labels: non-destructive normalization and no cross-merge

// Service-level labels
        if (rawSvcLabels is not null)
        {
            // Try to normalize to "key=value" pairs; if it yields nothing, keep the original YAML node.
            var svcDict = MergeHelpers.NormalizeLabels(rawSvcLabels);
            if (svcDict.Count > 0)
                svc["labels"] = BuildLabelsSequence(svcDict, serviceLabelsOrder);
            else
                svc["labels"] = rawSvcLabels; // keep original (e.g., complex strings like backticks)
        }
        else
        {
            // No service-level labels at all
            svc.Remove("labels");
        }

// Deploy-level labels
        if (deployMap is not null)
        {
            if (rawDepLabels is not null)
            {
                var depDict = MergeHelpers.NormalizeLabels(rawDepLabels);
                if (depDict.Count > 0)
                    deployMap["labels"] = BuildLabelsSequence(depDict, deployLabelsOrder);
                else
                    deployMap["labels"] = rawDepLabels; // keep original node
            }
            else
            {
                deployMap.Remove("labels");
            }
        }

        // 8) Attach secrets from secrets-map via x-sb-secrets
        AttachSecretsFromMap(svc, secretsMap);

        // 9) Token variables for interpolation (includes reserved SB_* tokens)
        var tokenVars = new Dictionary<string, string>(envVars, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in processEnv)
            if (!tokenVars.ContainsKey(kv.Key))
                tokenVars[kv.Key] = kv.Value ?? string.Empty;

        // Reserved tokens
        tokenVars["SB_STACK_ID"] = request.StackId ?? string.Empty;
        tokenVars["SB_ENV"] = env;
        tokenVars["SB_SERVICE_NAME"] = serviceName; // <- output key under services
        tokenVars["ENVVARS"] = BuildEnvVarsInline(envVars); // convenience for “inline” usage

        // 10) Put env as sequence of "KEY=VALUE" (preserve order)
        svc["environment"] = BuildEnvironmentSequence(envVars, templateEnvOrder);

        // 11) Expand tokens across entire service map (keys & values, including label list items)
        var svcExpanded = ExpandMapTokens(svc, tokenVars);
        return svcExpanded;
    }

    // ---------- Overlays helpers ----------

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
                if (!ObjectsEqual(listA[i], listB[i]))
                    return false;
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

            if (map.TryGetValue("services", out var sNode) && sNode is IDictionary<string, object?> services &&
                services.TryGetValue(canonicalService, out var svcNode) &&
                svcNode is IDictionary<string, object?> svcMap)
            {
                MergeMapNoConflict(svc, svcMap, prefix: "", conflicts);
            }
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

    private static IList<object?> BuildEnvironmentSequence(IDictionary<string, string> envVars,
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

    private async Task MergeTopLevelAsync(IDictionary<string, object?> rendered, string filePath, string topKey,
        CancellationToken ct)
    {
        // bubble up x-sb-top-config -> configs
        BubbleUpTopFromServices(rendered, serviceKey: "x-sb-top-config", targetTopKey: "configs");
        // bubble up x-sb-top-secrets -> secrets
        BubbleUpTopFromServices(rendered, serviceKey: "x-sb-top-secrets", targetTopKey: "secrets");

        // merge static top-level defs from stack file (secrets.yml / configs.yml)
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

    private static void BubbleUpTopFromServices(IDictionary<string, object?> rendered, string serviceKey,
        string targetTopKey)
    {
        if (!rendered.TryGetValue("services", out var node) || node is not IDictionary<string, object?> services)
            return;

        var aggregate = rendered.TryGetValue(targetTopKey, out var existing) &&
                        existing is IDictionary<string, object?> ex
            ? ex
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var svc in services.Values.OfType<IDictionary<string, object?>>())
        {
            if (svc.TryGetValue(serviceKey, out var x) && x is IDictionary<string, object?> m)
            {
                foreach (var kv in m)
                    aggregate[kv.Key] = kv.Value;
                svc.Remove(serviceKey);
            }
        }

        if (aggregate.Count > 0)
            rendered[targetTopKey] = aggregate;
    }

    // ---------- JSON/env helpers ----------
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
            var name = Path.GetFileName(file);

            // ❗ allowlist dosyalarını env kaynağı olarak KULLANMAYALIM
            if (name.Equals("use-envvars.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var lname = name.ToLowerInvariant();
            var isApp = lname.StartsWith("appsettings") && lname.EndsWith(".json");
            if (isApp && skipAppSettings)
                continue;

            try
            {
                using var s = File.OpenRead(file);
                var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

                // Yalnızca 'object' kökleri flatten edelim. (array/root → atla)
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                MergeHelpers.FlattenJson(doc.RootElement, flat);

                foreach (var kv in flat)
                {
                    // boş anahtarları da filtrele
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;

                    env[kv.Key] = kv.Value;
                    fileKeys?.Add(kv.Key); // file kaynağından geldiğini işaretle
                }
            }
            catch
            {
                // invalid json'u sessizce yoksay
            }
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
        var stackEnvDir = Path.Combine(root, "stacks", request.StackId!, env, "env");
        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);

        void addIfExists(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "appsettings*.json"))
                files.Add(f);
        }

        addIfExists(globalEnvDir);
        addIfExists(stackEnvDir);
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

        await ReadAllowListFileAsync(Path.Combine(root, "services", canonicalService, "env", env, "use-envvars.json"),
            result, ct);
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
                        JsonValueKind.False => false,
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
            // ignore malformed
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

        return node switch
        {
            string s => ExpandTokens(s, vars),
            IDictionary<string, object?> map => ExpandMapTokens(map, vars),
            IEnumerable<object?> list => list.Select(item => ExpandNodeTokens(item, vars)).ToList(),
            _ => node
        };
    }

    private static IDictionary<string, object?> ExpandMapTokens(IDictionary<string, object?> map,
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

    // ---------- secrets-map integration ----------

    private async Task<Dictionary<string, string>> LoadSecretsMapAsync(
        string root,
        string env,
        bool warnIfMissing,
        CancellationToken ct)
    {
        var mapPath = Path.Combine(root, "ops", "vars", $"secrets-map.{env}.yml");

        if (!File.Exists(mapPath))
        {
            if (warnIfMissing)
                AnsiConsole.MarkupLine(
                    $"[yellow]WARN:[/] secrets map not found for env [bold]{env}[/] at: {mapPath} — continuing without service secrets.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var map = await _yaml.LoadYamlAsync(mapPath, ct);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in map)
        {
            var val = kv.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(val))
                dict[kv.Key] = val!;
        }

        return dict;
    }

    private static void AttachSecretsFromMap(
        IDictionary<string, object?> svc,
        IReadOnlyDictionary<string, string> secretsMap)
    {
        if (!svc.TryGetValue("x-sb-secrets", out var node) || node is null)
            return;

        // normalize existing service-level secrets to a list
        var svcSecrets = new List<object?>();
        if (svc.TryGetValue("secrets", out var existing))
        {
            if (existing is IEnumerable<object?> list)
            {
                svcSecrets.AddRange(list);
            }
            else if (existing is IDictionary<string, object?> dictLike)
            {
                foreach (var k in dictLike.Keys)
                    svcSecrets.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = k
                    });
            }
        }

        // collect / bubble for top-level 'secrets' (external: true)
        var topBubble = svc.TryGetValue("x-sb-top-secrets", out var bubble) && bubble is IDictionary<string, object?> b
            ? b
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // local helper: stable signature for dedup
        static string SecretSig(IDictionary<string, object?> m)
        {
            string src = m.TryGetValue("source", out var sv) ? sv?.ToString() ?? "" : "";
            string tgt = m.TryGetValue("target", out var tv) ? tv?.ToString() ?? "" : "";
            string mode = m.TryGetValue("mode", out var mv) ? mv?.ToString() ?? "" : "";
            string uid = m.TryGetValue("uid", out var uv) ? uv?.ToString() ?? "" : "";
            string gid = m.TryGetValue("gid", out var gv) ? gv?.ToString() ?? "" : "";
            return $"{src}|{tgt}|{mode}|{uid}|{gid}";
        }

        if (node is IEnumerable<object?> reqList)
        {
            foreach (var item in reqList)
            {
                string? key = null;
                string? target = null;
                object? mode = null; // string or int acceptable
                object? uid = null; // string or int acceptable
                object? gid = null; // string or int acceptable

                if (item is string s)
                {
                    key = s;
                }
                else if (item is IDictionary<string, object?> m)
                {
                    if (m.TryGetValue("key", out var k) && k is not null)
                        key = k.ToString();

                    if (m.TryGetValue("target", out var t) && t is not null)
                        target = t.ToString();

                    if (m.TryGetValue("mode", out var md) && md is not null)
                        mode = md;

                    if (m.TryGetValue("uid", out var u) && u is not null)
                        uid = u;

                    if (m.TryGetValue("gid", out var g) && g is not null)
                        gid = g;
                }

                if (string.IsNullOrWhiteSpace(key))
                    continue;

                // map'te karşılığı yoksa atla
                if (!secretsMap.TryGetValue(key!, out var engineName) || string.IsNullOrWhiteSpace(engineName))
                    continue;

                var entry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = engineName
                };
                if (!string.IsNullOrWhiteSpace(target)) entry["target"] = target;
                if (mode is not null) entry["mode"] = mode;
                if (uid is not null) entry["uid"] = uid;
                if (gid is not null) entry["gid"] = gid;

                var sig = SecretSig(entry);
                var already = svcSecrets
                    .OfType<IDictionary<string, object?>>()
                    .Any(e => SecretSig(e) == sig);
                if (!already)
                    svcSecrets.Add(entry);

                // bubble external top-level declaration
                if (!topBubble.ContainsKey(engineName))
                {
                    topBubble[engineName] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["external"] = true
                    };
                }
            }
        }

        if (svcSecrets.Count > 0)
            svc["secrets"] = svcSecrets;
        else
            svc.Remove("secrets");

        if (topBubble.Count > 0)
            svc["x-sb-top-secrets"] = topBubble;

        // remove directive from final YAML
        svc.Remove("x-sb-secrets");
    }

    private static void DeepMergeRoot(
        IDictionary<string, object?> target,
        IDictionary<string, object?> source)
    {
        foreach (var (k, v) in source)
        {
            // Case 1: map vs map -> recursive merge
            if (v is IDictionary<string, object?> sMap &&
                target.TryGetValue(k, out var tVal) &&
                tVal is IDictionary<string, object?> tMap)
            {
                DeepMergeRoot(tMap, sMap);
                continue;
            }

            // Case 2: list merge special-case for "labels"
            if (string.Equals(k, "labels", StringComparison.OrdinalIgnoreCase) &&
                v is IEnumerable<object?> sList)
            {
                if (target.TryGetValue(k, out var tListObj) &&
                    tListObj is IEnumerable<object?> tList)
                {
                    // Union: keep original order from template, append new ones from overlay if not present
                    var result = new List<object?>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var it in tList)
                    {
                        result.Add(it);
                        if (it is string ts) seen.Add(ts);
                    }

                    foreach (var it in sList)
                    {
                        if (it is string ss)
                        {
                            if (!seen.Contains(ss))
                            {
                                result.Add(ss);
                                seen.Add(ss);
                            }
                        }
                        else
                        {
                            // Non-string item: append as-is (rare but be safe)
                            result.Add(it);
                        }
                    }

                    target[k] = result;
                }
                else
                {
                    // No existing list -> just take overlay list
                    target[k] = sList.ToList();
                }

                continue;
            }

            // Default behavior:
            //  - map vs non-map => replace
            //  - list (non-label) vs anything => replace
            //  - scalar vs anything => replace
            target[k] = v;
        }
    }
}