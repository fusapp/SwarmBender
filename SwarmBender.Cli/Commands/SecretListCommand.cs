using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;

namespace SwarmBender.Cli.Commands;

public sealed class SecretListCommand : AsyncCommand<SecretListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")] public string StackId { get; init; } = default!;
        [CommandOption("-e|--env <ENV>")] public string Env { get; init; } = "dev";
        [CommandOption("--root <PATH>")]  public string Root { get; init; } = Directory.GetCurrentDirectory();
        [CommandOption("--show-values")]   public bool ShowValues { get; init; }
    }

    private readonly ISbConfigLoader _cfgLoader;
    private readonly ISecretDiscovery _discovery;

    public SecretListCommand(ISbConfigLoader cfgLoader, ISecretDiscovery discovery)
    { _cfgLoader = cfgLoader; _discovery = discovery; }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var cfg = await _cfgLoader.LoadAsync(s.Root, CancellationToken.None);
        var found = await _discovery.DiscoverAsync(s.Root, s.StackId, s.Env, cfg, CancellationToken.None);

        var table = new Table().AddColumns("externalName","key","version", s.ShowValues ? "value" : " ");
        foreach (var d in found.OrderBy(x => x.ExternalName, StringComparer.OrdinalIgnoreCase))
            table.AddRow(d.ExternalName, d.Key, d.Version, s.ShowValues ? d.Value : "");
        AnsiConsole.Write(table);
        return 0;
    }
}