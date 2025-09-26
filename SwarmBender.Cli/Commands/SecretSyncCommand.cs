using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Services;

namespace SwarmBender.Cli.Commands;

public sealed class SecretSyncCommand : AsyncCommand<SecretSyncCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")] public string StackId { get; init; } = default!;
        [CommandOption("-e|--env <ENV>")] public string Env { get; init; } = "dev";
        [CommandOption("--root <PATH>")] public string Root { get; init; } = Directory.GetCurrentDirectory();
        [CommandOption("--dry-run")] public bool DryRun { get; init; }
        [CommandOption("--prune-old")] public bool PruneOld { get; init; }
        [CommandOption("--show-values")] public bool ShowValues { get; init; }
    }

    private readonly ISbConfigLoader _cfgLoader;
    private readonly ISecretDiscovery _discovery;
    private readonly ISecretsEngineRunnerFactory _factory;

    public SecretSyncCommand(ISbConfigLoader cfgLoader, ISecretDiscovery discovery, ISecretsEngineRunnerFactory factory)
    {
        _cfgLoader = cfgLoader;
        _discovery = discovery;
        _factory = factory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var ct = CancellationToken.None;
        var cfg = await _cfgLoader.LoadAsync(s.Root, ct);
        var found = await _discovery.DiscoverAsync(s.Root, s.StackId, s.Env, cfg, ct);
        if (found.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No secrets matched.[/]");
            return 0;
        }

        var engine = _factory.Create(cfg.Secrets.Engine);
        var existing = await engine.ListAsync(ct);
        var labels = cfg.Secrets.Labels ?? new Dictionary<string, string>();

        foreach (var sec in found.OrderBy(x => x.ExternalName, StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Contains(sec.ExternalName)) continue;
            if (s.DryRun)
                AnsiConsole.MarkupLine(
                    $"[grey]create[/] {sec.ExternalName} {(s.ShowValues ? $"= [dim]{Markup.Escape(sec.Value)}[/]" : "")}");
            else
            {
                await engine.CreateAsync(sec.ExternalName, sec.Value, labels, ct);
                AnsiConsole.MarkupLine($"[green]created[/] {sec.ExternalName}");
            }
        }

        if (s.PruneOld)
        {
            var keep = new HashSet<string>(found.Select(f => f.ExternalName), StringComparer.OrdinalIgnoreCase);
            foreach (var name in existing)
            {
                // kaba ama işlevsel: repo/stack’a ait ve tutulmayacaksa sil
                if (name.StartsWith($"sb_{s.StackId}_", StringComparison.OrdinalIgnoreCase) && !keep.Contains(name))
                {
                    if (s.DryRun) AnsiConsole.MarkupLine($"[grey]prune[/] {name}");
                    else
                    {
                        await engine.RemoveAsync(name, ct);
                        AnsiConsole.MarkupLine($"[red]removed[/] {name}");
                    }
                }
            }
        }

        return 0;
    }
}