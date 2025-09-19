using System.ComponentModel;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Cli.Commands.Secrets;

public sealed class SecretsDoctorSettings : CommandSettings
{
    [CommandOption("-e|--env <ENV>")]
    [Description("Environment (e.g., dev|stg|prod).")]
    public required string Env { get; init; }

    [CommandOption("-t|--tenant <SCOPE>")]
    [Description("Scope/tenant (default: global).")]
    public string Scope { get; init; } = "global";

    [CommandOption("--quiet")]
    public bool Quiet { get; init; }

    [CommandOption("--root <PATH>")]
    public string Root { get; init; } = ".";
}

public sealed class SecretsDoctorCommand : AsyncCommand<SecretsDoctorSettings>
{
    private readonly ISecretsDoctorService _svc;
    public SecretsDoctorCommand(ISecretsDoctorService svc) => _svc = svc;

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsDoctorSettings s)
    {
        var code = await _svc.DiagnoseAsync(s.Root, s.Env, s.Scope, s.Quiet);
        return code;
    }
}