using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Cli.Commands;

/// <summary>
/// Initializes the root scaffold (no stack-id) or a single stack (with stack-id).
/// Delegates all logic to IInitExecutor (DI). Async version.
/// </summary>
public sealed class InitCommand : AsyncCommand<InitCommand.Settings>
{
    private readonly IInitExecutor _exec;

    public InitCommand(IInitExecutor exec) => _exec = exec;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[STACK_ID]")]
        [Description("Optional stack id. If omitted, initializes the root scaffold.")]
        public string? StackId { get; init; }

        [CommandOption("-e|--env|--environments <ENV>")]
        [Description("Environment names (comma-separated or multiple flags). Default: dev,prod")]
        public string[] Environments { get; init; } = new[] { "dev", "prod" };

        [CommandOption("--path <DIR>")]
        [Description("Root directory to initialize (default: current directory).")]
        public string Path { get; init; } = ".";

        [CommandOption("--no-global-defs")]
        [Description("Do not create global baseline stubs under stacks/all/<env>.")]
        public bool NoGlobalDefs { get; init; }

        [CommandOption("--no-defs")]
        [Description("Stack mode: do not create stack-level secrets.yml/configs.yml stubs.")]
        public bool NoDefs { get; init; }

        [CommandOption("--no-aliases")]
        [Description("Stack mode: do not create aliases.yml stub.")]
        public bool NoAliases { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be created without writing files.")]
        public bool DryRun { get; init; }

        [CommandOption("--quiet")]
        [Description("Suppress non-essential output.")]
        public bool Quiet { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var request = new InitRequest(
            StackId: s.StackId,
            EnvNames: s.Environments,
            RootPath: s.Path,
            NoGlobalDefs: s.NoGlobalDefs,
            NoDefs: s.NoDefs,
            NoAliases: s.NoAliases,
            DryRun: s.DryRun,
            Quiet: s.Quiet);

        var result = await _exec.ExecuteAsync(request);

        if (!s.Quiet)
        {
            if (result.InvalidEnvs.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]Invalid environment name(s):[/] {0}", string.Join(", ", result.InvalidEnvs));
                AnsiConsole.MarkupLine("Allowed pattern: [grey]^[a-z0-9][-_.a-z0-9]*$[/]");
            }

            AnsiConsole.MarkupLine("[green]Done[/]. Created: {0}, Skipped: {1}",
                result.CreatedCount, result.SkippedCount);

            if (s.DryRun)
                AnsiConsole.MarkupLine("[yellow](dry-run: no files written)[/]");
        }

        if (result.InvalidEnvs.Count > 0)
            return 3;

        return result.CreatedCount > 0 && result.SkippedCount > 0 ? 2 : 0;
    }
}
