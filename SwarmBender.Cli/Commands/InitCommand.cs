using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SwarmBender.Cli.Commands;

/// <summary>Scaffold minimal folder layout.</summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Root path (defaults to cwd).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Environments CSV (default: dev,prod)")]
        [CommandOption("--env <CSV>")]
        public string Envs { get; init; } = "dev,prod";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var root = settings.Root;
        var envs = settings.Envs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Directory.CreateDirectory(Path.Combine(root, "stacks", "all"));
        Directory.CreateDirectory(Path.Combine(root, "ops", "state", "last"));
        Directory.CreateDirectory(Path.Combine(root, "ops", "state", "history"));
        Directory.CreateDirectory(Path.Combine(root, "ops", "vars", "private"));

        foreach (var env in envs)
        {
            Directory.CreateDirectory(Path.Combine(root, "stacks", "all", env, "env"));
            var stackDir = Path.Combine(root, "stacks", "all", env, "stack");
            Directory.CreateDirectory(stackDir);
            var globalYml = Path.Combine(stackDir, "global.yml");
            if (!File.Exists(globalYml))
                File.WriteAllText(globalYml, "# global overlays for this env\n");
        }

        // Minimal sb.yml
        var sbYml = Path.Combine(root, "ops", "sb.yml");
        if (!File.Exists(sbYml))
            File.WriteAllText(sbYml, """
version: 1
render:
  appsettingsMode: env
  outDir: ops/state/last
  writeHistory: true
tokens:
  user: {}
secretize:
  enabled: true
  paths: []
secrets:
  engine:
    type: docker-cli
    args: {}
providers:
  order:
    - type: file
    - type: env
    - type: azure-kv
    - type: infisical
""");

        AnsiConsole.MarkupLine("[green]Scaffold completed.[/]");
        return 0;
    }
}