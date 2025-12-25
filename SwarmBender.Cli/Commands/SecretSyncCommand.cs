using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Services;
using SwarmBender.Core.Util;

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

        public override ValidationResult Validate()
        {
            try
            {
                InputValidator.ValidateStackId(StackId);
                InputValidator.ValidateEnvironment(Env);
                InputValidator.ValidateRootPath(Root);
                return ValidationResult.Success();
            }
            catch (ArgumentException ex)
            {
                return ValidationResult.Error(ex.Message);
            }
        }
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
        try
        {
            var ct = CancellationToken.None;
            var validatedRoot = InputValidator.ValidateRootPath(s.Root);

            var cfg = await _cfgLoader.LoadAsync(validatedRoot, ct).ConfigureAwait(false);
            var found = await _discovery.DiscoverAsync(validatedRoot, s.StackId, s.Env, cfg, ct).ConfigureAwait(false);

            if (found.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No secrets matched.[/]");
                return 0;
            }

            var engine = _factory.Create(cfg.Secrets.Engine);
            var existing = await engine.ListAsync(ct).ConfigureAwait(false);
            var labels = cfg.Secrets.Labels ?? new Dictionary<string, string>();

            foreach (var sec in found.OrderBy(x => x.ExternalName, StringComparer.OrdinalIgnoreCase))
            {
                if (existing.Contains(sec.ExternalName)) continue;
                if (string.IsNullOrEmpty(sec.Value))
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] skip empty secret: {sec.ExternalName} (key={sec.Key})");
                    continue;
                }
                if (s.DryRun)
                    AnsiConsole.MarkupLine(
                        $"[grey]create[/] {sec.ExternalName} {(s.ShowValues ? $"= [dim]{Markup.Escape(sec.Value)}[/]" : "")}");
                else
                {
                    await engine.CreateAsync(sec.ExternalName, sec.Value, labels, ct).ConfigureAwait(false);
                    AnsiConsole.MarkupLine($"[green]created[/] {sec.ExternalName}");
                }
            }

            if (s.PruneOld)
            {
                var keep = new HashSet<string>(found.Select(f => f.ExternalName), StringComparer.OrdinalIgnoreCase);
                foreach (var name in existing)
                {
                    // kaba ama işlevsel: repo/stack'a ait ve tutulmayacaksa sil
                    if (name.StartsWith($"sb_{s.StackId}_", StringComparison.OrdinalIgnoreCase) && !keep.Contains(name))
                    {
                        if (s.DryRun) AnsiConsole.MarkupLine($"[grey]prune[/] {name}");
                        else
                        {
                            await engine.RemoveAsync(name, ct).ConfigureAwait(false);
                            AnsiConsole.MarkupLine($"[red]removed[/] {name}");
                        }
                    }
                }
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine("[red]Validation error:[/] {0}", Markup.Escape(ex.Message));
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
            return 1;
        }
    }
}