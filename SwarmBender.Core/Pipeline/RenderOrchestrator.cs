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

    public RenderOrchestrator(
        IEnumerable<IRenderStage> stages,
        IFileSystem fs,
        IYamlEngine yaml,
        ISbConfigLoader configLoader)
    {
        _stages = stages.OrderBy(x=>x.Order).ToArray();
        _fs = fs;
        _yaml = yaml;
        _configLoader = configLoader;
    }

    public async Task<RenderResult> RunAsync(RenderRequest request, CancellationToken ct = default)
    {
    
        var config = await _configLoader.LoadAsync(request.RootPath, ct);
        var ctx = RenderContext.Create(request, _fs, _yaml, config);

     
        _fs.EnsureDirectory(ctx.OutputDir);

        foreach (var stage in _stages)
        {
            ct.ThrowIfCancellationRequested();
            await stage.ExecuteAsync(ctx, ct);
        }

        if (string.IsNullOrWhiteSpace(ctx.OutFilePath))
            throw new InvalidOperationException(
                "Pipeline did not produce an output file. SerializeStage must set OutFilePath.");

        return new RenderResult
        {
            OutFile = ctx.OutFilePath!,
            HistoryFile = ctx.HistoryFilePath,
        };
    }
}

