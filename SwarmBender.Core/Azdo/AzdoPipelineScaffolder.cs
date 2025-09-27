using System.Text;
using System.Text.RegularExpressions;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;
using YamlDotNet.RepresentationModel;

namespace SwarmBender.Core.Azdo;

public sealed class AzdoPipelineScaffolder : IAzdoPipelineScaffolder
{
    private readonly IFileSystem _fs;
    private readonly IOutput _out;

    public AzdoPipelineScaffolder(IFileSystem fs, IOutput @out)
    { _fs = fs; _out = @out; }

    public async Task<string> GenerateAsync(
        string repoRoot,
        string stackId,
        SbConfig cfg,
        AzdoPipelineInitOptions opts,
        CancellationToken ct = default)
    {
        // 1) discover envs from stacks/{stackId}/<env>/...
        var stackDir = Path.Combine(repoRoot, "stacks", stackId);
        if (!Directory.Exists(stackDir))
            throw new DirectoryNotFoundException($"Stack directory not found: {stackDir}");

        var envs = Directory.EnumerateDirectories(stackDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n) && Regex.IsMatch(n!, "^[A-Za-z0-9_-]+$"))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (envs.Count == 0) envs = new() { "dev", "prod" };

        // 2) detect multi-tenant flag from template (x-sb-multi-tenant: true)
        var tpl1 = Path.Combine(stackDir, "docker-stack.template.yml");
        var tpl2 = Path.Combine(stackDir, "docker-stack.template.yaml");
        var tplFile = _fs.FileExists(tpl1) ? tpl1 : _fs.FileExists(tpl2) ? tpl2 : null;

