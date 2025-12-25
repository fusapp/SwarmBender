using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Pipeline;

/// <summary>Wires stages (Decorator/Pipeline). Order is important.</summary>
public sealed class RenderOrchestrator : IRenderOrchestrator
{
    private readonly IEnumerable<IRenderStage> _stages;
    private readonly IFileSystem _fs;
    private readonly IYamlEngine _yaml;
    private readonly ISbConfigLoader _configLoader;
    private readonly ILogger<RenderOrchestrator> _logger;

    public RenderOrchestrator(
        IEnumerable<IRenderStage> stages,
        IFileSystem fs,
        IYamlEngine yaml,
        ISbConfigLoader configLoader,
        ILogger<RenderOrchestrator> logger)
    {
        _stages = stages.OrderBy(x => x.Order).ToArray();
        _fs = fs;
        _yaml = yaml;
        _configLoader = configLoader;
        _logger = logger;
    }

    public async Task<RenderResult> RunAsync(RenderRequest request, PipelineMode mode, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Starting render pipeline for stack={StackId}, env={Env}, mode={Mode}",
            request.StackId, request.Env, mode);

        var config = await _configLoader.LoadAsync(request.RootPath, ct).ConfigureAwait(false);
        var ctx = RenderContext.Create(request, _fs, _yaml, config);

        _fs.EnsureDirectory(ctx.OutputDir);

        var runList = _stages
            .Where(s => AppliesToMode(s, mode))
            .OrderBy(s => s.Order)
            .ToArray();

        _logger.LogDebug("Executing {StageCount} stages", runList.Length);

        foreach (var stage in runList)
        {
            ct.ThrowIfCancellationRequested();
            var stageName = stage.GetType().Name;
            _logger.LogDebug("Executing stage: {StageName}", stageName);

            try
            {
                await stage.ExecuteAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stage {StageName} failed", stageName);
                throw;
            }
        }

        if (string.IsNullOrWhiteSpace(ctx.OutFilePath))
            throw new InvalidOperationException(
                "Pipeline did not produce an output file. SerializeStage or ConfigExportStage must set OutFilePath.");

        sw.Stop();
        _logger.LogInformation("Render pipeline completed in {ElapsedMs}ms, output={OutFile}",
            sw.ElapsedMilliseconds, ctx.OutFilePath);

        return new RenderResult
        {
            OutFile = ctx.OutFilePath!,
            HistoryFile = ctx.HistoryFilePath,
        };
    }
    
    private static bool AppliesToMode(IRenderStage stage, PipelineMode mode)
    {
        var attr = stage.GetType().GetCustomAttribute<StageUsageAttribute>();
        if (attr is null || attr.Modes is null || attr.Modes.Length == 0)
            return true; // işaretlenmemiş -> her mod
        return attr.Modes.Contains(mode);
    }
}

