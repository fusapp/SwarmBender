using System.Text;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SwarmBender.Services.Azdo;

/// <summary>
/// Generates Azure DevOps pipeline YAML using YamlDotNet DOM for safe indentation and ordering.
/// </summary>
public sealed class AzdoPipelineGenerator : IAzdoPipelineGenerator
{
    public Task<AzdoPipelineResult> GenerateAsync(AzdoPipelineRequest r, CancellationToken ct = default)
    {
        var yaml = BuildYaml(r);
        // We only return the recommended relative out file; the CLI writes to disk.
        var outRel = Path.Combine(r.OutDir ?? "ops/pipelines/azdo", $"{r.StackId}.yml")
            .Replace('\\', '/');

        return Task.FromResult(new AzdoPipelineResult(outRel, yaml));
    }

    // ----------------------------- YAML BUILDERS -----------------------------

    private static string BuildYaml(AzdoPipelineRequest r)
    {
        // root mapping
        var root = new YamlMappingNode
        {
            // trigger: none
            { "trigger", new YamlScalarNode("none") },
            // parameters: [ environmentName ]
            { "parameters", BuildParameters(r) },
            // stages: [ single stage that deploys given stack ]
            { "stages", BuildStages(r) }
        };

        // Render YAML
        var stream = new YamlStream(new YamlDocument(root));
        using var sw = new StringWriter(new StringBuilder());
        // Emit with LF and no BOM
        stream.Save(sw, assignAnchors: false);
        return sw.ToString();
    }

    private static YamlSequenceNode BuildParameters(AzdoPipelineRequest r)
    {
        var values = new YamlSequenceNode();
        foreach (var v in r.Environments ?? new List<string> { "dev", "prod" })
            values.Add(new YamlScalarNode(v.ToUpperInvariant()));

        var param = new YamlMappingNode
        {
            { "name",         new YamlScalarNode("environmentName") },
            { "displayName",  new YamlScalarNode("Environment") },
            { "type",         new YamlScalarNode("string") },
            { "default",      new YamlScalarNode((r.DefaultEnv ?? (r.Environments?.FirstOrDefault() ?? "dev")).ToUpperInvariant()) },
            { "values",       values }
        };

        return new YamlSequenceNode(param);
    }

    private static YamlSequenceNode BuildStages(AzdoPipelineRequest r)
    {
        var stages = new YamlSequenceNode();

        var stageName = $"Deploy_{r.StackId}";
        var stage = new YamlMappingNode
        {
            { "stage",       new YamlScalarNode(stageName) },
            { "displayName", new YamlScalarNode($"Deploy {r.StackId} stack") },
            { "variables",   BuildVariablesSection(r) },
            { "jobs",        BuildJobs(r) }
        };

        stages.Add(stage);
        return stages;
    }
    
    
    // Installs .NET SDK so 'dotnet tool' is available
    private static YamlMappingNode BuildUseDotNetTask(string version = "9.0.x")
    {
        return new YamlMappingNode
        {
            { "task", new YamlScalarNode("UseDotNet@2") },
            { "displayName", new YamlScalarNode("Setup .NET SDK") },
            { "inputs", new YamlMappingNode
                {
                    { "packageType", new YamlScalarNode("sdk") },
                    { "version", new YamlScalarNode(string.IsNullOrWhiteSpace(version) ? "9.0.x" : version) }
                }
            }
        };
    }

// Installs SwarmBender CLI as a global tool and verifies it's available
    private static YamlMappingNode BuildInstallSwarmBenderTask(bool prerelease = true)
    {
        // NOTE: '##vso[task.prependpath]' affects subsequent tasks; to use 'sb' in this step,
        // we also export PATH locally.
        var flag = prerelease ? " --prerelease" : string.Empty;
        var script = string.Join('\n', new[]
        {
            "set -euo pipefail",
            "export PATH=\"$PATH:$HOME/.dotnet/tools\"",
            $"dotnet tool install -g SwarmBender-Cli{flag} || dotnet tool update -g SwarmBender-Cli{flag}",
            "sb --version"
        });

        return BuildBashTask(
            displayName: "Install SwarmBender CLI",
            script: script
        );
    }

// Makes $HOME/.dotnet/tools available to subsequent steps (agent-wide for the job)
    private static YamlMappingNode BuildPrependDotnetToolsToPathTask()
    {
        return BuildBashTask(
            displayName: "Add dotnet tools to PATH",
            script: "echo '##vso[task.prependpath]$HOME/.dotnet/tools'"
        );
    }
    
