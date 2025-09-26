using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;

namespace SwarmBender.Cli.Commands;

public sealed class SecretPruneCommand : AsyncCommand<SecretPruneCommand.Settings>
    {
        public sealed class Settings : CommandSettings
        {
            [CommandArgument(0, "<STACK_ID>")] public string StackId { get; init; } = default!;
            [CommandOption("-e|--env <ENV>")] public string Env { get; init; } = "dev";
            [CommandOption("--root <PATH>")]  public string Root { get; init; } = Directory.GetCurrentDirectory();
            [CommandOption("--dry-run")]      public bool DryRun { get; init; }
        }

        private readonly ISbConfigLoader _cfgLoader;
        private readonly ISecretDiscovery _discovery;
        private readonly ISecretsEngineRunnerFactory _factory;

        public SecretPruneCommand(ISbConfigLoader cfgLoader, ISecretDiscovery discovery, ISecretsEngineRunnerFactory factory)
        { _cfgLoader = cfgLoader; _discovery = discovery; _factory = factory; }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
        {
            var ct = CancellationToken.None;
            var cfg = await _cfgLoader.LoadAsync(s.Root, ct);
            var desired = await _discovery.DiscoverAsync(s.Root, s.StackId, s.Env, cfg, ct);

            var engine   = _factory.Create(cfg.Secrets.Engine);
            var existing = await engine.ListAsync(ct);

            var keep = new HashSet<string>(desired.Select(d => d.ExternalName), StringComparer.OrdinalIgnoreCase);

            // yalnızca bu stack+env’e ait olanları hedefle
            string stackPrefix = $"sb_{s.StackId}_";
            string envMarker   = $"_{s.Env}_";

            var victims = existing
                .Where(name => name.StartsWith(stackPrefix, StringComparison.OrdinalIgnoreCase)
                               && name.Contains(envMarker, StringComparison.OrdinalIgnoreCase)
                               && !keep.Contains(name))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (victims.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]Prune: temizlenecek eski secret yok.[/]");
                return 0;
            }

            foreach (var name in victims)
            {
                if (s.DryRun) AnsiConsole.MarkupLine($"[grey]prune[/] {name}");
                else
                {
                    await engine.RemoveAsync(name, ct);
                    AnsiConsole.MarkupLine($"[red]removed[/] {name}");
                }
            }

            return 0;
        }
    }