        bool isMultiTenant = false;
        if (tplFile is not null)
        {
            var yaml = await _fs.ReadAllTextAsync(tplFile, ct);
            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(yaml));
                var root = (YamlMappingNode)stream.Documents[0].RootNode;
                if (root.Children.TryGetValue(new YamlScalarNode("x-sb-multi-tenant"), out var flagNode))
                    isMultiTenant = bool.TryParse(((YamlScalarNode)flagNode).Value, out var b) && b;
            }
            catch { /* defensive parse */ }
        }

        // 3) collect tenants from SbConfig metadata
        var tenantSlugs = (cfg.Metadata?.Tenants ?? new())
            .Select(t => t.Slug)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 4) build YAML
        var filePath = Path.Combine(repoRoot, "ops", "pipelines", "azdo", $"deploy-{stackId}.yml");
        var yamlText = BuildYaml(stackId, envs, opts, isMultiTenant, tenantSlugs);

        // 5) write
        _fs.EnsureDirectory(Path.GetDirectoryName(filePath)!);
        if (_fs.FileExists(filePath) && !opts.Force)
            throw new IOException($"File already exists: {filePath}");

        await _fs.WriteAllTextAsync(filePath, yamlText, ct);
        _out.Success($"Azure DevOps pipeline created: {filePath}");
        return filePath;
    }

    private static string BuildYaml(
        string stackId,
        IReadOnlyList<string> envs,
        AzdoPipelineInitOptions o,
        bool isMultiTenant,
        IReadOnlyList<string> tenantSlugs)
    {
        var sb = new StringBuilder();

        // --- trigger ---
        switch (o.Trigger.Mode)
        {
            case TriggerMode.None:
                sb.AppendLine("trigger: none");
                break;
            case TriggerMode.ManualOnly:
                sb.AppendLine("trigger: none");
                sb.AppendLine("pr: none");
                break;
            case TriggerMode.CI:
                sb.AppendLine("trigger:");
                if (o.Trigger.CiIncludeBranches.Count > 0)
                {
                    sb.AppendLine("  branches:");
                    sb.AppendLine("    include:");
                    foreach (var b in o.Trigger.CiIncludeBranches) sb.AppendLine($"    - {b}");
                    if (o.Trigger.CiExcludeBranches.Count > 0)
                    {
                        sb.AppendLine("    exclude:");
                        foreach (var b in o.Trigger.CiExcludeBranches) sb.AppendLine($"    - {b}");
                    }
                }
                if (!o.Trigger.PrEnabled) sb.AppendLine("pr: none");
                break;
        }

        // --- parameters ---
        sb.AppendLine("parameters:");
        // env param
        sb.AppendLine("- name: environmentName");
        sb.AppendLine("  displayName: Environment");
        sb.AppendLine("  type: string");
        sb.AppendLine($"  default: {envs.First().ToUpperInvariant()}");
        sb.AppendLine("  values:");
        foreach (var e in envs) sb.AppendLine($"  - {e.ToUpperInvariant()}");

        // company param
        var companies = o.Companies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        sb.AppendLine("- name: companyName");
        sb.AppendLine("  displayName: Company");
        sb.AppendLine("  type: string");
        sb.AppendLine($"  default: {companies.First()}");
        sb.AppendLine("  values:");
        foreach (var c in companies) sb.AppendLine($"  - {c}");

        // tenant param (only if multi-tenant)
        if (isMultiTenant && tenantSlugs.Count > 0)
        {
            sb.AppendLine("- name: tenantSlug");
            sb.AppendLine("  displayName: Tenant");
            sb.AppendLine("  type: string");
            sb.AppendLine($"  default: {tenantSlugs.First()}");
            sb.AppendLine("  values:");
            foreach (var t in tenantSlugs) sb.AppendLine($"  - {t}");
        }

        // extra params
        foreach (var p in o.ExtraParameters)
        {
            sb.AppendLine($"- name: {p.Name}");
            sb.AppendLine($"  displayName: {p.DisplayName}");
            sb.AppendLine($"  type: {p.Type}");
            if (!string.IsNullOrWhiteSpace(p.Default)) sb.AppendLine($"  default: {p.Default}");
            if (p.Values?.Count > 0)
            {
                sb.AppendLine("  values:");
                foreach (var v in p.Values) sb.AppendLine($"  - {v}");
            }
        }

        // --- stages/jobs ---
        var stageName = $"Deploy_{stackId}";
        var envPrefix = o.EnvironmentNamePrefix ?? "";
        var envVarResource = string.IsNullOrWhiteSpace(envPrefix)
            ? "${{ parameters.environmentName }}"
            : $"${{{{ format('{envPrefix}{{0}}', parameters.environmentName) }}}}";

        sb.AppendLine("stages:");
        sb.AppendLine($"- stage: {stageName}");
        sb.AppendLine($"  displayName: Deploy {stackId} stack");
        sb.AppendLine("  variables:");

        // registry VG
        if (!string.IsNullOrWhiteSpace(o.RegistryVariableGroup))
            sb.AppendLine($"  - group: {o.RegistryVariableGroup}");

        // user-provided VG specs expanded into variants
        foreach (var vg in o.VariableGroups)
        {
            // base
            sb.AppendLine($"  - group: {vg.Name}");

            // env
            if (vg.VariantByEnvironment)
                sb.AppendLine($"  - group: ${{{{ format('{vg.Name}_{{0}}', parameters.environmentName) }}}}");

            // tenant
            if (isMultiTenant && vg.VariantByTenant)
                sb.AppendLine($"  - group: ${{{{ format('{vg.Name}_{{0}}', parameters.tenantSlug) }}}}");

            // company
            if (vg.VariantByCompany)
                sb.AppendLine($"  - group: ${{{{ format('{vg.Name}_{{0}}', parameters.companyName) }}}}");

            // env+company
            if (vg.VariantByEnvironment && vg.VariantByCompany)
                sb.AppendLine($"  - group: ${{{{ format('{vg.Name}_{{0}}_{{1}}', parameters.environmentName, parameters.companyName) }}}}");

            // env+tenant
            if (isMultiTenant && vg.VariantByEnvironment && vg.VariantByTenant)
                sb.AppendLine($"  - group: ${{{{ format('{vg.Name}_{{0}}_{{1}}', parameters.environmentName, parameters.tenantSlug) }}}}");
        }

        sb.AppendLine("  jobs:");
        sb.AppendLine($"  - deployment: {stageName}");
        sb.AppendLine($"    displayName: Deploy {stackId}");
        sb.AppendLine("    environment:");
        sb.AppendLine($"      name: " + envVarResource);
        sb.AppendLine("      resourceType: virtualMachine");
        if (o.EnvironmentTags.Count == 1)
            sb.AppendLine($"      tags: {o.EnvironmentTags[0]}");
        else if (o.EnvironmentTags.Count > 1)
        {
            sb.AppendLine("      tags:");
            foreach (var t in o.EnvironmentTags) sb.AppendLine($"      - {t}");
        }

        sb.AppendLine("    strategy:");
        sb.AppendLine("      runOnce:");
        sb.AppendLine("        deploy:");
        sb.AppendLine("          steps:");
        sb.AppendLine("          - checkout: self");

        // Use .NET
        sb.AppendLine("          - task: UseDotNet@2");
        sb.AppendLine("            displayName: Setup .NET SDK");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              packageType: sdk");
        sb.AppendLine($"              version: {o.DotnetSdkVersion}");

        // Install CLI
        sb.AppendLine("          - task: Bash@3");
        sb.AppendLine("            displayName: Install SwarmBender CLI");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              targetType: inline");
        sb.AppendLine("              script: |");
        sb.AppendLine("                set -euo pipefail");
        sb.AppendLine("                export PATH=\"$PATH:$HOME/.dotnet/tools\"");
        sb.AppendLine("                dotnet tool install -g SwarmBender-Cli --prerelease || dotnet tool update -g SwarmBender-Cli --prerelease");
        sb.AppendLine("                sb -v");

        // PATH add
        sb.AppendLine("          - task: Bash@3");
        sb.AppendLine("            displayName: Add dotnet tools to PATH");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              targetType: inline");
        sb.AppendLine("              script: |");
        sb.AppendLine("                echo '##vso[task.prependpath]$HOME/.dotnet/tools'");

        // Login registry (assumes VG provided those vars)
        sb.AppendLine("          - task: Bash@3");
        sb.AppendLine("            displayName: Login to container registry");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              targetType: inline");
        sb.AppendLine("              script: |");
        sb.AppendLine("                set -euo pipefail");
        sb.AppendLine("                echo \"[+] Logging in to container registry\"");
        sb.AppendLine("                echo \"${REGISTRY_PASSWORD}\" | docker login ${REGISTRY_SERVER} -u \"${REGISTRY_USERNAME}\" --password-stdin");

        // Secrets sync
        sb.AppendLine("          - task: Bash@3");
        sb.AppendLine("            displayName: Sync Swarm secrets");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              targetType: inline");
        sb.AppendLine("              script: |");
        sb.AppendLine("                set -euo pipefail");
        sb.AppendLine("                ENV_LC=\"$(echo \"${{ parameters.environmentName }}\" | tr '[:upper:]' '[:lower:]')\"");
        if (isMultiTenant) sb.AppendLine("                TENANT_SLUG='${{ parameters.tenantSlug }}'");
        sb.AppendLine("                echo \"[+] Secrets sync for env=${ENV_LC}\"");
        sb.AppendLine($"               sb secret sync {stackId} -e \"${{ENV_LC}}\"");

        // Render (export extra params as env)
        sb.AppendLine("          - task: Bash@3");
        sb.AppendLine("            displayName: Render stack via SwarmBender");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              targetType: inline");
        sb.AppendLine("              script: |");
        sb.AppendLine("                set -euo pipefail");
        sb.AppendLine("                export COMPANY_NAME='${{ parameters.companyName }}'");
        sb.AppendLine("                ENV_LC='${{ lower(parameters.environmentName) }}'");
        if (isMultiTenant) sb.AppendLine("                export SB_TENANT_SLUG='${{ parameters.tenantSlug }}'");

        // export extra parameters as env
        foreach (var p in o.ExtraParameters.Where(x => x.ExportAsEnv))
        {
            var envName = string.IsNullOrWhiteSpace(p.EnvVarName) ? p.Name.ToUpperInvariant() : p.EnvVarName!;
            sb.AppendLine($"                export {envName}='${{{{ parameters.{p.Name} }}}}'");
        }

        sb.AppendLine($"                echo \"[+] Rendering stack '{stackId}' for env=${{ENV_LC}}\"");
        sb.AppendLine($"                sb render {stackId} -e \"${{ENV_LC}}\" --out-dir \"{o.RenderOutDir}\" --appsettings-mode \"{o.AppsettingsMode}\" {(o.WriteHistory ? "--write-history" : "")}".Trim());

        // Deploy
        sb.AppendLine("          - task: Bash@3");
        sb.AppendLine("            displayName: Deploy stack");
        sb.AppendLine("            inputs:");
        sb.AppendLine("              targetType: inline");
        sb.AppendLine("              script: |");
        sb.AppendLine("                set -euo pipefail");
        sb.AppendLine("                ENV_LC='${{ lower(parameters.environmentName) }}'");
        sb.AppendLine($"                STACK_FILE=\"{o.RenderOutDir}/{stackId}-\"${{ENV_LC}}\".stack.yml\"");
        sb.AppendLine($"                echo \"[+] Deploying: stack={stackId} file=${{STACK_FILE}}\"");
        sb.AppendLine($"                docker stack deploy --with-registry-auth --prune --resolve-image always -c \"${{STACK_FILE}}\" \"${{ parameters.companyName }}-{stackId}\"");

        return sb.ToString();
    }
}