    private static YamlSequenceNode BuildVariablesSection(AzdoPipelineRequest r)
    {
        var seq = new YamlSequenceNode();

        // Variable groups
        switch (r.VarGroupsMode)
        {
            case VarGroupsMode.Prefix:
            {
                var prefix = (r.VarGroupsPrefix ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    var expr = $"${{{{ format('{prefix.ToUpperInvariant()}_{{0}}', parameters.environmentName) }}}}";
                    seq.Add(new YamlMappingNode { { "group", new YamlScalarNode(expr) } });
                }
                break;
            }
            case VarGroupsMode.FixedList:
            {
                var items = (r.VarGroupsFixedCsv ?? string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var g in items)
                    seq.Add(new YamlMappingNode { { "group", new YamlScalarNode(g) } });
                break;
            }
            default:
                break;
        }

        // Optional registry variables are not groups; they are provided by user secrets.
        // We don't inject plain variables here; groups are enough.

        return seq;
    }

    private static YamlSequenceNode BuildJobs(AzdoPipelineRequest r)
    {
        var jobs = new YamlSequenceNode();

        var deploymentName = $"Deploy_{r.StackId}";
        var envNameNode = BuildEnvironmentNameNode(r);

        var environmentMap = new YamlMappingNode
        {
            { "name",         envNameNode },
            { "resourceType", new YamlScalarNode("virtualMachine") }
            // Optional tags can be added later via models if needed.
        };

        var deploySteps = BuildDeploySteps(r);

        var runOnceMap = new YamlMappingNode
        {
            { "deploy", new YamlMappingNode { { "steps", deploySteps } } }
        };

        var strategyMap = new YamlMappingNode
        {
            { "runOnce", runOnceMap }
        };

        var job = new YamlMappingNode
        {
            { "deployment",  new YamlScalarNode(deploymentName) },
            { "displayName", new YamlScalarNode($"Deploy {r.StackId}") },
            { "environment", environmentMap },
            { "strategy",    strategyMap }
        };

        jobs.Add(job);
        return jobs;
    }

    private static YamlNode BuildEnvironmentNameNode(AzdoPipelineRequest r)
    {
        // prefix => PREFIX_{ENV}
        if (r.EnvStrategy == EnvNameStrategy.Prefix)
        {
            var prefix = (r.EnvName ?? "INFRA").Trim().ToUpperInvariant();
            var expr = $"${{{{ format('{prefix}_{{0}}', parameters.environmentName) }}}}";
            return new YamlScalarNode(expr);
        }

        // fixed => a constant name
        var fixedName = string.IsNullOrWhiteSpace(r.EnvName) ? "ProdInfra" : r.EnvName.Trim();
        return new YamlScalarNode(fixedName);
    }

    private static YamlSequenceNode BuildDeploySteps(AzdoPipelineRequest r)
    {
        var steps = new YamlSequenceNode();

        // - checkout: self
        steps.Add(new YamlMappingNode
        {
            { "checkout", new YamlScalarNode("self") }
        });
        
        // NEW: .NET SDK + SwarmBender CLI
        steps.Add(BuildUseDotNetTask("9.0.x"));              // UseDotNet@2
        steps.Add(BuildInstallSwarmBenderTask(prerelease: true)); // dotnet tool install/update SwarmBender-Cli
        steps.Add(BuildPrependDotnetToolsToPathTask());      // prepend tools path for subsequent steps
        

        // Optional registry login
        if (r.IncludeRegistryLogin)
        {
            steps.Add(BuildBashTask(
                displayName: "Login to container registry",
                script: BuildRegistryLoginScript(r)));
        }

        // Optional: secrets sync
        if (r.IncludeSecretsSync)
        {
            steps.Add(BuildBashTask(
                displayName: "Sync Swarm secrets",
                script: BuildSecretsSyncScript(r)));
        }

        // Render + Deploy
        steps.Add(BuildBashTask(
            displayName: "Render stack via SwarmBender",
            script: BuildRenderScript(r)));

        steps.Add(BuildBashTask(
            displayName: "Deploy stack",
            script: BuildDeployScript(r)));

        return steps;
    }

