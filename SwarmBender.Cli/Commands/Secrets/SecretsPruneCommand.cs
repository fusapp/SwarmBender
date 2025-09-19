using System.ComponentModel;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Cli.Commands.Secrets;

public sealed class SecretsPruneSettings : CommandSettings
{
    [CommandOption("-e|--env <ENV>")]
    [Description("Environment (e.g., dev|stg|prod).")]
    public required string Env { get; init; }

    [CommandOption("-t|--tenant <SCOPE>")]
    [Description("Scope/tenant (default: global).")]
    public string Scope { get; init; } = "global";

    [CommandOption("--retain <N>")]
    [Description("Number of recent versions to retain per base key (default: 2).")]
    public int Retain { get; init; } = 2;

    [CommandOption("--dry-run")]
    public bool DryRun { get; init; }

    [CommandOption("--quiet")]
    public bool Quiet { get; init; }

    [CommandOption("--root <PATH>")]
    public string Root { get; init; } = ".";
}

public sealed class SecretsPruneCommand : AsyncCommand<SecretsPruneSettings>
{
    private readonly ISecretsPruneService _svc;
    public SecretsPruneCommand(ISecretsPruneService svc) => _svc = svc;

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsPruneSettings s)
    {
        var n = await _svc.PruneAsync(s.Root, s.Env, s.Scope, s.Retain, s.DryRun, s.Quiet);
        return 0;
    }
}