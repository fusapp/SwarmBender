using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.Pipeline;

namespace SwarmBender.Cli.Commands;

public class ConfigExportCommand : AsyncCommand<ConfigExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")]
        [Description("Stack identifier (e.g., sso)")]
        public string StackId { get; init; } = default!;

        [CommandOption("-e|--env <ENV>")]
        [Description("Environment (default: dev)")]
        public string Env { get; init; } = "dev";

        [CommandOption("--root <PATH>")]
        [Description("Repository root (default: current directory)")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [CommandOption("-o|--out <FILE>")]
        [Description("Output appsettings.json path (optional). If omitted, written under render outDir.")]
        public string? OutFile { get; init; }

        [CommandOption("--no-history")]
        [Description("Do not write history artifacts")]
        public bool NoHistory { get; init; }
    }

    private readonly IRenderOrchestrator _orchestrator;
    private readonly IFileSystem _fs;

    public ConfigExportCommand(IRenderOrchestrator orchestrator, IFileSystem fs)
    {
        _orchestrator = orchestrator;
        _fs = fs;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var ct = CancellationToken.None;

        // RenderRequest: history’yi kapatma seçeneği
        var request = new RenderRequest
        (
            RootPath: s.Root,
            StackId: s.StackId,
            Env: s.Env,
            WriteHistory: !s.NoHistory,
            AppSettingsMode: "env",
            // OutDir: ConfigExportStage içinde kullanılacak. 
            // Eğer -o verilmişse, oranın klasörüne yazdırırız:
            OutDir: ResolveOutDir(s)
        );

        AnsiConsole.MarkupLine(
            $"[grey]Config export starting:[/] stack=[yellow]{s.StackId}[/], env=[yellow]{s.Env}[/], out=[yellow]{request.OutDir}[/]");

        // ConfigExport modunda çalıştır
        var result = await _orchestrator.RunAsync(request, PipelineMode.ConfigExport, ct);

        if (string.IsNullOrWhiteSpace(result.OutFile) || !_fs.FileExists(result.OutFile))
        {
            AnsiConsole.MarkupLine("[red]Export failed: no output file produced.[/]");
            return -1;
        }

        // -o/--out istenmişse sonradan yeniden adlandır/taşı
        if (!string.IsNullOrWhiteSpace(s.OutFile))
        {
            var desired = Path.GetFullPath(s.OutFile!);
            var desiredDir = Path.GetDirectoryName(desired)!;
            _fs.EnsureDirectory(desiredDir);

            // overwrite semantics: mevcutsa sil
            if (_fs.FileExists(desired))
                _fs.DeleteFile(desired);

            _fs.MoveFile(result.OutFile, desired);
            AnsiConsole.MarkupLine($"[green]Exported:[/] {desired}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Exported:[/] {result.OutFile}");
        }

        return 0;
    }

    private string ResolveOutDir(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.OutFile))
            return Path.Combine(s.Root, "ops", "state", "last"); // mevcut varsayımla uyumlu

        // -o verildiyse OutDir = hedef klasör
        var full = Path.GetFullPath(s.OutFile!);
        var dir = Path.GetDirectoryName(full);
        return string.IsNullOrWhiteSpace(dir) ? Directory.GetCurrentDirectory() : dir!;
    }
}