namespace SwarmBender.Services.Abstractions;

/// <summary>Abstraction over Docker Engine secret operations.</summary>
public interface IDockerSecretClient
{
    /// <summary>Returns all secret names available on the target Engine.</summary>
    Task<IReadOnlyCollection<string>> ListNamesAsync(CancellationToken ct = default);

    /// <summary>Returns secret metadata (name, createdAt, labels) for pruning/doctor.</summary>
    Task<IReadOnlyCollection<DockerSecretInfo>> ListDetailedAsync(CancellationToken ct = default);

    /// <summary>Creates a new secret with given name and raw content. Returns true if created, false if already existed.</summary>
    Task<bool> EnsureCreatedAsync(string name, string content, IDictionary<string,string>? labels, CancellationToken ct = default);

    /// <summary>Removes the specified secret. Returns true if removed.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken ct = default);
}

/// <summary>Lightweight info surfaced by docker secret ls/inspect.</summary>
public sealed record DockerSecretInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string,string> Labels
);