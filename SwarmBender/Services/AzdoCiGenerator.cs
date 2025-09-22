using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;
using SwarmBender.Services.Templates;

namespace SwarmBender.Services;

public class AzdoCiGenerator: ICiGenerator
{
    private readonly AzdoSwarmDeployTemplate _template = new();

    public async Task<CiGenResult> GenerateAsync(CiGenRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.Provider, "azdo", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only 'azdo' provider is supported.");

        if (!string.Equals(request.Kind, "swarm-deploy", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only 'swarm-deploy' kind is supported.");

        if (string.IsNullOrWhiteSpace(request.StackId))
            throw new ArgumentException("StackId cannot be empty.", nameof(request.StackId));

        if (request.Environments is null || request.Environments.Count == 0)
            throw new ArgumentException("At least one environment must be provided.", nameof(request.Environments));

        var model = new AzdoSwarmDeployModel
        {
            StackId = request.StackId,
            Envs = request.Environments,
            Branch = request.Branch,
            PoolVmImage = request.PoolVmImage,
            DotnetSdk = request.DotnetSdk,
            SbVersion = request.SbVersion
        };

        var yaml = _template.Render(model);

        var root = Path.GetFullPath(request.RootPath);
        var outPath = Path.IsPathRooted(request.OutPath)
            ? request.OutPath
            : Path.Combine(root, request.OutPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, yaml, new System.Text.UTF8Encoding(false), ct);

        return new CiGenResult(outPath);
    }
}