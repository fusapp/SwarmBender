using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Cli.Commands.Secrets;

public sealed class SecretsRotateCommand : AsyncCommand<SecretsRotateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-e|--env <ENV>")]
        [Description("Environment name (e.g., prod, dev)")]
        public string Env { get; set; } = "";

        [CommandOption("--scope <SCOPE>")]
        [Description("Rotation scope: global | stack | service (default: global)")]
        public string Scope { get; set; } = "global";

        [CommandOption("--stack <STACK_ID>")]
        [Description("Stack id (required if scope=stack or scope=service)")]
        public string? StackId { get; set; }

        [CommandOption("--service <NAME>")]
        [Description("Service name (required if scope=service)")]
        public string? Service { get; set; }

        [CommandOption("-k|--key <KEY>")]
        [Description("Key to rotate (repeatable)")]
        public string[] Keys { get; set; } = Array.Empty<string>();

        [CommandOption("--match <GLOB_OR_REGEX>")]
        [Description("Match keys in the provided JSON. Glob (*,?) or /regex/.")]
        public string? Match { get; set; }

        [CommandOption("--values <PATH>")]
        [Description("JSON file with { \"KEY\": \"value\" }")]
        public string? ValuesJsonPath { get; set; }

        [CommandOption("--stdin")]
        [Description("Read values JSON from STDIN")]
        public bool ReadStdin { get; set; }

        [CommandOption("--version-mode <MODE>")]
        [Description("Version mode: content-sha | timestamp | serial (default: content-sha)")]
        public string VersionMode { get; set; } = "content-sha";

        [CommandOption("--keep <N>")]
        [Description("Keep at most N old versions on engine (default: 0)")]
        public int Keep { get; set; } = 0;

        [CommandOption("--root <PATH>")]
        [Description("Project root path (defaults to current working directory)")]
        public string? Root { get; set; }

        [CommandOption("--dry-run")]
        [Description("Do not create or delete anything; just preview")]
        public bool DryRun { get; set; }

        [CommandOption("-q|--quiet")]
        [Description("Suppress console output")]
        public bool Quiet { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Env))
                return ValidationResult.Error("Missing --env.");
            var scope = (Scope ?? "global").ToLowerInvariant();
            if (scope is "stack" or "service" && string.IsNullOrWhiteSpace(StackId))
                return ValidationResult.Error("When --scope is stack or service, --stack must be provided.");
            if (scope is "service" && string.IsNullOrWhiteSpace(Service))
                return ValidationResult.Error("When --scope is service, --service must be provided.");
            if (!ReadStdin && string.IsNullOrWhiteSpace(ValuesJsonPath))
                return ValidationResult.Error("Provide --values <file> or --stdin for values JSON.");
            return ValidationResult.Success();
        }
    }

    private readonly ISecretsRotateService _service;

    public SecretsRotateCommand(ISecretsRotateService service) => _service = service;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var root = string.IsNullOrWhiteSpace(s.Root) ? Environment.CurrentDirectory : s.Root;
        var req = new SecretsRotateRequest(
            RootPath: root,
            Env: s.Env,
            Scope: s.Scope,
            StackId: s.StackId,
            ServiceName: s.Service,
            Keys: s.Keys,
            Match: s.Match,
            ValuesJsonPath: s.ValuesJsonPath,
            ReadValuesFromStdin: s.ReadStdin,
            VersionMode: s.VersionMode,
            Keep: Math.Max(0, s.Keep),
            DryRun: s.DryRun,
            Quiet: s.Quiet
        );

        try
        {
            await _service.RotateAsync(req);
            return 0;
        }
        catch (Exception ex)
        {
            if (!s.Quiet) AnsiConsole.MarkupLine("[red]ERROR:[/] {0}", ex.Message);
            return -1;
        }
    }
}