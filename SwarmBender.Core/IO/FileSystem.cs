using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Util;

namespace SwarmBender.Core.IO;

public sealed class FileSystem : IFileSystem
{
    public Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        => File.ReadAllTextAsync(path, ct);

    public Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        return File.WriteAllTextAsync(path, content, ct);
    }

    public IEnumerable<string> GlobFiles(string root, string pattern)
    {
        var expanded = Globber.ExpandPlaceholders(pattern);
        var dir = new DirectoryInfo(root);
        if (!dir.Exists) return Enumerable.Empty<string>();

        // Very simple globbing: split dir/glob
        var isYml = expanded.EndsWith(".yml") || expanded.EndsWith(".yaml") || expanded.Contains("*");
        var searchRoot = dir.FullName;
        var files = Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories)
            .Where(p => Globber.IsMatch(Path.GetRelativePath(root, p).Replace('\\','/'), expanded));
        return files;
    }

    public bool FileExists(string path) => File.Exists(path);

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);
    
    public void MoveFile(string source, string destination, bool overwrite = false)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (overwrite && File.Exists(destination))
            File.Delete(destination);

        File.Move(source, destination);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}