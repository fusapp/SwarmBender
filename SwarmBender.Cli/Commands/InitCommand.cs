using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Cli.Commands;

/// <summary>Scaffolds minimal repo layout and writes a complete ops/sb.yml via YamlDotNet.</summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Root path (defaults to cwd).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Environments CSV (default: dev,prod)")]
        [CommandOption("--env <CSV>")]
        public string Envs { get; init; } = "dev,prod";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var root = settings.Root;
        var envs = settings.Envs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 1) Scaffold folders
        Directory.CreateDirectory(Path.Combine(root, "stacks", "all"));
        Directory.CreateDirectory(Path.Combine(root, "ops", "state", "last"));
        Directory.CreateDirectory(Path.Combine(root, "ops", "state", "history"));
        Directory.CreateDirectory(Path.Combine(root, "ops", "vars", "private"));

        foreach (var env in envs)
        {
            Directory.CreateDirectory(Path.Combine(root, "stacks", "all", env, "env"));
            var stackDir = Path.Combine(root, "stacks", "all", env, "stack");
            Directory.CreateDirectory(stackDir);

            var globalYml = Path.Combine(stackDir, "global.yml");
            if (!File.Exists(globalYml))
                File.WriteAllText(globalYml, "# global overlays for this env\n");
        }

        // 2) Prepare a complete SbConfig instance (aligned to our contract)
        var cfg = new SbConfig
        {
            Version = 1,
            Render = new RenderSection
            {
                AppsettingsMode = "env",                 // "env" | "config"
                OutDir = "ops/state/last",
                WriteHistory = true,
                OverlayOrder = new()
                {
                    "stacks/all/{env}/stack/*.y?(a)ml",
                    "stacks/{stackId}/{env}/stack/*.y?(a)ml"
                }
            },
            Tokens = new TokensSection
            {
                // implicit SB_* tokens are documented by the engine; user can add more here
                User = new Dictionary<string, string>
                {
                    ["COMPANY_NAME"] = "fusapp",
                    ["ENVIRONMENT_RESOURCENAME"] = "contabo"
                }
            },
            Secretize = new SecretizeSection
            {
                Enabled = true,
                Paths = new List<string>
                {
                    "ConnectionStrings.*",
                    "Redis__*",
                    "Mongo__*"
                }
            },
            Secrets = new SecretsSection
            {
                Engine = new SecretsEngine
                {
                    Type = "docker-cli", // docker-cli | docker-dotnet
                    Args = new SecretsEngineArgs
                    {
                        DockerPath = "docker",
                        DockerHost = "unix:///var/run/docker.sock"
                    }
                },
                NameTemplate = "sb_{scope}_{env}_{key}_{version}",
                VersionMode = "content-sha",
                Labels = new Dictionary<string, string>
                {
                    ["owner"] = "swarmbender"
                }
            },
            Providers = new ProvidersSection
            {
                Order = new List<ProviderOrderItem>
                {
                    new() { Type = "file" },
                    new() { Type = "env" },
                    new() { Type = "azure-kv" },
                    new() { Type = "infisical" }
                },
                File = new ProvidersFile
                {
                    ExtraJsonDirs = new List<string>() // user can add extra env json dirs later
                },
                Env = new ProvidersEnv
                {
                    AllowlistFileSearch = new List<string>
                    {
                        "stacks/{stackId}/use-envvars.json",
                        "stacks/all/use-envvars.json"
                    }
                },
                AzureKv = new ProvidersAzureKv
                {
                    Enabled = true,
                    VaultUrl = "https://YOUR-VAULT-NAME.vault.azure.net/",
                    KeyTemplate = "{key}",
                    Replace = new Dictionary<string, string> { ["__"] = "--" }
                },
                Infisical = new ProvidersInfisical
                {
                    Enabled = true,
                    BaseUrl = "https://app.infisical.com",
                    WorkspaceId = "", // or project/workspace slugs
                    EnvMap = new Dictionary<string, string> { ["dev"] = "dev", ["prod"] = "prod" },
                    PathTemplate = "/{scope}",
                    KeyTemplate = "{key}",
                    Replace = new Dictionary<string, string> { ["__"] = "_" },
                    Include = new List<string> { "ConnectionStrings__*", "Redis__*", "Mongo__*" }
                }
            },
            Metadata = new MetadataSection
            {
                Groups = new List<MetadataGroup>
                {
                    new() { Id = "web",        Description = "Web & Edge workloads" },
                    new() { Id = "data",       Description = "Data services" },
                    new() { Id = "background", Description = "Workers / schedulers" },
                },
                Tenants = new List<MetadataTenant>
                {
                    new() { Id = "tenant-a", Slug = "ta", Groups = new() { "web", "data" } },
                    new() { Id = "tenant-b", Slug = "tb", Groups = new() { "web", "background" } }
                }
            },
            Schema = new SchemaSection
            {
                Required = new List<string>
                {
                    "render.outDir",
                    "providers.order"
                },
                Enums = new Dictionary<string, List<string>>
                {
                    ["render.appsettingsMode"] = new() { "env", "config" }
                }
            }
        };

        // 3) Serialize to YAML (respects [YamlMember] aliases on SbConfig)
        var sbYmlPath = Path.Combine(root, "ops", "sb.yml");
        if (!File.Exists(sbYmlPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sbYmlPath)!);

            var serializer = new SerializerBuilder()
                // We keep default naming; aliases on model control field names.
                .WithNamingConvention(NullNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull) // cleaner yaml
                .Build();

            var yaml = serializer.Serialize(cfg);
            File.WriteAllText(sbYmlPath, yaml);
        }

        AnsiConsole.MarkupLine("[green]Scaffold completed.[/]");
        return 0;
    }
}