    private static YamlMappingNode BuildBashTask(string displayName, string script)
    {
        var inputs = new YamlMappingNode
        {
            { "targetType", new YamlScalarNode("inline") },
            { "script",     new YamlScalarNode(script) { Style = ScalarStyle.Literal } }
        };

        return new YamlMappingNode
        {
            { "task",        new YamlScalarNode("Bash@3") },
            { "displayName", new YamlScalarNode(displayName) },
            { "inputs",      inputs }
        };
    }

    // ------------------------------ SCRIPTS ---------------------------------

    private static string BuildRegistryLoginScript(AzdoPipelineRequest r)
    {
        // Variables are expected to come from variable groups or library
        var serverVar = string.IsNullOrWhiteSpace(r.RegistryServerVar) ? "REGISTRY_SERVER"   : r.RegistryServerVar;
        var userVar   = string.IsNullOrWhiteSpace(r.RegistryUserVar)   ? "REGISTRY_USERNAME" : r.RegistryUserVar;
        var passVar   = string.IsNullOrWhiteSpace(r.RegistryPassVar)   ? "REGISTRY_PASSWORD" : r.RegistryPassVar;

        return @$"
set -euo pipefail
echo ""[+] Logging in to container registry""
echo ""$${passVar}"" | docker login ""$${serverVar}"" -u ""$${userVar}"" --password-stdin
".TrimStart();
    }

    private static string BuildSecretsSyncScript(AzdoPipelineRequest r)
    {
        return @"
set -euo pipefail
ENV_LC=""$(echo ""${{ parameters.environmentName }}"" | tr '[:upper:]' '[:lower:]')""
echo ""[+] Secrets sync for env=${ENV_LC}""
sb secrets sync -e ""${ENV_LC}""
".TrimStart();
    }

    private static string BuildRenderScript(AzdoPipelineRequest r)
    {
        var outDir = string.IsNullOrWhiteSpace(r.RenderOutDir) ? "ops/state/last" : r.RenderOutDir;
        var writeHistoryFlag = r.WriteHistory ? " --write-history" : string.Empty;
        var appMode = string.IsNullOrWhiteSpace(r.AppSettingsMode) ? "env" : r.AppSettingsMode.Trim().ToLowerInvariant();

        return @$"
set -euo pipefail
ENV_LC=""$(echo ""${{ parameters.environmentName }}"" | tr '[:upper:]' '[:lower:]')""
echo ""[+] Rendering stack '{r.StackId}' for env=${{ENV_LC}}""
sb render {r.StackId} -e ""${{ENV_LC}}"" --out-dir ""{outDir}"" --appsettings-mode ""{appMode}""{writeHistoryFlag}
".TrimStart();
    }

    private static string BuildDeployScript(AzdoPipelineRequest r)
    {
        var outDir = string.IsNullOrWhiteSpace(r.RenderOutDir) ? "ops/state/last" : r.RenderOutDir;

        return @$"
set -euo pipefail
ENV_LC=""$(echo ""${{ parameters.environmentName }}"" | tr '[:upper:]' '[:lower:]')""
STACK_FILE=""{outDir}/{r.StackId}-""${{ENV_LC}}"".stack.yml""
echo ""[+] Deploying: stack={r.StackId} file=${{STACK_FILE}}""
docker stack deploy --with-registry-auth --prune --resolve-image always -c ""${{STACK_FILE}}"" ""{r.StackId}""
".TrimStart();
    }
}