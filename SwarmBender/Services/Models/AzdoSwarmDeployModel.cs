namespace SwarmBender.Services.Models;


/// <summary>
/// Pure data used by the template to render Azure DevOps YAML.
/// </summary>
public sealed class AzdoSwarmDeployModel
{
    public required string StackId { get; init; }
    public required IReadOnlyList<string> Envs { get; init; }
    public required string Branch { get; init; }
    public required string PoolVmImage { get; init; }
    public required string DotnetSdk { get; init; }
    public required string SbVersion { get; init; }

    // Fixed variable names used by the pipeline (can be expanded later if needed).
    public string VarDockerHost => "DOCKER_HOST";
    public string VarRegistryServer => "REGISTRY_SERVER";
    public string VarRegistryUser => "REGISTRY_USERNAME";
    public string VarRegistryPass => "REGISTRY_PASSWORD";
}