namespace SwarmBender.Cli.Commands.Utils.Azdo;

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

/// <summary>
/// sb utils azdo pipeline init --stack <id> -e dev,prod [--out ...] [--branch ...]
/// Generates a ready-to-run Azure DevOps pipeline for Swarm deploy.
/// </summary>
public sealed class AzdoPipelineInitCommand : AsyncCommand<AzdoPipelineInitCommand.Settings>
{
    private readonly ICiGenerator _gen;
    public AzdoPipelineInitCommand(ICiGenerator gen) => _gen = gen;

    public sealed class Settings : CommandSettings
    {
        [Description("Project root path (defaults to current directory).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Required stack ID to deploy (e.g., 'sso').")]
        [CommandOption("--stack <STACK_ID>")]
        public string StackId { get; init; } = "";

        [Description("Comma-separated environments (e.g., dev,prod).")]
        [CommandOption("-e|--envs <LIST>")]
        public string EnvsCsv { get; init; } = "";

        [Description("Output YAML path. Default: ops/ci-templates/azure/swarm-deploy.yml")]
        [CommandOption("--out <PATH>")]
        public string OutPath { get; init; } = "ops/ci-templates/azure/swarm-deploy.yml";

        [Description("Default branch to trigger from. Default: main")]
        [CommandOption("--branch <BRANCH>")]
        public string Branch { get; init; } = "main";

        [Description("Azure DevOps pool vmImage. Default: ubuntu-latest")]
        [CommandOption("--pool <VMIMAGE>")]
        public string PoolVmImage { get; init; } = "ubuntu-latest";

        [Description(".NET SDK version (UseDotNet@2). Default: 9.0.x")]
        [CommandOption("--dotnet <VER>")]
        public string DotnetSdk { get; init; } = "9.0.x";

        [Description("SwarmBender-Cli NuGet tool version. Default: 1.*")]
        [CommandOption("--sb-version <VER>")]
        public string SbVersion { get; init; } = "1.*";

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(StackId))
                return ValidationResult.Error("--stack is required.");

            if (string.IsNullOrWhiteSpace(EnvsCsv))
                return ValidationResult.Error("Provide at least one environment via -e/--envs (e.g., dev,prod).");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var envs = s.EnvsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var req = new CiGenRequest(
            RootPath: s.Root,
            Provider: "azdo",
            Kind: "swarm-deploy",
            StackId: s.StackId,
            Environments: envs,
            OutPath: s.OutPath,
            Branch: s.Branch,
            PoolVmImage: s.PoolVmImage,
            DotnetSdk: s.DotnetSdk,
            SbVersion: s.SbVersion
        );

        var res = await _gen.GenerateAsync(req, CancellationToken.None);

        AnsiConsole.MarkupLine("[green]Pipeline generated:[/] {0}", res.OutFile);
        AnsiConsole.MarkupLine("Stack: [cyan]{0}[/], Envs: [cyan]{1}[/]", s.StackId, string.Join(", ", envs));
        AnsiConsole.MarkupLine("Branch: {0} | Pool: {1}", s.Branch, s.PoolVmImage);
        AnsiConsole.MarkupLine(@"
[bold]Next steps:[/]
  1) In Azure DevOps, create a new pipeline and point it to this YAML file.
  2) Define variables if needed:
     - [cyan]REGISTRY_SERVER[/], [cyan]REGISTRY_USERNAME[/], [cyan]REGISTRY_PASSWORD[/] (optional; login step is conditional)
     - [cyan]DOCKER_HOST[/] if your agent talks to a remote Docker Swarm manager (e.g. tcp://host:2375).
  3) Ensure the agent has Docker CLI available and network access to the Swarm manager.");
        return 0;
    }
}