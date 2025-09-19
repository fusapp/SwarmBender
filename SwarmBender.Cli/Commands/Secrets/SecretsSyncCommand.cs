using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Cli.Commands.Secrets;

public sealed class SecretsSyncSettings : CommandSettings
{
    [CommandOption("-e|--env <ENV>")]
    [Description("Environment (e.g., dev|stg|prod).")]
    public required string Env { get; init; }

    [CommandOption("-t|--tenant <SCOPE>")]
    [Description("Scope/tenant (default: global).")]
    public string Scope { get; init; } = "global";

    [CommandOption("-s|--stack <STACK_ID>")]
    [Description("Optional stack id (for future filtering).")]
    public string? StackId { get; init; }

    [CommandOption("--service <NAME>")]
    [Description("Optional service filter(s); repeatable.")]
    public string[] Services { get; init; } = System.Array.Empty<string>();

    [CommandOption("--version-mode <MODE>")]
    [Description("Override version mode (kv-version|content-sha|hmac).")]
    public string? VersionMode { get; init; }

    [CommandOption("--dry-run")]
    [Description("Do not create anything; only preview and write nothing.")]
    public bool DryRun { get; init; }

    [CommandOption("--quiet")]
    [Description("Suppress verbose output.")]
    public bool Quiet { get; init; }

    [CommandOption("--root <PATH>")]
    [Description("Root path (defaults to current directory).")]
    public string Root { get; init; } = ".";
}

public sealed class SecretsSyncCommand : AsyncCommand<SecretsSyncSettings>
{
    private readonly ISecretsSyncService _svc;
    public SecretsSyncCommand(ISecretsSyncService svc) => _svc = svc;

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsSyncSettings s)
    {
        var req = new SecretsSyncRequest
        {
            RootPath = s.Root,
            Env = s.Env,
            Scope = s.Scope,
            StackId = s.StackId,
            Services = s.Services,
            VersionModeOverride = s.VersionMode,
            DryRun = s.DryRun,
            Quiet = s.Quiet
        };

        var res = await _svc.SyncAsync(req);

        if (!s.Quiet)
        {
            var table = new Table().RoundedBorder().AddColumn("Key").AddColumn("Secret Name");
            foreach (var e in res.Entries)
            {
                var parts = e.Split(':', 2);
                table.AddRow($"[grey]{parts[0]}[/]", $"[white]{parts[1].Trim()}[/]");
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\n[green]Created[/]: {res.Created}  [grey]Skipped[/]: {res.Skipped}");
            AnsiConsole.MarkupLine($"Map: [cyan]{res.MapPath}[/]");
        }
        return 0;
    }
}