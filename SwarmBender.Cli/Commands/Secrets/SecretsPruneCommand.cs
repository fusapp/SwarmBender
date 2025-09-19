using System.ComponentModel;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Cli.Commands;

public sealed class SecretsPruneCommand
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-e|--env <ENV>")]
        [Description("Environment name (e.g., prod, dev).")]
        public string Environment { get; init; } = null!;

        [CommandOption("-t|--scope <SCOPE>")]
        [Description("Optional logical scope/tenant (e.g., global, sso).")]
        public string? Scope { get; init; }

        [CommandOption("-k|--keep <N>")]
        [Description("Keep latest N versions per key (default: 2).")]
        public int KeepLatest { get; init; } = 2;

        [CommandOption("--dry-run")]
        [Description("Do not delete anything; just show what would be removed.")]
        public bool DryRun { get; init; }

        [CommandOption("--report <PATH>")]
        [Description("Optional YAML report output path (e.g., ops/reports/prune.yml).")]
        public string? ReportPath { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Environment))
                return ValidationResult.Error("Environment is required. Use -e|--env <ENV>.");
            if (KeepLatest < 1)
                return ValidationResult.Error("KeepLatest must be >= 1.");
            return ValidationResult.Success();
        }
    }

    public sealed class Exec : AsyncCommand<Settings>
    {
        private readonly ISecretsPruneService _service;

        public Exec(ISecretsPruneService service) => _service = service;

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            var req = new SecretsPruneRequest(
                Scope: settings.Scope,
                Environment: settings.Environment,
                KeepLatest: settings.KeepLatest,
                DryRun: settings.DryRun,
                ReportPath: settings.ReportPath
            );

            SecretsPruneResult res;
            try
            {
                // NOTE: CommandContext doesn't expose a CancellationToken in many Spectre versions.
                res = await _service.PruneAsync(req, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] {0}", ex.Message);
                return -1;
            }

            var table = new Table().Centered();
            table.AddColumn("Kept");
            table.AddColumn("Removed");
            table.AddRow(
                string.Join("\n", res.Kept.Any() ? res.Kept : new[] { "(none)" }),
                string.Join("\n", res.Removed.Any() ? res.Removed : new[] { "(none)" })
            );
            AnsiConsole.Write(table);

            if (settings.DryRun)
                AnsiConsole.MarkupLine("[grey](dry-run: no secrets were deleted)[/]");
            if (!string.IsNullOrWhiteSpace(settings.ReportPath))
                AnsiConsole.MarkupLine("[grey]Report written to:[/] {0}", settings.ReportPath);

            return 0;
        }
    }
}