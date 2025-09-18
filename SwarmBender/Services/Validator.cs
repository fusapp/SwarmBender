using System.Text.Json;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>Validates stacks against policies and basic schema.</summary>
public sealed class Validator : IValidator
{
    private readonly IYamlLoader _yaml;

    public Validator(IYamlLoader yaml) => _yaml = yaml;

    public async Task<ValidateResult> ValidateAsync(ValidateRequest request, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(request.RootPath);
        var stacksRoot = Path.Combine(root, "stacks");

        var stacks = new List<string>();
        if (!Directory.Exists(stacksRoot))
            throw new DirectoryNotFoundException($"stacks folder not found at: {stacksRoot}");

        if (!string.IsNullOrWhiteSpace(request.StackId))
        {
            stacks.Add(request.StackId!);
        }
        else
        {
            foreach (var dir in Directory.GetDirectories(stacksRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
                    continue;
                stacks.Add(name);
            }
        }

        var results = new List<StackValidationResult>();
        foreach (var stackId in stacks)
        {
            var res = await ValidateSingleStackAsync(root, stackId, ct);
            results.Add(res);
            await WriteReportAsync(root, res, ct);
        }

        var combined = new ValidateResult(results);
        if (!string.IsNullOrWhiteSpace(request.OutFile))
        {
            var outPath = Path.GetFullPath(Path.Combine(root, request.OutFile!));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var json = JsonSerializer.Serialize(combined, new JsonSerializerOptions{WriteIndented=true});
            await File.WriteAllTextAsync(outPath, json, ct);
        }

        return combined;
    }

    private async Task<StackValidationResult> ValidateSingleStackAsync(string root, string stackId, CancellationToken ct)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        var composePolicyPath = Path.Combine(root, "ops", "checks", "compose-v3.yml");
        var policy = await _yaml.LoadYamlAsync(composePolicyPath, ct);
        var allows = GetStringList(policy, "allow") ?? new List<string> { "version", "services" };
        var forbids = GetStringList(policy, "forbid") ?? new List<string> { "build", "depends_on" };

        var templatePath = Path.Combine(root, "stacks", stackId, "docker-stack.template.yml");
        if (!File.Exists(templatePath))
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, "STACK_TEMPLATE_MISSING", "Missing docker-stack.template.yml", templatePath));
            return new StackValidationResult(stackId, errors, warnings);
        }

        var template = await _yaml.LoadYamlAsync(templatePath, ct);
        if (template.Count == 0)
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, "STACK_TEMPLATE_EMPTY", "Template is empty or invalid YAML", templatePath));
            return new StackValidationResult(stackId, errors, warnings);
        }

        foreach (var k in template.Keys)
        {
            if (!allows.Contains(k, StringComparer.OrdinalIgnoreCase) && !k.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "COMPOSE_KEY_NOT_ALLOWED", $"Top-level key '{k}' is not in 'allow' list.", templatePath, k));
            }
        }

        foreach (var fb in forbids)
        {
            if (template.ContainsKey(fb))
            {
                errors.Add(new ValidationIssue(ValidationSeverity.Error, "COMPOSE_KEY_FORBIDDEN", $"Forbidden top-level key '{fb}' present.", templatePath, fb));
            }
        }

        if (!template.TryGetValue("services", out var servicesObj) || servicesObj is not IDictionary<string, object?> services || services.Count == 0)
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, "SERVICES_MISSING", "No 'services' section present.", templatePath, "services"));
            return new StackValidationResult(stackId, errors, warnings);
        }

        foreach (var svcKvp in services)
        {
            var svcName = svcKvp.Key;
            if (svcKvp.Value is IDictionary<string, object?> svcMap)
            {
                foreach (var fb in forbids)
                {
                    if (svcMap.ContainsKey(fb))
                    {
                        errors.Add(new ValidationIssue(ValidationSeverity.Error, "SERVICE_KEY_FORBIDDEN", $"Service '{svcName}' has forbidden key '{fb}'.", templatePath, $"services.{svcName}.{fb}"));
                    }
                }
            }
        }

        var guardrailsPath = Path.Combine(root, "ops", "policies", "guardrails.yml");
        var guard = await _yaml.LoadYamlAsync(guardrailsPath, ct);
        var require = guard.TryGetValue("require", out var reqObj) && reqObj is IDictionary<string, object?> reqDict ? reqDict : new Dictionary<string, object?>();
        if (require.TryGetValue("healthcheck", out var hc) && hc is bool mustHc && mustHc)
        {
            foreach (var svcKvp in services)
            {
                var svcName = svcKvp.Key;
                var svcMap = svcKvp.Value as IDictionary<string, object?>;
                if (svcMap is null) continue;
                if (!svcMap.ContainsKey("healthcheck"))
                {
                    warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "HEALTHCHECK_SUGGESTED",
                        $"Service '{svcName}' has no 'healthcheck' in template. It may be added by layered config; verify after render.",
                        templatePath, $"services.{svcName}"));
                }
            }
        }

        return new StackValidationResult(stackId, errors, warnings);
    }

    private static List<string>? GetStringList(IDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val is null)
            return null;

        if (val is IEnumerable<object?> list)
        {
            return list.Select(x => x?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        return null;
    }

    private static async Task WriteReportAsync(string root, StackValidationResult res, CancellationToken ct)
    {
        var reports = Path.Combine(root, "ops", "reports", "preflight");
        Directory.CreateDirectory(reports);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(reports, $"{res.StackId}-{stamp}.json");

        var json = System.Text.Json.JsonSerializer.Serialize(res, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json, ct);
    }
}
