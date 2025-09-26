using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;

namespace SwarmBender.Cli.Commands;

public sealed class SecretDiffCommand : AsyncCommand<SecretDiffCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")] public string StackId { get; init; } = default!;
        [CommandOption("-e|--env <ENV>")] public string Env { get; init; } = "dev";
        [CommandOption("--root <PATH>")] public string Root { get; init; } = Directory.GetCurrentDirectory();
        [CommandOption("--show-matches")] public bool ShowMatches { get; init; }
    }

    private readonly ISbConfigLoader _cfgLoader;
    private readonly ISecretDiscovery _discovery;
    private readonly ISecretsEngineRunnerFactory _factory;

    public SecretDiffCommand(ISbConfigLoader cfgLoader, ISecretDiscovery discovery, ISecretsEngineRunnerFactory factory)
    {
        _cfgLoader = cfgLoader;
        _discovery = discovery;
        _factory = factory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var ct = CancellationToken.None;
        var cfg = await _cfgLoader.LoadAsync(s.Root, ct);
        var desired = await _discovery.DiscoverAsync(s.Root, s.StackId, s.Env, cfg, ct);

        var engine = _factory.Create(cfg.Secrets.Engine);
        var existing = await engine.ListAsync(ct);

        var desiredSet = new HashSet<string>(desired.Select(d => d.ExternalName), StringComparer.OrdinalIgnoreCase);
        var existingSet = existing;

        string stackPrefix = $"sb_{s.StackId}_";
        string envMarker = $"_{s.Env}_";

        // sadece bu stack+env’e bakan mevcut set
        var existingFiltered = existingSet
            .Where(n => n.StartsWith(stackPrefix, StringComparison.OrdinalIgnoreCase)
                        && n.Contains(envMarker, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toCreate = desiredSet.Except(existingFiltered, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var toPrune = existingFiltered.Except(desiredSet, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var matches = desiredSet.Intersect(existingFiltered, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // çıktı
        if (toCreate.Count == 0 && toPrune.Count == 0 && !s.ShowMatches)
        {
            AnsiConsole.MarkupLine("[green]Diff: her şey güncel görünüyor.[/]");
            return 0;
        }

        if (toCreate.Count > 0)
        {
            var panel = new Panel(new Rows(toCreate.Select(x => new Markup($"[green]+ create[/] {Markup.Escape(x)}"))))
                .Header("Create")
                .Border(BoxBorder.Rounded);
            AnsiConsole.Write(panel);
        }

        if (toPrune.Count > 0)
        {
            var panel = new Panel(new Rows(toPrune.Select(x => new Markup($"[red]- prune[/] {Markup.Escape(x)}"))))
                .Header("Prune")
                .Border(BoxBorder.Rounded);
            AnsiConsole.Write(panel);
        }

        if (s.ShowMatches && matches.Count > 0)
        {
            var panel = new Panel(new Rows(matches.Select(x => new Markup($"[grey]= match[/] {Markup.Escape(x)}"))))
                .Header("Match")
                .Border(BoxBorder.Rounded);
            AnsiConsole.Write(panel);
        }

        return 0;
    }
}