// File: SwarmBender/Services/Validator.cs

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>
/// Validates stacks against policies and basic schema (overlay-aware).
/// - Runs a dry-run render per environment to catch overlay conflicts/missing pieces
/// - Keeps image/labels/guardrails and secrets/configs shape checks
/// - Validates appsettings*.json (parse) and required env keys
/// </summary>
public sealed class Validator : IValidator
{
    private readonly IYamlLoader _yaml;
    private readonly IRenderExecutor _renderer;

    public Validator(IYamlLoader yaml, IRenderExecutor renderer)
        => (_yaml, _renderer) = (yaml, renderer);

    public async Task<ValidateResult> ValidateAsync(ValidateRequest request, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(request.RootPath);
        return await ValidateInternalAsync(root, request, ct);
    }

    private async Task<ValidateResult> ValidateInternalAsync(string root, ValidateRequest request, CancellationToken ct)
    {
        var stacksRoot = Path.Combine(root, "stacks");
        if (!Directory.Exists(stacksRoot))
            throw new DirectoryNotFoundException($"stacks folder not found at: {stacksRoot}");

        var stacks = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.StackId))
            stacks.Add(request.StackId!);
        else
        {
            foreach (var dir in Directory.GetDirectories(stacksRoot))
            {
                var name = Path.GetFileName(dir);
                if (!string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
                    stacks.Add(name);
            }
        }

        var envs = await ResolveEnvironmentsAsync(root, request.Environments, ct);

        var results = new List<StackValidationResult>();
        foreach (var stackId in stacks)
        {
            var res = await ValidateSingleStackAsync(root, stackId, envs, request, ct);
            results.Add(res);
            await WriteReportAsync(root, res, ct);
        }

        if (!string.IsNullOrWhiteSpace(request.OutFile))
        {
            var outPath = Path.GetFullPath(Path.Combine(root, request.OutFile!));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var json = JsonSerializer.Serialize(new ValidateResult(results),
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(outPath, json, ct);
        }

        return new ValidateResult(results);
    }

    private async Task<List<string>> ResolveEnvironmentsAsync(string root, IEnumerable<string> requested,
        CancellationToken ct)
    {
        var envs = new List<string>();
        foreach (var token in requested.SelectMany(e =>
                     e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            envs.Add(token.ToLowerInvariant());

        if (envs.Count > 0)
            return envs.Distinct().ToList();

        var allDir = Path.Combine(root, "stacks", "all");
        if (!Directory.Exists(allDir))
            return new List<string>();

        foreach (var envDir in Directory.GetDirectories(allDir))
            envs.Add(Path.GetFileName(envDir).ToLowerInvariant());

        return envs.Distinct().ToList();
    }

    private async Task<StackValidationResult> ValidateSingleStackAsync(string root, string stackId, List<string> envs,
        ValidateRequest request, CancellationToken ct)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        // --- Policies ---
        var composePolicy = await _yaml.LoadYamlAsync(Path.Combine(root, "ops", "checks", "compose-v3.yml"), ct);
        var allows = GetStringList(composePolicy, "allow") ?? new List<string> { "version", "services" };
        var forbids = GetStringList(composePolicy, "forbid") ?? new List<string> { "build", "depends_on" };

        var labelsPolicy = await _yaml.LoadYamlAsync(Path.Combine(root, "ops", "policies", "labels.yml"), ct);
        var requiredLabels = GetStringList(labelsPolicy, "required") ?? new List<string>();
        var reservedLabels = GetStringList(labelsPolicy, "reserved") ?? new List<string>();

        var imagesPolicy = await _yaml.LoadYamlAsync(Path.Combine(root, "ops", "policies", "images.yml"), ct);
        var forbidLatest = GetBool(imagesPolicy, "forbid_latest", defaultValue: true);
        var requireTagOrDigest = GetBool(imagesPolicy, "require_tag_or_digest", defaultValue: true);
        var allowRegexes = GetStringList(imagesPolicy, "allow") ?? new List<string>();
        var allowTagless = GetBool(imagesPolicy, "allow_tagless", defaultValue: false);
        var allowMatchers = allowRegexes.Select(rx => new Regex(rx, RegexOptions.Compiled | RegexOptions.IgnoreCase))
            .ToList();

        var guardrails = await _yaml.LoadYamlAsync(Path.Combine(root, "ops", "policies", "guardrails.yml"), ct);
        var guardRequire = guardrails.TryGetValue("require", out var req) && req is IDictionary<string, object?> rdict
            ? rdict
            : new Dictionary<string, object?>();
        var guardSuggest = guardrails.TryGetValue("suggest", out var sug) && sug is IDictionary<string, object?> sdict
            ? sdict
            : new Dictionary<string, object?>();

        var requireHealth = GetBool(guardRequire, "healthcheck", false);
        var requireLogging = GetBool(guardRequire, "logging", false);
        var requireResources = GetBool(guardRequire, "resources_limits", false);
        var suggestResources = GetBool(guardSuggest, "resources_limits", false);

        // --- Template presence & basic compose keys ---
        var templatePath = Path.Combine(root, "stacks", stackId, "docker-stack.template.yml");
        if (!File.Exists(templatePath))
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, "STACK_TEMPLATE_MISSING",
                "Missing docker-stack.template.yml", templatePath));
            return new StackValidationResult(stackId, errors, warnings);
        }

        var template = await _yaml.LoadYamlAsync(templatePath, ct);
        if (template.Count == 0)
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, "STACK_TEMPLATE_EMPTY",
                "Template is empty or invalid YAML", templatePath));
            return new StackValidationResult(stackId, errors, warnings);
        }

        // Top-level keys
        foreach (var k in template.Keys)
        {
            if (!allows.Contains(k, StringComparer.OrdinalIgnoreCase) &&
                !k.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "COMPOSE_KEY_NOT_ALLOWED",
                    $"Top-level key '{k}' is not in 'allow' list.", templatePath, k));
        }

        foreach (var fb in forbids)
        {
            if (template.ContainsKey(fb))
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "COMPOSE_KEY_FORBIDDEN",
                    $"Forbidden top-level key '{fb}' present.", templatePath, fb));
        }

        if (!template.TryGetValue("services", out var servicesObj) ||
            servicesObj is not IDictionary<string, object?> services || services.Count == 0)
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, "SERVICES_MISSING",
                "No 'services' section present.", templatePath, "services"));
            return new StackValidationResult(stackId, errors, warnings);
        }

        // --- Overlay sanity via dry-run render (per env) ---
        foreach (var env in envs)
        {
            try
            {
                var rr = new RenderRequest(
                    RootPath: root,
                    StackId: stackId,
                    Environments: new[] { env },
                    DryRun: true,
                    Quiet: true,
                    Preview: false,
                    WriteHistory: false,
                    OutDir: ".sb-validate",
                    AppSettingsMode: request.AppSettingsMode,
                    AppSettingsTarget: request.AppSettingsTarget
                );
                await _renderer.RenderAsync(rr, ct);
            }
            catch (DirectoryNotFoundException ex)
            {
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "RENDER_DIR_MISSING", $"[{env}] {ex.Message}",
                    null));
            }
            catch (FileNotFoundException ex)
            {
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "RENDER_FILE_MISSING", $"[{env}] {ex.Message}",
                    null));
            }
            catch (InvalidOperationException ex)
            {
                // overlay conflicts, missing sections, etc. from RenderExecutor
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "OVERLAY_CONFLICT", $"[{env}] {ex.Message}",
                    null));
            }
            catch (Exception ex)
            {
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "RENDER_UNEXPECTED",
                    $"[{env}] Unexpected during render: {ex.Message}", null));
            }
        }

        // --- appsettings*.json structural validation (global) ---
        foreach (var env in envs)
            await ValidateAppsettingsJsonDirAsync(Path.Combine(root, "stacks", "all", env, "env"), errors, ct);

        // --- Metadata (read once) ---
        var aliasesPath = Path.Combine(root, "stacks", stackId, "aliases.yml");
        var aliases = await _yaml.LoadYamlAsync(aliasesPath, ct) ?? new Dictionary<string, object?>();
        var aliasMap = ToStringToStringMap(aliases);

        var groupsPath = Path.Combine(root, "metadata", "groups.yml");
        var groupsYaml = await _yaml.LoadYamlAsync(groupsPath, ct);
        var groupMap = ToStringToStringMap(groupsYaml);

        var requiredKeysPath = Path.Combine(root, "ops", "checks", "required-keys.yml");
        var reqKeysYaml = await _yaml.LoadYamlAsync(requiredKeysPath, ct);
        var reqServiceKeys =
            reqKeysYaml.TryGetValue("services", out var svcReq) && svcReq is IDictionary<string, object?> svcReqMap
                ? ToStringToStringListMap(svcReqMap)
                : new Dictionary<string, List<string>>();
        var reqGroupKeys =
            reqKeysYaml.TryGetValue("groups", out var grpReq) && grpReq is IDictionary<string, object?> grpReqMap
                ? ToStringToStringListMap(grpReqMap)
                : new Dictionary<string, List<string>>();

        // Validate aliases & service presence
        foreach (var kv in aliasMap)
        {
            var templateSvc = kv.Key;
            var canonical = kv.Value;
            if (!services.ContainsKey(templateSvc))
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "ALIAS_UNKNOWN_TEMPLATE_SERVICE",
                    $"Alias maps unknown template service '{templateSvc}'.", aliasesPath, templateSvc));

            var svcDir = Path.Combine(root, "services", canonical);
            if (!Directory.Exists(svcDir))
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "ALIAS_CANONICAL_DIR_MISSING",
                    $"Canonical service directory not found: services/{canonical}", aliasesPath, canonical));
        }

        // --- Per-service validations ---
        var canonicalChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var svcKvp in services)
        {
            var svcName = svcKvp.Key;
            var svcMap = svcKvp.Value as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            var canonical = aliasMap.TryGetValue(svcName, out var c) ? c : svcName;

            // appsettings*.json structural validation (service-specific, once per canonical)
            if (!canonicalChecked.Contains(canonical))
            {
                foreach (var env in envs)
                    await ValidateAppsettingsJsonDirAsync(Path.Combine(root, "services", canonical, "env", env), errors,
                        ct);
                canonicalChecked.Add(canonical);
            }

            // Forbidden keys at service level
            foreach (var fb in forbids)
            {
                if (svcMap.ContainsKey(fb))
                    errors.Add(new ValidationIssue(ValidationSeverity.Error, "SERVICE_KEY_FORBIDDEN",
                        $"Service '{svcName}' has forbidden key '{fb}'.", templatePath, $"services.{svcName}.{fb}"));
            }

            // Image checks
            if (svcMap.TryGetValue("image", out var imgVal) && imgVal is string image &&
                !string.IsNullOrWhiteSpace(image))
            {
                var allowMatch = allowMatchers.Any(rx => rx.IsMatch(image));
                if (!allowMatch)
                {
                    var hasDigest = image.Contains("@sha256:", StringComparison.OrdinalIgnoreCase); // fixed: Contains
                    var hasColon = image.Contains(':');
                    var hasTag = hasColon && !image.EndsWith(':');
                    var tag = hasTag ? image[(image.LastIndexOf(':') + 1)..] : "";

                    if (forbidLatest && hasTag && string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase))
                        errors.Add(new ValidationIssue(ValidationSeverity.Error, "IMAGE_LATEST_FORBIDDEN",
                            $"Service '{svcName}' uses ':latest' tag.", templatePath, $"services.{svcName}.image"));

                    if (requireTagOrDigest && !hasDigest && !hasTag && !allowTagless)
                        errors.Add(new ValidationIssue(ValidationSeverity.Error, "IMAGE_TAG_REQUIRED",
                            $"Service '{svcName}' image has no tag or digest.", templatePath,
                            $"services.{svcName}.image"));
                }
            }

            // Labels policy
            var labelKeys = CollectLabelKeys(svcMap);
            foreach (var reqLabel in requiredLabels)
                if (!labelKeys.Contains(reqLabel))
                    errors.Add(new ValidationIssue(ValidationSeverity.Error, "LABEL_REQUIRED_MISSING",
                        $"Service '{svcName}' missing required label '{reqLabel}'.", templatePath,
                        $"services.{svcName}.labels"));

            foreach (var resLabel in reservedLabels)
                if (labelKeys.Contains(resLabel))
                    errors.Add(new ValidationIssue(ValidationSeverity.Error, "LABEL_RESERVED_USED",
                        $"Service '{svcName}' uses reserved label '{resLabel}'.", templatePath,
                        $"services.{svcName}.labels"));

            // Guardrails
            if (requireHealth && !svcMap.ContainsKey("healthcheck"))
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "HEALTHCHECK_REQUIRED",
                    $"Service '{svcName}' missing 'healthcheck'.", templatePath, $"services.{svcName}.healthcheck"));
            else if (!requireHealth && !svcMap.ContainsKey("healthcheck"))
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "HEALTHCHECK_SUGGESTED",
                    $"Service '{svcName}' has no 'healthcheck' in template.", templatePath, $"services.{svcName}"));

            var hasLogging = svcMap.ContainsKey("logging") ||
                             (TryGetMap(svcMap, "deploy") is IDictionary<string, object?> d &&
                              d.ContainsKey("logging"));
            if (requireLogging && !hasLogging)
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "LOGGING_REQUIRED",
                    $"Service '{svcName}' missing logging settings.", templatePath, $"services.{svcName}.logging"));

            var deployMap = TryGetMap(svcMap, "deploy");
            var resourcesMap = deployMap is null ? null : TryGetMap(deployMap, "resources");
            var limitsMap = resourcesMap is null ? null : TryGetMap(resourcesMap, "limits");
            var hasCpu = limitsMap is not null && (limitsMap.ContainsKey("cpus") || limitsMap.ContainsKey("cpu"));
            var hasMem = limitsMap is not null && limitsMap.ContainsKey("memory");

            if (requireResources && (!hasCpu || !hasMem))
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "RESOURCES_LIMITS_REQUIRED",
                    $"Service '{svcName}' must define deploy.resources.limits (cpu/memory).", templatePath,
                    $"services.{svcName}.deploy.resources.limits"));
            else if (!requireResources && suggestResources && (!hasCpu || !hasMem))
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "RESOURCES_LIMITS_SUGGESTED",
                    $"Service '{svcName}' should define deploy.resources.limits (cpu/memory).", templatePath,
                    $"services.{svcName}.deploy.resources.limits"));
        }

        // --- Top-level secrets/configs definition shape ---
        await ValidateSecretsConfigsShapeAsync(root, stackId, "secrets.yml", "secrets", errors, warnings, ct);
        await ValidateSecretsConfigsShapeAsync(root, stackId, "configs.yml", "configs", errors, warnings, ct);

        // --- Required configuration keys across envs (template env + baselines + appsettings) ---
        var templateServices = (IDictionary<string, object?>)template["services"];
        foreach (var svcName in templateServices.Keys)
        {
            var canonical2 = aliasMap.TryGetValue(svcName, out var c2) ? c2 : svcName;

            var requiredForSvc = reqServiceKeys.TryGetValue(canonical2, out var list2) ? list2 : new List<string>();
            var grp2 = groupMap.TryGetValue(canonical2, out var g2) ? g2 : null;
            var requiredForGroup = (grp2 is not null && reqGroupKeys.TryGetValue(grp2, out var gl2))
                ? gl2
                : new List<string>();
            var required = requiredForSvc.Concat(requiredForGroup).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (required.Count == 0)
                continue;

            foreach (var env in envs)
            {
                var foundKeys = await CollectConfigKeysAsync(root, canonical2, env, templatePath, templateServices,
                    svcName, ct);
                foreach (var rk in required)
                {
                    if (!foundKeys.Contains(rk))
                        errors.Add(new ValidationIssue(ValidationSeverity.Error, "ENV_REQUIRED_MISSING",
                            $"[{env}] Service '{canonical2}' missing required config key '{rk}' in template/baselines/appsettings.",
                            null, $"env:{env}:{canonical2}:{rk}"));
                }
            }
        }

        // --- x-sb-secrets overlays & secrets-map checks ---
        await ValidateXsbSecretsOverlaysAsync(
            root,
            stackId,
            envs,
            templateServices: ((IDictionary<string, object?>)template["services"]).Keys.ToList(),
            aliasMap: aliasMap,
            warnings: warnings,
            ct: ct);

        return new StackValidationResult(stackId, errors, warnings);
    }

    // ---- helpers ----

    private static HashSet<string> CollectLabelKeys(IDictionary<string, object?> svcMap)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (svcMap.TryGetValue("labels", out var l1))
            foreach (var k in ExtractLabels(l1))
                keys.Add(k);

        if (TryGetMap(svcMap, "deploy") is IDictionary<string, object?> deploy &&
            deploy.TryGetValue("labels", out var l2))
            foreach (var k in ExtractLabels(l2))
                keys.Add(k);

        return keys;
    }

    private static IEnumerable<string> ExtractLabels(object? node)
    {
        if (node is IDictionary<string, object?> map)
            return map.Keys;

        if (node is IEnumerable<object?> list)
        {
            var keys = new List<string>();
            foreach (var item in list)
            {
                var s = item?.ToString() ?? "";
                var idx = s.IndexOf('=');
                keys.Add(idx > 0 ? s[..idx] : s);
            }

            return keys;
        }

        return Array.Empty<string>();
    }

    private static IDictionary<string, object?>? TryGetMap(IDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) && v is IDictionary<string, object?> m ? m : null;

    private static bool GetBool(IDictionary<string, object?> dict, string key, bool defaultValue)
        => dict.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;

    private static List<string>? GetStringList(IDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val is null) return null;
        if (val is IEnumerable<object?> list)
            return list.Select(x => x?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return null;
    }

    private static Dictionary<string, string> ToStringToStringMap(IDictionary<string, object?> map)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
            d[kv.Key] = kv.Value?.ToString() ?? "";
        return d;
    }

    private static Dictionary<string, List<string>> ToStringToStringListMap(IDictionary<string, object?> map)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            if (kv.Value is IEnumerable<object?> list)
                d[kv.Key] = list.Select(x => x?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return d;
    }

    private async Task ValidateAppsettingsJsonDirAsync(string dir, List<ValidationIssue> errors, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "appsettings*.json"))
        {
            try
            {
                using var s = File.OpenRead(file);
                await JsonDocument.ParseAsync(s, cancellationToken: ct);
            }
            catch
            {
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "APPSETTINGS_INVALID_JSON",
                    $"Invalid JSON: {Path.GetFileName(file)}", file, null));
            }
        }
    }

    private async Task<HashSet<string>> CollectConfigKeysAsync(string root, string canonicalService, string env,
        string templatePath, IDictionary<string, object?> services, string templateSvcName, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) From template service environment
        if (services.TryGetValue(templateSvcName, out var svc) && svc is IDictionary<string, object?> svcMap)
        {
            if (svcMap.TryGetValue("environment", out var envNode))
            {
                if (envNode is IDictionary<string, object?> envMap)
                {
                    foreach (var k in envMap.Keys) keys.Add(k);
                }
                else if (envNode is IEnumerable<object?> list)
                {
                    foreach (var item in list)
                    {
                        var s = item?.ToString() ?? "";
                        var idx = s.IndexOf('=');
                        var key = idx > 0 ? s[..idx] : s;
                        if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
                    }
                }
            }
        }

        // 2) Global baseline: stacks/all/<env>/env/*.json (flatten, includes appsettings)
        var baseEnvDir = Path.Combine(root, "stacks", "all", env, "env");
        if (Directory.Exists(baseEnvDir))
        {
            foreach (var file in Directory.GetFiles(baseEnvDir, "*.json"))
            foreach (var k in await ReadJsonKeysAsync(file, ct))
                keys.Add(k);
        }

        // 3) Service-specific: services/<canonicalService>/env/<env>/*.json (flatten, includes appsettings)
        var svcEnvDir = Path.Combine(root, "services", canonicalService, "env", env);
        if (Directory.Exists(svcEnvDir))
        {
            foreach (var file in Directory.GetFiles(svcEnvDir, "*.json", SearchOption.AllDirectories))
            foreach (var k in await ReadJsonKeysAsync(file, ct))
                keys.Add(k);
        }

        return keys;
    }

    private static async Task<IEnumerable<string>> ReadJsonKeysAsync(string path, CancellationToken ct)
    {
        try
        {
            using var s = File.OpenRead(path);
            var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var keys = new List<string>();
            CollectJsonKeys(doc.RootElement, "", keys);
            return keys;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void CollectJsonKeys(JsonElement el, string prefix, List<string> acc)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var name = string.IsNullOrEmpty(prefix) ? prop.Name : prefix + "__" + prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    CollectJsonKeys(prop.Value, name, acc);
                else
                    acc.Add(name);
            }
        }
    }

    private static async Task WriteReportAsync(string root, StackValidationResult res, CancellationToken ct)
    {
        var reports = Path.Combine(root, "ops", "reports", "preflight");
        Directory.CreateDirectory(reports);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(reports, $"{res.StackId}-{stamp}.json");

        var json = JsonSerializer.Serialize(res, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>
    /// Ensures secrets/configs definition files have a valid shape.
    /// Accepts empty maps. Warns if topKey section is missing.
    /// </summary>
    private async Task ValidateSecretsConfigsShapeAsync(
        string root,
        string stackId,
        string fileName,
        string topKey,
        List<ValidationIssue> errors,
        List<ValidationIssue> warnings,
        CancellationToken ct)
    {
        var path = Path.Combine(root, "stacks", stackId, fileName);
        if (!File.Exists(path)) return;

        var yaml = await _yaml.LoadYamlAsync(path, ct);
        if (yaml.Count == 0)
        {
            warnings.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                $"{topKey.ToUpper()}_FILE_EMPTY",
                $"{fileName} is empty or invalid YAML (ignored).",
                path));
            return;
        }

        if (!yaml.TryGetValue(topKey, out var top) || top is not IDictionary<string, object?> map)
        {
            warnings.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                $"{topKey.ToUpper()}_TOPKEY_MISSING",
                $"'{topKey}:' section missing in {fileName} (ignored).",
                path,
                topKey));
            return;
        }

        // Empty map is OK.
        if (map.Count == 0) return;

        foreach (var item in map)
        {
            var def = item.Value as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            var hasExternal = def.TryGetValue("external", out var ev) && ev is bool eb && eb;
            var hasFile = def.ContainsKey("file");

            if (hasExternal && hasFile)
                errors.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"{topKey.ToUpper()}_BOTH_FILE_AND_EXTERNAL",
                    $"Item '{item.Key}' in {fileName} has both 'external' and 'file'.",
                    path,
                    $"{topKey}.{item.Key}"));
            // If neither, it's acceptable to be declared later; do not error.
        }
    }

    /// <summary>
    /// Loads secrets map for the given environment. Returns empty map if missing; optionally emits a Warning.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadSecretsMapAsync(
        string root,
        string env,
        bool warnIfMissing,
        List<ValidationIssue> warnings,
        CancellationToken ct)
    {
        var mapPath = Path.Combine(root, "ops", "vars", $"secrets-map.{env}.yml");
        if (!File.Exists(mapPath))
        {
            if (warnIfMissing)
                warnings.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "SECRETS_MAP_MISSING",
                    $"secrets-map file not found for env '{env}'. Rendering may skip x-sb-secrets.",
                    mapPath));
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

    /// <summary>
    /// Enumerates overlay files for x-sb-secrets scanning (global+stack+service overlays).
    /// </summary>
    private static IEnumerable<(string scope, string path)> EnumerateOverlayFiles(
        string root, string stackId, string env, IEnumerable<string> canonicalServices)
    {
        // stacks/all/<env>/*.yml | stacks/all/<env>/stack/*.yml
        var g1 = Path.Combine(root, "stacks", "all", env);
        var g2 = Path.Combine(root, "stacks", "all", env, "stack");

        // stacks/<stack>/<env>/*.yml | stacks/<stack>/<env>/stack/*.yml
        var s1 = Path.Combine(root, "stacks", stackId, env);
        var s2 = Path.Combine(root, "stacks", stackId, env, "stack");

        static IEnumerable<string> YamlFiles(string dir)
            => Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.yml").Concat(Directory.EnumerateFiles(dir, "*.yaml"))
                : Array.Empty<string>();

        foreach (var p in YamlFiles(g1)) yield return ("global", p);
        foreach (var p in YamlFiles(g2)) yield return ("global", p);
        foreach (var p in YamlFiles(s1)) yield return ("stack", p);
        foreach (var p in YamlFiles(s2)) yield return ("stack", p);

        // services/<svc>/<env>/*.yml
        foreach (var svc in canonicalServices)
        {
            var dir = Path.Combine(root, "services", svc, env);
            foreach (var p in YamlFiles(dir)) yield return ($"service:{svc}", p);
        }
    }

    /// <summary>
    /// Validates x-sb-secrets items (shape + mapping) across overlays. Wildcard services ("*") applied to all.
    /// Warnings only (does not fail the validation).
    /// </summary>
    private async Task ValidateXsbSecretsOverlaysAsync(
        string root,
        string stackId,
        List<string> envs,
        List<string> templateServices,
        Dictionary<string, string> aliasMap,
        List<ValidationIssue> warnings,
        CancellationToken ct)
    {
        // Build canonical service set (apply aliases if provided)
        var canonicalServices = new List<string>();
        foreach (var name in templateServices)
        {
            var canonical = aliasMap.TryGetValue(name, out var c) && !string.IsNullOrWhiteSpace(c) ? c : name;
            if (!canonicalServices.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                canonicalServices.Add(canonical);
        }

        foreach (var env in envs)
        {
            var secretsMap = await LoadSecretsMapAsync(root, env, warnIfMissing: true, warnings, ct);

            // collect x-sb-secrets per service
            var collected = new Dictionary<string, List<object?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in canonicalServices) collected[svc] = new List<object?>();

            foreach (var (scope, path) in EnumerateOverlayFiles(root, stackId, env, canonicalServices))
            {
                IDictionary<string, object?>? yaml;
                try
                {
                    yaml = await _yaml.LoadYamlAsync(path, ct);
                }
                catch
                {
                    continue;
                }

                if (yaml is null || yaml.Count == 0) continue;

                // If this is a service overlay file (service:<name>) and has root-level x-sb-secrets
                if (scope.StartsWith("service:", StringComparison.Ordinal))
                {
                    var svcName = scope.Split(':', 2)[1];
                    if (yaml.TryGetValue("x-sb-secrets", out var rootNode) && rootNode is IEnumerable<object?> rootList)
                        collected[svcName].AddRange(rootList);
                }

                // services: section
                if (yaml.TryGetValue("services", out var svNode) && svNode is IDictionary<string, object?> svMap)
                {
                    // wildcard
                    if (svMap.TryGetValue("*", out var wildNode) && wildNode is IDictionary<string, object?> wildMap)
                    {
                        if (wildMap.TryGetValue("x-sb-secrets", out var wildX) &&
                            wildX is IEnumerable<object?> wildList)
                        {
                            foreach (var svc in canonicalServices)
                                collected[svc].AddRange(wildList);
                        }
                    }

                    // explicit services
                    foreach (var kv in svMap)
                    {
                        if (kv.Key == "*") continue;
                        var svcName = kv.Key;
                        var canonical = aliasMap.TryGetValue(svcName, out var c) && !string.IsNullOrWhiteSpace(c)
                            ? c
                            : svcName;

                        if (!canonicalServices.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                            continue;

                        if (kv.Value is IDictionary<string, object?> svcMap &&
                            svcMap.TryGetValue("x-sb-secrets", out var xNode) &&
                            xNode is IEnumerable<object?> list)
                        {
                            collected[canonical].AddRange(list);
                        }
                    }
                }
            }

            // validate collected items per service
            foreach (var svc in canonicalServices)
            {
                foreach (var item in collected[svc])
                {
                    string? key = null;
                    if (item is string s)
                    {
                        key = s;
                    }
                    else if (item is IDictionary<string, object?> m)
                    {
                        // shape check
                        if (!m.ContainsKey("key"))
                        {
                            warnings.Add(new ValidationIssue(
                                ValidationSeverity.Warning,
                                "X_SB_SECRETS_ITEM_MISSING_KEY",
                                $"Service '{svc}' has an x-sb-secrets item without 'key'. Item will be ignored at render time.",
                                null,
                                $"env:{env}:service:{svc}:x-sb-secrets"));
                            continue;
                        }

                        key = m["key"]?.ToString();
                        // optional unknown field hint
                        var allowed = new HashSet<string>(new[] { "key", "target", "mode", "uid", "gid" },
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var field in m.Keys)
                        {
                            if (!allowed.Contains(field))
                            {
                                warnings.Add(new ValidationIssue(
                                    ValidationSeverity.Warning,
                                    "X_SB_SECRETS_ITEM_UNKNOWN_FIELD",
                                    $"Service '{svc}' x-sb-secrets item has unknown field '{field}'.",
                                    null,
                                    $"env:{env}:service:{svc}:x-sb-secrets"));
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    // mapping check
                    if (secretsMap.Count == 0)
                    {
                        // already warned with SECRETS_MAP_MISSING above; no per-item warning needed
                        continue;
                    }

                    if (!secretsMap.ContainsKey(key))
                    {
                        warnings.Add(new ValidationIssue(
                            ValidationSeverity.Warning,
                            "SECRETS_MAP_ENTRY_MISSING",
                            $"No secrets-map entry for key '{key}' (env '{env}', service '{svc}'). Secret will be skipped.",
                            Path.Combine(root, "ops", "vars", $"secrets-map.{env}.yml"),
                            key));
                    }
                }
            }
        }
    }
}