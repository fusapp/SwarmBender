using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Abstractions;

public interface ISecretsEngineRunnerFactory
{
    ISecretsEngineRunner Create(SecretsEngine engine);
}