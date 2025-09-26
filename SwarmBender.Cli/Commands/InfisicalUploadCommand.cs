using System.Text.RegularExpressions;
using Infisical.Sdk;
using Infisical.Sdk.Model;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Cli.Commands;

public sealed class InfisicalUploadCommand: AsyncCommand<InfisicalUploadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<STACK_ID>")]          public string StackId { get; init; } = default!;
        [CommandOption("-e|--env <ENV>")]           public string Env { get; init; } = "dev";
        [CommandOption("--root <PATH>")]            public string Root { get; init; } = Directory.GetCurrentDirectory();
        [CommandOption("--dry-run")]                public bool DryRun { get; init; }
        [CommandOption("--diff-only")]              public bool DiffOnly { get; init; }
        [CommandOption("--force")]                  public bool Force { get; init; }
        [CommandOption("--show-values")]            public bool ShowValues { get; init; }
        [CommandOption("--verbose")]                public bool Verbose { get; init; }
    }

    private readonly ISbConfigLoader _cfg;
    private readonly IInfisicalUploader _uploader;
    private readonly IOutput _out;

    public InfisicalUploadCommand(ISbConfigLoader cfg, IInfisicalUploader uploader, IOutput @out)
    { _cfg = cfg; _uploader = uploader; _out = @out; }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        var config = await _cfg.LoadAsync(s.Root, CancellationToken.None);
        var report = await _uploader.UploadAsync(
            s.Root, s.StackId, s.Env, config, force: s.Force, dryRun: s.DryRun, showValues: s.ShowValues, ct: CancellationToken.None);
        
        _out.Info(
            $"Infisical upload summary: " +
            $"created={report.Created}, " +
            $"updated={report.Updated}, " +
            $"skipped-same={report.SkippedSame}, " +
            $"skipped-filtered={report.SkippedFiltered}, " +
            $"skipped-missing-token={report.SkippedMissingToken}, " +
            $"skipped-other={report.SkippedOther}"
        );
        if (s.Verbose && report.Items is { Count: > 0 })
        {
            foreach (var it in report.Items.Where(i => i.Reason.StartsWith("error")))
                _out.Error($"{it.Key} -> {it.Reason}");
        }
        return 0;
    }
}