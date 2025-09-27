using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public sealed class SecretsEngineRunnerFactory : ISecretsEngineRunnerFactory
{
    public ISecretsEngineRunner Create(SecretsEngine engine)
    {
        var type = engine?.Type?.ToLowerInvariant() ?? "docker-cli";
        Console.WriteLine($"Docker Type {type}");
        return type switch
        {
            "docker-dotnet" => new DockerDotnetSecretsRunner(engine!.Args),
            "docker-cli" or null => new DockerCliSecretsRunner(engine!.Args), // geri uyumluluk
            _ => throw new NotSupportedException($"Unknown secrets engine: {engine!.Type}")
        };
    }
}