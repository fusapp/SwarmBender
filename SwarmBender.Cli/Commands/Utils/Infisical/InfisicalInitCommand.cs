using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Cli.Commands.Utils.Infisical;

/// <summary>
/// Interactive wizard to create/update ops/providers/infisical.yml.
/// </summary>
public sealed class InfisicalInitCommand : AsyncCommand<InfisicalInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Project root path (defaults to current directory)")]
        [CommandOption("--root <PATH>")]
        public string? Root { get; init; }

        [Description("Custom config path (defaults to ops/vars/providers/infisical.yml)")]
        [CommandOption("--config <PATH>")]
        public string? ConfigPath { get; init; }

        [Description("Overwrite without asking")]
        [CommandOption("--yes")]
        public bool Yes { get; init; }
    }

    private readonly IInfisicalConfigWizard _wizard;

    public InfisicalInitCommand(IInfisicalConfigWizard wizard) => _wizard = wizard;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(settings.Root)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(settings.Root);

            var written = await _wizard.RunAsync(root, settings.ConfigPath, settings.Yes, CancellationToken.None);
            AnsiConsole.MarkupLine($"[green]Done.[/] {written}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return -1;
        }
    }
}