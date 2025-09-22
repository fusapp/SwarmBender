using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Cli.Commands.Utils.Azdo;

public sealed class AzdoPipelineInitCommand : AsyncCommand<AzdoPipelineInitCommand.Settings>
{
    private readonly IAzdoPipelineGenerator _generator;
    public AzdoPipelineInitCommand(IAzdoPipelineGenerator generator) => _generator = generator;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--stack <STACK_ID>")]
        [Description("Stack id (required).")]
        public string StackId { get; init; } = string.Empty;

        [CommandOption("--root <PATH>")]
        [Description("Project root (defaults to current directory).")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [CommandOption("--out-dir <DIR>")]
        [Description("Output directory for pipeline YAML (default: ops/pipelines/azdo).")]
        public string OutDir { get; init; } = "ops/pipelines/azdo";

        [CommandOption("--envs <CSV>")]
        [Description("Environment choices (comma separated). Default: dev,prod")]
        public string? EnvsCsv { get; init; }

        [CommandOption("--env-mode <fixed|prefix>")]
        [Description("Environment naming mode. 'fixed' uses a single environment name. 'prefix' creates NAME_{ENV}. Default: prefix")]
        public string EnvMode { get; init; } = "prefix";

        [CommandOption("--env-name <NAME>")]
        [Description("When env-mode=prefix, this is the prefix (e.g. INFRA). When env-mode=fixed, this is the fixed name (e.g. ProdInfra).")]
        public string EnvName { get; init; } = "INFRA";

        [CommandOption("--vg-mode <fixed|prefix>")]
        [Description("Variable groups mode. 'fixed' = CSV list, 'prefix' = PREFIX_{ENV}. Default: prefix")]
        public string VgMode { get; init; } = "prefix";

        [CommandOption("--vg-value <VALUE>")]
        [Description("When vg-mode=prefix this is the prefix; when vg-mode=fixed this is comma-separated variable group names.")]
        public string? VgValue { get; init; }

        [CommandOption("--include-secrets-sync")]
        [Description("Include 'sb secrets sync' step (default: true).")]
        [DefaultValue(true)]
        public bool IncludeSecretsSync { get; init; } = true;

        [CommandOption("--appsettings-mode <env|config>")]
        [Description("Render appsettings into env vars or config file (default: env).")]
        public string AppSettingsMode { get; init; } = "env";

        [CommandOption("--render-out <DIR>")]
        [Description("Render output directory (default: ops/state/last).")]
        public string RenderOut { get; init; } = "ops/state/last";

        [CommandOption("--write-history")]
        [Description("Write render history under ops/state/history (default: true).")]
        [DefaultValue(true)]
        public bool WriteHistory { get; init; } = true;

        [CommandOption("--with-registry")]
        [Description("Include container registry login step (default: false).")]
        public bool WithRegistry { get; init; } = false;

        [CommandOption("--reg-user-var <VAR>")]
        [Description("Registry username variable name (default: REGISTRY_USERNAME).")]
        public string RegUserVar { get; init; } = "REGISTRY_USERNAME";

        [CommandOption("--reg-pass-var <VAR>")]
        [Description("Registry password/secret variable name (default: REGISTRY_PASSWORD).")]
        public string RegPassVar { get; init; } = "REGISTRY_PASSWORD";

        [CommandOption("--reg-server-var <VAR>")]
        [Description("Registry server variable name (default: REGISTRY_SERVER).")]
        public string RegServerVar { get; init; } = "REGISTRY_SERVER";

        [CommandOption("--interactive")]
        [Description("Interactive wizard mode.")]
        public bool Interactive { get; init; } = false;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(StackId))
                return ValidationResult.Error("--stack is required");
            if (!string.Equals(EnvMode, "fixed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(EnvMode, "prefix", StringComparison.OrdinalIgnoreCase))
                return ValidationResult.Error("--env-mode must be 'fixed' or 'prefix'");
            if (!string.Equals(VgMode, "fixed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(VgMode, "prefix", StringComparison.OrdinalIgnoreCase))
                return ValidationResult.Error("--vg-mode must be 'fixed' or 'prefix'");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        // Working copies
        var envChoices = ParseCsvOrDefault(s.EnvsCsv, new[] { "dev", "prod" });
        var envModeStr = s.EnvMode;
        var envName    = s.EnvName;
        var vgModeStr  = s.VgMode;
        var vgValue    = s.VgValue;

        var withRegistry       = s.WithRegistry;
        var regServerVar       = s.RegServerVar;
        var regUserVar         = s.RegUserVar;
        var regPassVar         = s.RegPassVar;
        var includeSecretsSync = s.IncludeSecretsSync;
        var appSettingsMode    = s.AppSettingsMode;

        if (s.Interactive)
        {
            envModeStr = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Environment naming mode")
                    .AddChoices("prefix", "fixed")
                    .HighlightStyle(Style.Plain));

            envName = AnsiConsole.Prompt(
                new TextPrompt<string>(envModeStr == "prefix"
                        ? "Environment prefix (renders as PREFIX_{ENV}). Default: INFRA"
                        : "Fixed environment name (e.g., ProdInfra). Default: INFRA")
                    .DefaultValue(s.EnvName)
                    .AllowEmpty()).Trim();

            var csvDefault = s.EnvsCsv ?? "dev,prod";
            var envCsv = AnsiConsole.Prompt(
                new TextPrompt<string>($"Environment choices as CSV. Default: {csvDefault}")
                    .DefaultValue(csvDefault)
                    .AllowEmpty()).Trim();
            envChoices = ParseCsvOrDefault(envCsv, new[] { "dev", "prod" });

            vgModeStr = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Variable groups mode")
                    .AddChoices("prefix", "fixed")
                    .HighlightStyle(Style.Plain));

            vgValue = AnsiConsole.Prompt(
                new TextPrompt<string>(vgModeStr == "prefix"
                        ? "Variable group prefix (expands to PREFIX_{ENV}) (optional)"
                        : "Variable groups CSV (comma separated) (optional)")
                    .AllowEmpty()).Trim();

            withRegistry = AnsiConsole.Confirm("Include registry login step?", withRegistry);
            if (withRegistry)
            {
                regServerVar = AnsiConsole.Prompt(new TextPrompt<string>("Registry server variable name").DefaultValue(regServerVar).AllowEmpty()).Trim();
                regUserVar   = AnsiConsole.Prompt(new TextPrompt<string>("Registry username variable name").DefaultValue(regUserVar).AllowEmpty()).Trim();
                regPassVar   = AnsiConsole.Prompt(new TextPrompt<string>("Registry password/secret variable name").DefaultValue(regPassVar).AllowEmpty()).Trim();
            }

            includeSecretsSync = AnsiConsole.Confirm("Include 'sb secrets sync' step?", includeSecretsSync);

            appSettingsMode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("AppSettings mode")
                    .AddChoices("env", "config")
                    .HighlightStyle(Style.Plain));
        }

        var envStrategy = string.Equals(envModeStr, "fixed", StringComparison.OrdinalIgnoreCase)
            ? EnvNameStrategy.Fixed
            : EnvNameStrategy.Prefix;

        var vgMode = string.Equals(vgModeStr, "fixed", StringComparison.OrdinalIgnoreCase)
            ? VarGroupsMode.FixedList
            : VarGroupsMode.Prefix;

        var req = new AzdoPipelineRequest(
            RootPath: s.Root,
            StackId: s.StackId,
            OutDir: s.OutDir,
            Environments: envChoices,
            DefaultEnv: envChoices.First(),
            EnvStrategy: envStrategy,
            EnvName: envName,
            VarGroupsMode: vgMode,
            VarGroupsFixedCsv: vgMode == VarGroupsMode.FixedList ? (vgValue ?? string.Empty) : string.Empty,
            VarGroupsPrefix: vgMode == VarGroupsMode.Prefix ? (vgValue ?? string.Empty) : string.Empty,
            IncludeSecretsSync: includeSecretsSync,
            AppSettingsMode: appSettingsMode,
            RenderOutDir: s.RenderOut,
            WriteHistory: s.WriteHistory,
            IncludeRegistryLogin: withRegistry,
            RegistryServerVar: regServerVar,
            RegistryUserVar: regUserVar,
            RegistryPassVar: regPassVar
        );

        var result = await _generator.GenerateAsync(req, CancellationToken.None);
        var outPath = result.OutFile;

        // Normalize full path
        if (!Path.IsPathRooted(outPath))
            outPath = Path.Combine(s.Root, outPath);
        outPath = Path.GetFullPath(outPath);

        // Ensure directory
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write & verify
        await File.WriteAllTextAsync(outPath, result.Yaml ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!File.Exists(outPath))
            throw new IOException($"Pipeline write verification failed. File not found after write: {outPath}");

        AnsiConsole.MarkupLine("[green]Pipeline generated:[/] {0}", outPath);
        AnsiConsole.MarkupLine("Next steps:");
        AnsiConsole.MarkupLine("  1) Create a new Azure DevOps pipeline and point it to this YAML.");
        AnsiConsole.MarkupLine("  2) Define required variable groups and (optionally) registry variables.");
        AnsiConsole.MarkupLine("  3) Ensure the agent runs on your Swarm manager (Docker available).");
        return 0;
    }

    private static List<string> ParseCsvOrDefault(string? csv, IEnumerable<string> fallback)
        => !string.IsNullOrWhiteSpace(csv)
            ? csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : fallback.ToList();
}