using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;

namespace SwarmBender.Cli.Commands;

/// <summary>
/// Lists all IRenderStage implementations discovered via DI, ordered by their Order.
/// Usage: sb doctor stage list
/// </summary>
public sealed class DoctorStageListCommand : Command<DoctorStageListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--namespace-filter <TEXT>")]
        public string? NamespaceFilter { get; init; }

        [CommandOption("--json")]
        public bool AsJson { get; init; }
    }

    private readonly IEnumerable<IRenderStage> _stages;

    public DoctorStageListCommand(IEnumerable<IRenderStage> stages)
    {
        _stages = stages;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        // materialize & sort
        var list = _stages
            .Select(s => new
            {
                s.Order,
                Type = s.GetType(),
                Name = s.GetType().Name,
                Ns = s.GetType().Namespace ?? "",
                Asm = s.GetType().Assembly.GetName().Name ?? "unknown"
            })
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // optional filter by namespace contains
        if (!string.IsNullOrWhiteSpace(settings.NamespaceFilter))
        {
            var f = settings.NamespaceFilter!;
            list = list.Where(x =>
                    x.Ns.Contains(f, StringComparison.OrdinalIgnoreCase)
                 || x.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                 || x.Asm.Contains(f, StringComparison.OrdinalIgnoreCase))
                 .ToList();
        }

        if (settings.AsJson)
        {
            // Lightweight JSON output for machine use
            var payload = list.Select(x => new
            {
                order = x.Order,
                name = x.Name,
                @namespace = x.Ns,
                assembly = x.Asm
            });
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            AnsiConsole.MarkupLine("[grey]// stage list[/]");
            AnsiConsole.WriteLine(json);
            return 0;
        }

        // Build a nice table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Order[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Stage[/]"));
        table.AddColumn(new TableColumn("[bold]Namespace[/]"));
        table.AddColumn(new TableColumn("[bold]Assembly[/]"));

        // find potential issues (duplicate orders)
        var dupOrders = list.GroupBy(x => x.Order).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        foreach (var x in list)
        {
            var orderCell = dupOrders.Contains(x.Order)
                ? $"[red]{x.Order}[/]"
                : $"[green]{x.Order}[/]";

            table.AddRow(orderCell, $"[white]{x.Name}[/]", $"[grey]{x.Ns}[/]", $"[grey]{x.Asm}[/]");
        }

        AnsiConsole.MarkupLine("[bold]Render pipeline stages (in execution order):[/]");
        AnsiConsole.Write(table);

        if (dupOrders.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning[/]: Duplicate [bold]Order[/] values detected: {string.Join(", ", dupOrders.OrderBy(i => i))}.");
        }

        // Small legend
        AnsiConsole.MarkupLine("[grey]Legend: green=unique order, red=duplicate order[/]");

        return 0;
    }
}