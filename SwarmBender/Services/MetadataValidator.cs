using System.Text.RegularExpressions;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

public sealed class MetadataValidator : IMetadataValidator
{
    private readonly IYamlLoader _yaml;

    public MetadataValidator(IYamlLoader yaml) => _yaml = yaml;

    public async Task<MetaValidationResult> ValidateAsync(string rootPath, CancellationToken ct = default)
    {
        var issues = new List<MetaIssue>();

        await ValidateTenantsAsync(Path.Combine(rootPath, "metadata", "tenants.yml"), issues, ct);
        await ValidateGroupsAsync(Path.Combine(rootPath, "metadata", "groups.yml"), issues, ct);

        var errors = issues.Count(i => i.Kind == MetaIssueKind.Error);
        var warns  = issues.Count(i => i.Kind == MetaIssueKind.Warning);
        return new MetaValidationResult(errors, warns, issues);
    }

    // ---------- tenants.yml ----------
    private async Task ValidateTenantsAsync(string path, IList<MetaIssue> issues, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            // Not having tenants is OK; warn for discoverability.
            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, "File not found (optional)."));
            return;
        }

        IDictionary<string, object?> map;
        try { map = await _yaml.LoadYamlAsync(path, ct); }
        catch (Exception ex)
        {
            issues.Add(new MetaIssue(MetaIssueKind.Error, path, $"Invalid YAML: {ex.Message}"));
            return;
        }

        if (!map.TryGetValue("tenants", out var tenantsNode) || tenantsNode is not IDictionary<string, object?> tenantsMap)
        {
            issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Missing top-level 'tenants' mapping."));
            return;
        }

        var slugRx   = new Regex("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
        var envKeyRx = new Regex("^[A-Z0-9_]+$", RegexOptions.Compiled);

        foreach (var (slug, value) in tenantsMap)
        {
            if (string.IsNullOrWhiteSpace(slug) || !slugRx.IsMatch(slug))
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Tenant key must be a lowercase slug ([a-z0-9-]).", $"tenants.{slug}"));

            if (value is not IDictionary<string, object?> tMap)
            {
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Tenant entry must be a mapping.", $"tenants.{slug}"));
                continue;
            }

            // displayName (required, non-empty)
            if (!tMap.TryGetValue("displayName", out var dnObj) || string.IsNullOrWhiteSpace(dnObj?.ToString()))
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Missing required 'displayName'.", $"tenants.{slug}.displayName"));

            // vars (optional map<string,string> uppercase keys)
            if (tMap.TryGetValue("vars", out var varsObj))
            {
                if (varsObj is not IDictionary<string, object?> varsMap)
                {
                    issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'vars' must be a mapping of string->string.", $"tenants.{slug}.vars"));
                }
                else
                {
                    foreach (var kv in varsMap)
                    {
                        if (!envKeyRx.IsMatch(kv.Key))
                            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, $"Env var key '{kv.Key}' is not UPPER_SNAKE_CASE.", $"tenants.{slug}.vars.{kv.Key}"));
                        if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Value.ToString()))
                            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, $"Env var '{kv.Key}' has empty value.", $"tenants.{slug}.vars.{kv.Key}"));
                    }
                }
            }

            // traefik.certresolver (optional string)
            if (tMap.TryGetValue("traefik", out var trObj))
            {
                if (trObj is not IDictionary<string, object?> trMap)
                {
                    issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'traefik' must be a mapping.", $"tenants.{slug}.traefik"));
                }
                else if (trMap.TryGetValue("certresolver", out var crObj) && crObj is not null && string.IsNullOrWhiteSpace(crObj.ToString()))
                {
                    issues.Add(new MetaIssue(MetaIssueKind.Warning, path, "'traefik.certresolver' is empty.", $"tenants.{slug}.traefik.certresolver"));
                }
            }

            // domains (optional list<string>)
            if (tMap.TryGetValue("domains", out var domObj))
            {
                if (domObj is not IEnumerable<object?> list)
                {
                    issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'domains' must be a sequence of strings.", $"tenants.{slug}.domains"));
                }
                else
                {
                    foreach (var (item, idx) in list.Select((x, i) => (x, i)))
                    {
                        if (item is null || string.IsNullOrWhiteSpace(item.ToString()))
                            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, $"domains[{idx}] is empty.", $"tenants.{slug}.domains[{idx}]"));
                    }
                }
            }
        }
    }

    // ---------- groups.yml ----------
    private async Task ValidateGroupsAsync(string path, IList<MetaIssue> issues, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            // Not having groups is OK; warn for discoverability.
            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, "File not found (optional)."));
            return;
        }

        IDictionary<string, object?> map;
        try { map = await _yaml.LoadYamlAsync(path, ct); }
        catch (Exception ex)
        {
            issues.Add(new MetaIssue(MetaIssueKind.Error, path, $"Invalid YAML: {ex.Message}"));
            return;
        }

        if (!map.TryGetValue("groups", out var groupsNode) || groupsNode is not IDictionary<string, object?> groupsMap)
        {
            issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Missing top-level 'groups' mapping."));
            return;
        }

        var svcNameRx = new Regex("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

        foreach (var (gKey, value) in groupsMap)
        {
            if (string.IsNullOrWhiteSpace(gKey))
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Group key must be non-empty.", $"groups.{gKey}"));

            if (value is not IDictionary<string, object?> gMap)
            {
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Group entry must be a mapping.", $"groups.{gKey}"));
                continue;
            }

            // services (required list<string> non-empty, name pattern)
            if (!gMap.TryGetValue("services", out var svcObj) || svcObj is not IEnumerable<object?> svcList)
            {
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "Missing required 'services' sequence.", $"groups.{gKey}.services"));
            }
            else
            {
                var any = false;
                foreach (var (item, idx) in svcList.Select((x, i) => (x, i)))
                {
                    any = true;
                    var s = item?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(s))
                        issues.Add(new MetaIssue(MetaIssueKind.Error, path, $"services[{idx}] is empty.", $"groups.{gKey}.services[{idx}]"));
                    else if (!svcNameRx.IsMatch(s))
                        issues.Add(new MetaIssue(MetaIssueKind.Warning, path, $"Service '{s}' is not kebab-case ([a-z][a-z0-9-]*).", $"groups.{gKey}.services[{idx}]"));
                }
                if (!any)
                    issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'services' must not be empty.", $"groups.{gKey}.services"));
            }

            // defaults (optional map), labels (optional map<string,string>), env (optional map<string,string>)
            if (gMap.TryGetValue("defaults", out var defObj) && defObj is not IDictionary<string, object?>)
                issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'defaults' must be a mapping.", $"groups.{gKey}.defaults"));

            if (gMap.TryGetValue("labels", out var lblObj))
            {
                if (lblObj is not IDictionary<string, object?> lblMap)
                {
                    issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'labels' must be a mapping of string->string.", $"groups.{gKey}.labels"));
                }
                else
                {
                    foreach (var kv in lblMap)
                        if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Value.ToString()))
                            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, $"Label '{kv.Key}' has empty value.", $"groups.{gKey}.labels.{kv.Key}"));
                }
            }

            if (gMap.TryGetValue("env", out var envObj))
            {
                if (envObj is not IDictionary<string, object?> envMap)
                {
                    issues.Add(new MetaIssue(MetaIssueKind.Error, path, "'env' must be a mapping of string->string.", $"groups.{gKey}.env"));
                }
                else
                {
                    foreach (var kv in envMap)
                        if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Value.ToString()))
                            issues.Add(new MetaIssue(MetaIssueKind.Warning, path, $"Env '{kv.Key}' has empty value.", $"groups.{gKey}.env.{kv.Key}"));
                }
            }
        }
    }
}