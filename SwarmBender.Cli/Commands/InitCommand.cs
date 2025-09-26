using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Cli.Commands;

/// <summary>Scaffolds repo layout and (optionally) a specific stack.</summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Optional stack id to scaffold (e.g., 'sso'). If omitted, only repo layout is created.")]
        [CommandArgument(0, "[STACK_ID]")]
        public string? StackId { get; init; }

        [Description("Root path (defaults to cwd).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Environments CSV used for overlays (default: dev,prod)")]
        [CommandOption("-e|--env <CSV>")]
        public string Envs { get; init; } = "dev,prod";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var root = settings.Root;
        var envs = settings.Envs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 1) Base repo layout
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

        // 2) ops/sb.yml (complete config via YamlDotNet)
        var sbYmlPath = Path.Combine(root, "ops", "sb.yml");
        if (!File.Exists(sbYmlPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sbYmlPath)!);
            var cfg = CreateDefaultConfig();
            var serializer = new SerializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();
            File.WriteAllText(sbYmlPath, serializer.Serialize(cfg));
        }

        // 3) Optional: scaffold a specific stack (template + per-env overlays)
        if (!string.IsNullOrWhiteSpace(settings.StackId))
        {
            var stackId = settings.StackId!;
            var stackRoot = Path.Combine(root, "stacks", stackId);
            Directory.CreateDirectory(stackRoot);

            // Template file (docker-stack.template.yml) — don’t overwrite if exists
            var tplYml = Path.Combine(stackRoot, "docker-stack.template.yml");
            var tplYaml = Path.Combine(stackRoot, "docker-stack.template.yaml");
            if (!File.Exists(tplYml) && !File.Exists(tplYaml))
            {
                File.WriteAllText(tplYml,
                    @"services:
  api:
    image: alpine:3.20
    command: [""sh"", ""-c"", ""while true; do echo hello; sleep 30; done""]
    deploy:
      labels:
        traefik.enable: ""true""
");
            }

            // Per-env overlay folders: stacks/{stackId}/{env}/stack
            foreach (var env in envs)
            {
                Directory.CreateDirectory(Path.Combine(stackRoot, env, "env"));
                var stackDir = Path.Combine(stackRoot, env, "stack");
                Directory.CreateDirectory(stackDir);

                var overlay = Path.Combine(stackDir, "svc.yml");
                if (!File.Exists(overlay))
                {
                    File.WriteAllText(overlay,
                        $@"services:
  api:
    logging:
      driver: json-file
    labels:
      com.example.env: ""{env}""
");
                }
            }

            AnsiConsole.MarkupLine($"[green]Stack scaffolded:[/] [bold]{stackId}[/]");
        }

        AnsiConsole.MarkupLine("[green]Scaffold completed.[/]");
        return 0;
    }

    private static SbConfig CreateDefaultConfig() => new SbConfig
    {
        Version = 1,
        Render = new RenderSection
        {
            AppsettingsMode = "env",
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
            User = new()
            {
                ["COMPANY_NAME"] = "fusapp",
                ["ENVIRONMENT_RESOURCENAME"] = "contabo"
            }
        },
        Secretize = new SecretizeSection
        {
            Enabled = true,
            Paths = new() { "ConnectionStrings.*", "Redis__*", "Mongo__*" }
        },
        Secrets = new SecretsSection
        {
            Engine = new SecretsEngine
            {
                Type = "docker-cli",
                Args = new SecretsEngineArgs
                {
                    DockerPath = "docker",
                    DockerHost = "unix:///var/run/docker.sock"
                }
            },
            NameTemplate = "sb_{scope}_{env}_{key}_{version}",
            VersionMode = "content-sha",
            Labels = new() { ["owner"] = "swarmbender" }
        },
        Providers = new ProvidersSection
        {
            Order = new()
            {
                new ProviderOrderItem { Type = "file" },
                new ProviderOrderItem { Type = "env" },
                new ProviderOrderItem { Type = "azure-kv" },
                new ProviderOrderItem { Type = "infisical" }
            },
            File = new ProvidersFile
            {
                ExtraJsonDirs = new()
            },
            Env = new ProvidersEnv
            {
                AllowlistFileSearch = new()
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
                Replace = new() { ["__"] = "--" }
            },
            Infisical = new ProvidersInfisical
            {
                Enabled = true,
                BaseUrl = "https://app.infisical.com",
                WorkspaceId = "",
                EnvMap = new() { ["dev"] = "dev", ["prod"] = "prod" },
                PathTemplate = "/{scope}",
                KeyTemplate = "{key}",
                Replace = new() { ["__"] = "_" },
                Include = new() { "ConnectionStrings__*", "Redis__*", "Mongo__*" }
            }
        },
        Metadata = new MetadataSection
        {
            Groups = new()
            {
                new MetadataGroup { Id = "web", Description = "Web & Edge workloads" },
                new MetadataGroup { Id = "data", Description = "Data services" },
                new MetadataGroup { Id = "background", Description = "Workers / schedulers" }
            },
            Tenants = new()
            {
                new MetadataTenant { Id = "tenant-a", Slug = "ta", Groups = new() { "web", "data" } },
                new MetadataTenant { Id = "tenant-b", Slug = "tb", Groups = new() { "web", "background" } }
            }
        },
        Schema = new SchemaSection
        {
            Required = new() { "render.outDir", "providers.order" },
            Enums = new() { ["render.appsettingsMode"] = new() { "env", "config" } }
        }
    };
}