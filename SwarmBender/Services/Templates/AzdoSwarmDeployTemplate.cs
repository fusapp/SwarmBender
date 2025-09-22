namespace SwarmBender.Services.Templates;

using SwarmBender.Services.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Builds Azure DevOps YAML for Swarm deploy using YamlDotNet with a clean object graph.
/// </summary>
public sealed class AzdoSwarmDeployTemplate
{
    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance) // keep original keys
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public string Render(AzdoSwarmDeployModel m)
    {
        // Azure DevOps root mapping
        var root = new Dictionary<string, object?>
        {
            ["name"] = $"Swarm Deploy - {m.StackId}",
            ["trigger"] = new Dictionary<string, object?>
            {
                ["branches"] = new Dictionary<string, object?>
                {
                    ["include"] = new List<string> { m.Branch }
                }
            },
            ["variables"] = new List<object?>
            {
                Var("SB_VERSION", m.SbVersion),
                Var(m.VarDockerHost, ""),       // may be blank; if blank Docker CLI talks to local daemon
                Var(m.VarRegistryServer, ""),
                Var(m.VarRegistryUser,   ""),
                Var(m.VarRegistryPass,   "")
            },
            ["pool"] = new Dictionary<string, object?>
            {
                ["vmImage"] = m.PoolVmImage
            },
            ["jobs"] = new List<object?>
            {
                BuildDeployJob(m)
            }
        };

        return Yaml.Serialize(root);
    }

    private static Dictionary<string, object?> Var(string name, string value)
        => new()
        {
            ["name"] = name,
            ["value"] = value
        };

    private static object BuildDeployJob(AzdoSwarmDeployModel m)
    {
        // strategy.matrix: each env becomes a matrix entry with ENV variable
        var matrix = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in m.Envs)
        {
            matrix[EnvKey(env)] = new Dictionary<string, object?> { ["ENV"] = env };
        }

        return new Dictionary<string, object?>
        {
            ["job"] = "deploy",
            ["displayName"] = "Deploy to Docker Swarm",
            ["strategy"] = new Dictionary<string, object?>
            {
                ["matrix"] = matrix
            },
            ["steps"] = BuildSteps(m)
        };
    }

    private static string EnvKey(string env) => env.Replace('-', '_');

    private static List<object?> BuildSteps(AzdoSwarmDeployModel m)
    {
        var steps = new List<object?>();

        // - checkout: self
        steps.Add(new Dictionary<string, object?>
        {
            ["checkout"] = "self"
        });

        // - task: UseDotNet@2
        steps.Add(new Dictionary<string, object?>
        {
            ["task"] = "UseDotNet@2",
            ["inputs"] = new Dictionary<string, object?>
            {
                ["packageType"] = "sdk",
                ["version"] = m.DotnetSdk
            },
            ["displayName"] = $"Use .NET SDK {m.DotnetSdk}"
        });

        // Install sb dotnet tool
        steps.Add(new Dictionary<string, object?>
        {
            ["script"] =
                "echo \"##vso[task.prependpath]$HOME/.dotnet/tools\"\n" +
                "dotnet tool install --global SwarmBender-Cli --version $(SB_VERSION)\n" +
                "sb --version",
            ["displayName"] = "Install SwarmBender CLI"
        });

        // Docker login (conditional)
        steps.Add(new Dictionary<string, object?>
        {
            ["script"] =
                "echo \"Logging in to container registry\"\n" +
                "echo \"$(REGISTRY_PASSWORD)\" | docker login \"$(REGISTRY_SERVER)\" --username \"$(REGISTRY_USERNAME)\" --password-stdin",
            ["displayName"] = "Docker login (conditional)",
            ["condition"] =
                "and(succeeded(), " +
                "ne(variables['REGISTRY_SERVER'], ''), " +
                "ne(variables['REGISTRY_USERNAME'], ''), " +
                "ne(variables['REGISTRY_PASSWORD'], ''))",
            ["env"] = new Dictionary<string, object?>
            {
                ["DOCKER_HOST"] = $"$({nameof(m.VarDockerHost)})".Replace(nameof(m.VarDockerHost), m.VarDockerHost)
            }
        });

        // sb render
        steps.Add(new Dictionary<string, object?>
        {
            ["script"] =
                $"sb render {m.StackId} -e $(ENV) --out ops/state/last --quiet\n" +
                "echo \"Rendered: ops/state/last/\"",
            ["displayName"] = "Render stack (sb render)",
            ["env"] = new Dictionary<string, object?>
            {
                ["DOCKER_HOST"] = $"$({nameof(m.VarDockerHost)})".Replace(nameof(m.VarDockerHost), m.VarDockerHost)
            }
        });

        // docker stack deploy
        steps.Add(new Dictionary<string, object?>
        {
            ["script"] =
                $"docker stack deploy -c ops/state/last/{m.StackId}-$(ENV).stack.yml {m.StackId}-$(ENV)",
            ["displayName"] = "Deploy to Swarm (docker stack deploy)",
            ["env"] = new Dictionary<string, object?>
            {
                ["DOCKER_HOST"] = $"$({nameof(m.VarDockerHost)})".Replace(nameof(m.VarDockerHost), m.VarDockerHost)
            }
        });

        return steps;
    }
}