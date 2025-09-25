namespace SwarmBender.Core.Abstractions;

public interface IFileSystem
{
    Task<string> ReadAllTextAsync(string path, CancellationToken ct);
    Task WriteAllTextAsync(string path, string content, CancellationToken ct);
    IEnumerable<string> GlobFiles(string root, string pattern); // supports basic glob (*, ?, [abc])
    bool FileExists(string path);
    void EnsureDirectory(string path);
}