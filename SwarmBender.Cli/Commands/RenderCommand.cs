using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Cli.Commands;

public sealed class RenderCommand : AsyncCommand<RenderCommand.Settings>
{
    private readonly IRenderOrchestrator _orch;

    public RenderCommand(IRenderOrchestrator orch) => _orch = orch;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")]
        public string StackId { get; init; } = default!;

        [Description("Environment (e.g., dev, prod).")]
        [CommandOption("-e|--env <ENV>")]
        public string Env { get; init; } = "dev";

        [Description("Root path (defaults to cwd).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Out dir (default ops/state/last).")]
        [CommandOption("--out-dir <DIR>")]
        public string OutDir { get; init; } = "ops/state/last";

        [Description("Write history (default true).")]
        [CommandOption("--write-history")]
        public bool WriteHistory { get; init; } = true;

        [Description("Appsettings mode: env|config (default env).")]
        [CommandOption("--appsettings-mode <MODE>")]
        public string AppSettingsMode { get; init; } = "env";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var req = new RenderRequest(
            RootPath: s.Root,
            StackId: s.StackId,
            Env: s.Env,
            AppSettingsMode: s.AppSettingsMode,
            OutDir: s.OutDir,
            WriteHistory: s.WriteHistory
        );

        var res = await _orch.RunAsync(req, CancellationToken.None);
        AnsiConsole.MarkupLine("[green]Rendered.[/] Output: {0}", res.OutFile);
        return 0;
    }
}