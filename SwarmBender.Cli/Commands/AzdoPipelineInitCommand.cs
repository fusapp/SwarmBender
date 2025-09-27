// SwarmBender.Cli/Commands/AzdoPipelineInitCommand.cs
using System.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Azdo;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Cli.Commands;

public sealed class AzdoPipelineInitCommand : AsyncCommand<AzdoPipelineInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")] public string StackId { get; init; } = default!;
        [CommandOption("--root <PATH>")]   public string Root { get; init; } = Directory.GetCurrentDirectory();
        [CommandOption("--force")]         public bool Force { get; init; }
        [CommandOption("--non-interactive")] public bool NonInteractive { get; init; }
    }

    private readonly ISbConfigLoader _cfgLoader;
    private readonly IAzdoPipelineScaffolder _scaffolder;

    public AzdoPipelineInitCommand(ISbConfigLoader cfgLoader, IAzdoPipelineScaffolder scaffolder)
    { _cfgLoader = cfgLoader; _scaffolder = scaffolder; }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var cfg = await _cfgLoader.LoadAsync(s.Root, CancellationToken.None);

        var opts = s.NonInteractive
            ? BuildDefaults()
            : RunWizardSpectre(s.StackId, cfg);

        // Allow overwrite flag from CLI
        opts.Force = s.Force;

        try
        {
            await _scaffolder.GenerateAsync(s.Root, s.StackId, cfg, opts, CancellationToken.None);
            AnsiConsole.MarkupLine("[green]✔ Azure DevOps pipeline created successfully.[/]");
            return 0;
        }
        catch (IOException ioEx) when (!s.Force)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ioEx.Message)}[/]");
            AnsiConsole.MarkupLine("[grey]Hint: use [bold]--force[/] to overwrite.[/]");
            return 2;
        }
    }

    private static AzdoPipelineInitOptions BuildDefaults() => new()
    {
        DotnetSdkVersion = "9.0.x",
        RegistryVariableGroup = null,
        Trigger = new TriggerOptions { Mode = TriggerMode.None },
        VariableGroups = new List<VariableGroupSpec>
        {
            new() { Name = "REGISTRY", VariantByEnvironment = false },
            new() { Name = "STACK",    VariantByEnvironment = true, VariantByCompany = true },
        },
        Companies = new() { "fusapp" },
        ExtraParameters = new(),
        EnvironmentNamePrefix = "",
        EnvironmentTags = new() { "APP" },
        RenderOutDir = "ops/state/last",
        WriteHistory = true,
        AppsettingsMode = "env"
    };

    // --- Wizard (Spectre) ---
    private static AzdoPipelineInitOptions RunWizardSpectre(string stackId, SbConfig cfg)
    {
        AnsiConsole.Write(
            new FigletText("AZDO Pipeline")
                .Centered().Color(Color.Blue));

        AnsiConsole.MarkupLine($"[grey]Stack:[/] [bold]{stackId}[/]");

        var opts = BuildDefaults();

        // Trigger mode
        var trig = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Trigger mode?")
                .AddChoices("none", "ci", "manual"));
        opts.Trigger = trig switch
        {
            "ci" => new TriggerOptions
            {
                Mode = TriggerMode.CI,
                CiIncludeBranches = AskCsv("CI include branches", new[] { "main" }),
                CiExcludeBranches = AskCsv("CI exclude branches", Array.Empty<string>()),
                PrEnabled = AnsiConsole.Confirm("Enable PR triggers?", false)
            },
            "manual" => new TriggerOptions { Mode = TriggerMode.ManualOnly },
            _        => new TriggerOptions { Mode = TriggerMode.None }
        };

        // Registry variable group
        if (AnsiConsole.Confirm("Add a registry variable group?", false))
            opts.RegistryVariableGroup = AnsiConsole.Ask<string>("Registry variable group name:", "");

        // Environment resource prefix
        opts.EnvironmentNamePrefix = AnsiConsole.Ask<string>("Environment name prefix (empty for none):", "");

        // Tags
        opts.EnvironmentTags = AskCsv("Environment tags", new[] { "APP" });

        // Companies
        opts.Companies = AskCsv("Companies", new[] { "fusapp" });

        // Variable Groups (loop)
        AnsiConsole.MarkupLine("[grey]Add variable groups (leave name empty to finish)...[/]");
        var vgs = new List<VariableGroupSpec>();
        while (true)
        {
            var name = AnsiConsole.Ask<string>("Variable group [bold]name[/] (empty to stop):", "");
            if (string.IsNullOrWhiteSpace(name)) break;

            var byEnv     = AnsiConsole.Confirm("Variant by [bold]Environment[/]?", true);
            var byTenant  = AnsiConsole.Confirm("Variant by [bold]Tenant[/]?", false);
            var byCompany = AnsiConsole.Confirm("Variant by [bold]Company[/]?", false);

            vgs.Add(new VariableGroupSpec
            {
                Name = name,
                VariantByEnvironment = byEnv,
                VariantByTenant = byTenant,
                VariantByCompany = byCompany
            });
        }
        if (vgs.Count > 0) opts.VariableGroups = vgs;

        // Extra parameters (loop)
        AnsiConsole.MarkupLine("[grey]Add extra pipeline parameters (leave name empty to finish)...[/]");
        var extras = new List<PipelineParameterSpec>();
        while (true)
        {
            var name = AnsiConsole.Ask<string>("Param [bold]name[/] (empty to stop):", "");
            if (string.IsNullOrWhiteSpace(name)) break;

            var display = AnsiConsole.Ask<string>("Display name:", name);
            var type = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Type?")
                    .AddChoices("string", "boolean"));
            var def = AnsiConsole.Ask<string>("Default (optional):", "");
            var values = AskCsv("Enum values (optional)", Array.Empty<string>());
            var export = AnsiConsole.Confirm("Export as env before render?", true);
            string? envVar = null;
            if (export)
            {
                envVar = AnsiConsole.Ask<string>($"Env var name (default: {name.ToUpperInvariant()}):", "");
                if (string.IsNullOrWhiteSpace(envVar)) envVar = name.ToUpperInvariant();
            }

            extras.Add(new PipelineParameterSpec
            {
                Name = name,
                DisplayName = display,
                Type = type,
                Default = string.IsNullOrWhiteSpace(def) ? null : def,
                Values = values,
                ExportAsEnv = export,
                EnvVarName = envVar
            });
        }
        if (extras.Count > 0) opts.ExtraParameters = extras;

        // .NET SDK version
        opts.DotnetSdkVersion = AnsiConsole.Ask<string>("Dotnet SDK version:", opts.DotnetSdkVersion);

        // Render settings
        opts.RenderOutDir   = AnsiConsole.Ask<string>("Render out dir:", opts.RenderOutDir);
        opts.AppsettingsMode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Appsettings mode?")
                .AddChoices("env", "config"));
        opts.WriteHistory = AnsiConsole.Confirm("Write render history?", opts.WriteHistory);

        // Summary
        AnsiConsole.MarkupLine("[grey]Wizard complete. Generating pipeline...[/]");
        return opts;
    }

    // --- helpers ---
    private static List<string> AskCsv(string title, IEnumerable<string> defaults)
    {
        var defStr   = string.Join(',', defaults);
        var escaped  = Markup.Escape(defStr);           // içeriği kaçır
        var literal  = $"[[{escaped}]]";                // köşeli parantezleri literal yap

        var prompt = new TextPrompt<string>($"{title} (comma-separated) {literal}:")
            .DefaultValue(defStr)
            .AllowEmpty();

        var input = AnsiConsole.Prompt(prompt);
        var items = string.IsNullOrWhiteSpace(input)
            ? defaults
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.ToList();
    }

    private static bool AskYesNo(string question, bool dflt)
    {
        var defLabel = dflt ? "Y/n" : "y/N";
        var literal  = $"[[{Markup.Escape(defLabel)}]]";

        var prompt = new TextPrompt<string>($"{question} {literal}:")
            .DefaultValue("")
            .AllowEmpty();

        var s = AnsiConsole.Prompt(prompt).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return dflt;
        return s is "y" or "yes" or "true";
    }
}