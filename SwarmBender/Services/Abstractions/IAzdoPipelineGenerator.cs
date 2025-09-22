using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Services.Models;

namespace SwarmBender.Services.Abstractions;

public interface IAzdoPipelineGenerator
{
    Task<AzdoPipelineResult> GenerateAsync(AzdoPipelineRequest request, CancellationToken ct = default);
}

public sealed record AzdoPipelineResult(string OutFile, string Yaml);