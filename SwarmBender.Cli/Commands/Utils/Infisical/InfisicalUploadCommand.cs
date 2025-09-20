using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using SwarmBender.Services.Abstractions;
using System.Threading; // CancellationToken

namespace SwarmBender.Cli.Commands.Utils.Infisical;

public sealed class InfisicalUploadCommand : AsyncCommand<InfisicalUploadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--root <PATH>")]
        [Description("Project root (defaults to current directory).")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [CommandOption("-e|--env <ENV>")]
        [Description("Environment name (e.g., prod, dev).")]
        public string Env { get; init; } = "";

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Scope for path templating (e.g., global, sso).")]
        public string Scope { get; init; } = "global";

        [CommandOption("--from <APPSETTINGS_JSON>")]
        [Description("Path to appsettings JSON to upload.")]
        public string From { get; init; } = "";

        [CommandOption("--include <PATTERNS>")]
        [Description("Comma-separated wildcard patterns to include (overrides provider include).")]
        public string? Include { get; init; }

        [CommandOption("--dry-run")]
        [Description("Do not call Infisical. Just print what would be uploaded.")]
        public bool DryRun { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Env))
                return ValidationResult.Error("Missing --env");

            if (string.IsNullOrWhiteSpace(From))
                return ValidationResult.Error("Missing --from <appsettings.json>");

            if (!File.Exists(From))
                return ValidationResult.Error($"File not found: {From}");

            return ValidationResult.Success();
        }
    }

    private readonly IInfisicalClient _client;

    public InfisicalUploadCommand(IInfisicalClient client) => _client = client;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var root = Path.GetFullPath(s.Root);
        var from = Path.GetFullPath(s.From);

        // Load & flatten JSON
        Dictionary<string, string> flat;
        await using (var fs = File.OpenRead(from))
        {
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: CancellationToken.None);
            flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SwarmBender.Services.MergeHelpers.FlattenJson(doc.RootElement, flat);
        }

        // Optional include override
        var include = s.Include?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList() ?? new List<string>();

        var req = new InfisicalUploadRequest(
            RootPath: root,
            Env: s.Env,
            Scope: s.Scope,
            Items: flat,
            IncludeOverride: include,
            DryRun: s.DryRun
        );

        var res = await _client.UploadAsync(req, CancellationToken.None);

        // Render result table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Key");
        table.AddColumn("Infisical Key");
        table.AddColumn("Action");

        foreach (var i in res.Items)
            table.AddRow(i.FlatKey, i.InfisicalKey, i.Action);

        AnsiConsole.Write(table);

        if (res.DryRun)
            AnsiConsole.MarkupLine("[yellow]Dry-run completed. No changes were sent to Infisical.[/]");
        else
            AnsiConsole.MarkupLine("[green]Upload completed.[/]");

        return 0;
    }
}