using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Cli.Commands;

/// <summary>
/// Renders final stack.yml by merging template + baselines + service overrides.
/// </summary>
public sealed class RenderCommand : AsyncCommand<RenderCommand.Settings>
{
    private readonly IRenderExecutor _executor;

    public RenderCommand(IRenderExecutor executor) => _executor = executor;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")]
        [Description("Stack id under stacks/<STACK_ID>.")]
        public string StackId { get; init; } = default!;

        [CommandOption("-e|--env|--environments <ENV>")]
        [Description("Environment names (comma-separated or multiple flags).")]
        public string[] Environments { get; init; } = Array.Empty<string>();

        [CommandOption("--out-dir <DIR>")]
        [Description("Output directory (default: ops/state/last).")]
        public string OutDir { get; init; } = "ops/state/last";

        [CommandOption("--no-history")]
        [Description("Do not write a history snapshot under ops/state/history.")]
        public bool NoHistory { get; init; }

        [CommandOption("--preview")]
        [Description("Print rendered YAML to stdout.")]
        public bool Preview { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show actions without writing files.")]
        public bool DryRun { get; init; }

        [CommandOption("--path <DIR>")]
        [Description("Root directory (default: current directory).")]
        public string Path { get; init; } = ".";

        [CommandOption("--quiet")]
        [Description("Suppress non-essential output.")]
        public bool Quiet { get; init; }

        [CommandOption("--appsettings-mode <env|config>")]
        [Description("How to consume appsettings*.json: 'env' (flatten to env vars) or 'config' (generate swarm config & mount). Default: env.")]
        public string AppSettingsMode { get; init; } = "env";

        [CommandOption("--appsettings-target <PATH>")]
        [Description("When --appsettings-mode=config, mount target path inside the container (default: /app/appsettings.json).")]
        public string AppSettingsTarget { get; init; } = "/app/appsettings.json";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var envs = s.Environments.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                 .Select(x => x.ToLowerInvariant())
                                 .Distinct()
                                 .ToArray();

        if (envs.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]No environments specified.[/] Use -e dev,prod or multiple -e flags.");
            return 2;
        }

        var mode = (s.AppSettingsMode ?? "env").ToLowerInvariant();
        if (mode != "env" && mode != "config")
        {
            AnsiConsole.MarkupLine("[red]Invalid --appsettings-mode.[/] Use 'env' or 'config'.");
            return 2;
        }

        try
        {
            var req = new RenderRequest(
                RootPath: s.Path,
                StackId: s.StackId,
                Environments: envs,
                OutDir: s.OutDir,
                WriteHistory: !s.NoHistory,
                Preview: s.Preview,
                DryRun: s.DryRun,
                Quiet: s.Quiet,
                AppSettingsMode: mode,
                AppSettingsTarget: s.AppSettingsTarget);

            var result = await _executor.RenderAsync(req);

            if (!s.Quiet)
            {
                var table = new Table().Centered();
                table.AddColumn("Environment");
                table.AddColumn("Output");
                foreach (var o in result.Outputs)
                    table.AddRow(o.Environment, o.OutputPath);
                AnsiConsole.Write(table);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (!s.Quiet)
                AnsiConsole.MarkupLine("[red]ERROR:[/] {0}", ex.Message);
            return 4;
        }
    }
}
