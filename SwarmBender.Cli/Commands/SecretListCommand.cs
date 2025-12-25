using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Util;

namespace SwarmBender.Cli.Commands;

public sealed class SecretListCommand : AsyncCommand<SecretListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")] public string StackId { get; init; } = default!;
        [CommandOption("-e|--env <ENV>")] public string Env { get; init; } = "dev";
        [CommandOption("--root <PATH>")]  public string Root { get; init; } = Directory.GetCurrentDirectory();
        [CommandOption("--show-values")]   public bool ShowValues { get; init; }

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

    public SecretListCommand(ISbConfigLoader cfgLoader, ISecretDiscovery discovery)
    { _cfgLoader = cfgLoader; _discovery = discovery; }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        try
        {
            var ct = CancellationToken.None;
            var validatedRoot = InputValidator.ValidateRootPath(s.Root);

            var cfg = await _cfgLoader.LoadAsync(validatedRoot, ct).ConfigureAwait(false);
            var found = await _discovery.DiscoverAsync(validatedRoot, s.StackId, s.Env, cfg, ct).ConfigureAwait(false);

            var table = new Table().AddColumns("externalName", "key", "version", s.ShowValues ? "value" : " ");
            foreach (var d in found.OrderBy(x => x.ExternalName, StringComparer.OrdinalIgnoreCase))
                table.AddRow(d.ExternalName, d.Key, d.Version, s.ShowValues ? d.Value : "");
            AnsiConsole.Write(table);
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