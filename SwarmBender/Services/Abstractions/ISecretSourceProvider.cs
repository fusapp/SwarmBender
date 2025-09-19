namespace SwarmBender.Services.Abstractions;

/// <summary>Provides secret key-value pairs from a backing store (env, file, key vault...).</summary>
public interface ISecretSourceProvider
{
    /// <summary>Unique provider type key (e.g., 'env', 'file', 'azure-key-vault').</summary>
    string Type { get; }

    /// <summary>Fetch flattened secret keys and their values for a given scope/env.</summary>
    Task<IDictionary<string, string>> GetAsync(string rootPath, string scope, string env, CancellationToken ct = default);
}