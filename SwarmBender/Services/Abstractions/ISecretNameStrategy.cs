namespace SwarmBender.Services.Abstractions;

/// <summary>Builds versioned secret names from policy + inputs.</summary>
public interface ISecretNameStrategy
{
    string BuildName(string scope, string env, string key, string versionSuffix, string nameTemplate);
}