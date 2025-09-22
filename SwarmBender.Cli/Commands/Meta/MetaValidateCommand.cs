using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Cli.Commands.Meta;

public sealed class MetaValidateCommand : AsyncCommand<MetaValidateCommand.Settings>
{
    private readonly IMetadataValidator _validator;
    public MetaValidateCommand(IMetadataValidator validator) => _validator = validator;

    public sealed class Settings : CommandSettings
    {
        [Description("Project root (defaults to current directory).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Print details per issue.")]
        [CommandOption("--details")]
        public bool Details { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var res = await _validator.ValidateAsync(s.Root, CancellationToken.None);

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Metadata Validation[/]");
        table.AddColumn("Errors");
        table.AddColumn("Warnings");
        table.AddRow(res.ErrorCount.ToString(), res.WarningCount.ToString());
        AnsiConsole.Write(table);

        if (s.Details && res.Issues.Count > 0)
        {
            AnsiConsole.WriteLine();
            var g = res.Issues.GroupBy(i => i.File);
            foreach (var fileGroup in g)
            {
                AnsiConsole.MarkupLine($"[bold underline]{fileGroup.Key}[/]");
                foreach (var issue in fileGroup)
                {
                    var badge = issue.Kind == MetaIssueKind.Error ? "[red]ERROR[/]" : "[yellow]WARN[/]";
                    var path  = string.IsNullOrWhiteSpace(issue.Path) ? "" : $" @{issue.Path}";
                    AnsiConsole.MarkupLine($"{badge} - {issue.Message}{path}");
                }
                AnsiConsole.WriteLine();
            }
        }

        return res.ErrorCount > 0 ? 2 : 0;
    }
}