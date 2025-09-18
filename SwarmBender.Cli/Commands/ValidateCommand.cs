using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Cli.Commands;

/// <summary>
/// Validates one stack or all stacks in the repository, producing a machine-readable report.
/// Exit codes: 0 (ok), 2 (warnings only), 1 (errors), 4 (fatal).
/// </summary>
public sealed class ValidateCommand : AsyncCommand<ValidateCommand.Settings>
{
    private readonly IValidator _validator;

    public ValidateCommand(IValidator validator) => _validator = validator;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[STACK_ID]")]
        [Description("Optional stack id. If omitted, validates all stacks under 'stacks/'.")]
        public string? StackId { get; init; }

        [CommandOption("--path <DIR>")]
        [Description("Root directory (default: current directory).")]
        public string Path { get; init; } = ".";

        [CommandOption("-e|--env|--environments <ENV>")]
        [Description("Filter environments (comma-separated or multiple flags). If omitted, auto-detect under stacks/all/.")]
        public string[] Environments { get; init; } = Array.Empty<string>();

        [CommandOption("--out <FILE>")]
        [Description("Write a combined JSON report to this file (optional). Per-stack JSONs are saved under ops/reports/preflight/.")]
        public string? OutFile { get; init; }

        [CommandOption("--details")]
        [Description("Print detailed errors and warnings after the summary table.")]
        public bool Details { get; init; }

        [CommandOption("--quiet")]
        [Description("Suppress non-essential output.")]
        public bool Quiet { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        try
        {
            var req = new ValidateRequest(
                RootPath: s.Path,
                StackId: s.StackId,
                Environments: s.Environments,
                Quiet: s.Quiet,
                OutFile: s.OutFile);

            var result = await _validator.ValidateAsync(req);

            if (!s.Quiet)
            {
                var table = new Table().Centered();
                table.AddColumn("Stack");
                table.AddColumn("Errors");
                table.AddColumn("Warnings");

                foreach (var r in result.Stacks)
                    table.AddRow(r.StackId, r.Errors.Count.ToString(), r.Warnings.Count.ToString());

                AnsiConsole.Write(table);

                if (s.Details)
                {
                    foreach (var r in result.Stacks)
                    {
                        if (r.Errors.Count == 0 && r.Warnings.Count == 0)
                            continue;

                        AnsiConsole.Write(new Rule($"[bold]Stack: {Markup.Escape(r.StackId)}[/]").RuleStyle("grey").LeftJustified());

                        if (r.Errors.Count > 0)
                        {
                            AnsiConsole.MarkupLine("[red bold]Errors[/]");
                            int i = 1;
                            foreach (var e in r.Errors)
                            {
                                var file = string.IsNullOrWhiteSpace(e.File) ? "" : $" [grey]({Markup.Escape(e.File)})[/]";
                                var path = string.IsNullOrWhiteSpace(e.Path) ? "" : $" [grey]@{Markup.Escape(e.Path)}[/]";
                                AnsiConsole.MarkupLine($"[red]{i}.[/] [bold]{Markup.Escape(e.Code)}[/] - {Markup.Escape(e.Message)}{file}{path}");
                                i++;
                            }
                            AnsiConsole.WriteLine();
                        }

                        if (r.Warnings.Count > 0)
                        {
                            AnsiConsole.MarkupLine("[yellow bold]Warnings[/]");
                            int i = 1;
                            foreach (var w in r.Warnings)
                            {
                                var file = string.IsNullOrWhiteSpace(w.File) ? "" : $" [grey]({Markup.Escape(w.File)})[/]";
                                var path = string.IsNullOrWhiteSpace(w.Path) ? "" : $" [grey]@{Markup.Escape(w.Path)}[/]";
                                AnsiConsole.MarkupLine($"[yellow]{i}.[/] [bold]{Markup.Escape(w.Code)}[/] - {Markup.Escape(w.Message)}{file}{path}");
                                i++;
                            }
                            AnsiConsole.WriteLine();
                        }
                    }

                    AnsiConsole.MarkupLine("[grey]Details are also written to ops/reports/preflight/ as JSON per stack.[/]");
                }
            }

            var anyErrors = result.Stacks.Any(su => su.Errors.Count > 0);
            var anyWarnings = result.Stacks.Any(su => su.Warnings.Count > 0);

            if (anyErrors) return 1;
            if (anyWarnings) return 2;
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
