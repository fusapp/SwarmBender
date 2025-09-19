using System.ComponentModel;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Cli.Commands;

public sealed class SecretsDoctorCommand
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-e|--env <ENV>")]
        [Description("Environment name (e.g., prod, dev).")]
        public string Environment { get; init; } = null!;

        [CommandOption("--map <PATH>")]
        [Description("Optional secrets map path (defaults to ops/vars/secrets-map.<env>.yml).")]
        public string? MapPath { get; init; }

        [CommandOption("--root <PATH>")]
        [Description("Project root path (defaults to current working directory).")]
        public string? RootPath { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Environment))
                return ValidationResult.Error("Environment is required. Use -e|--env <ENV>.");
            return ValidationResult.Success();
        }
    }

    public sealed class Exec : AsyncCommand<Settings>
    {
        private readonly ISecretsDoctorService _service;

        public Exec(ISecretsDoctorService service) => _service = service;

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            var root = string.IsNullOrWhiteSpace(settings.RootPath)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(settings.RootPath);

            SecretsDoctorResult res;
            try
            {
                var req = new SecretsDoctorRequest(root, settings.Environment, settings.MapPath);
                // NOTE: CommandContext doesn't expose a CancellationToken in many Spectre versions.
                res = await _service.CheckAsync(req, CancellationToken.None);
            }
            catch (FileNotFoundException fnf)
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] {0}", fnf.Message);
                AnsiConsole.MarkupLine("[grey]Path:[/] {0}", fnf.FileName ?? "(unknown)");
                return -1;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] {0}", ex.Message);
                return -1;
            }

            var summary = new Table().Centered();
            summary.AddColumn("Map");
            summary.AddColumn("Missing on Engine");
            summary.AddColumn("Orphaned on Engine");
            summary.AddRow(
                res.MapPath,
                res.MissingOnEngine.Count.ToString(),
                res.OrphanedOnEngine.Count.ToString()
            );
            AnsiConsole.Write(summary);

            if (res.MissingOnEngine.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Missing on Engine[/]:");
                AnsiConsole.Write(new Panel(string.Join('\n', res.MissingOnEngine)) { Border = BoxBorder.Rounded });
            }

            if (res.OrphanedOnEngine.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Orphaned on Engine[/]:");
                AnsiConsole.Write(new Panel(string.Join('\n', res.OrphanedOnEngine)) { Border = BoxBorder.Rounded });
            }

            if (res.MultiVersions.Any())
            {
                var mv = new Table().RoundedBorder();
                mv.Title("Multiple Versions for Same Key");
                mv.AddColumn("Key");
                mv.AddColumn("Names");
                foreach (var kv in res.MultiVersions)
                    mv.AddRow(kv.Key, string.Join('\n', kv.Value));
                AnsiConsole.Write(mv);
            }

            if (!res.MissingOnEngine.Any() && !res.OrphanedOnEngine.Any() && !res.MultiVersions.Any())
                AnsiConsole.MarkupLine("[green]Doctor OK[/] — no inconsistencies detected.");

            return 0;
        }
    }
}