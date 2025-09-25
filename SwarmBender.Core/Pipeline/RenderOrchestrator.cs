using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Pipeline;

/// <summary>Wires stages (Decorator/Pipeline). Order is important.</summary>
public sealed class RenderOrchestrator : IRenderOrchestrator
{
    private readonly IEnumerable<IRenderStage> _stages;
    private readonly IFileSystem _fs;
    private readonly IYamlEngine _yaml;
    private readonly SbConfig _config;

    public RenderOrchestrator(
        IEnumerable<IRenderStage> stages,
        IFileSystem fs,
        IYamlEngine yaml,
        SbConfig config)
    {
        _stages = stages.ToArray();
        _fs = fs;
        _yaml = yaml;
        _config = config;
    }

    public async Task<RenderResult> RunAsync(RenderRequest request, CancellationToken ct = default)
    {
    
        var ctx = RenderContext.Create(request, _fs, _yaml);

     
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

