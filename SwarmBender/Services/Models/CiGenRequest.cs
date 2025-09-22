namespace SwarmBender.Services.Models;

/// <summary>
/// CI pipeline generation request (reduced surface focused on Azure DevOps + Swarm).
/// </summary>
public sealed record CiGenRequest(
    string RootPath,
    string Provider,          // "azdo"
    string Kind,              // "swarm-deploy"
    string StackId,           // e.g., "sso"
    IReadOnlyList<string> Environments, // e.g., ["dev","prod"]
    string OutPath,           // e.g., "ops/ci-templates/azure/swarm-deploy.yml"
    string Branch = "main",
    string PoolVmImage = "ubuntu-latest",
    string DotnetSdk = "9.0.x",
    string SbVersion = "1.*"  // SwarmBender-Cli NuGet tool version
);