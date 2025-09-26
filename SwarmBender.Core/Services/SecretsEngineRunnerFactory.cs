using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public sealed class SecretsEngineRunnerFactory : ISecretsEngineRunnerFactory
{
    public ISecretsEngineRunner Create(SecretsEngine engine)
    {
        var type = engine?.Type?.ToLowerInvariant() ?? "docker-cli";
        return type switch
        {
            "docker-cli"    => new DockerCliSecretsRunner(engine!.Args),
            "docker-dotnet" => new DockerCliSecretsRunner(engine!.Args), // şimdilik cli
            _               => new DockerCliSecretsRunner(engine!.Args)
        };
    }
}