using Spectre.Console.Cli;

namespace SwarmBender.Cli.Commands.Utils;

// marker branch (no Execute)
public sealed class UtilsCommand : Command<CommandSettings>
{
    public override int Execute(CommandContext context, CommandSettings settings) => 0;
}