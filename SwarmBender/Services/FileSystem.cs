using Spectre.Console;
using System.Text;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>Async file system helper.</summary>
public sealed class FileSystem : IFileSystem
{
    public Task<WriteResult> EnsureDirectoryAsync(string path, bool dryRun, bool quiet, CancellationToken ct = default)
    {
        if (Directory.Exists(path))
        {
            if (!quiet) AnsiConsole.MarkupLine("[grey]skip dir[/] {0}", path);
            return Task.FromResult(WriteResult.Skipped);
        }

        if (dryRun)
        {
            if (!quiet) AnsiConsole.MarkupLine("[yellow]create dir (dry)[/] {0}", path);
            return Task.FromResult(WriteResult.Created);
        }

        Directory.CreateDirectory(path); // no async API for directories
        if (!quiet) AnsiConsole.MarkupLine("[green]create dir[/] {0}", path);
        return Task.FromResult(WriteResult.Created);
    }

    public async Task<WriteResult> EnsureFileAsync(string path, string content, bool dryRun, bool quiet, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            if (!quiet) AnsiConsole.MarkupLine("[grey]skip file[/] {0}", path);
            return WriteResult.Skipped;
        }

        if (dryRun)
        {
            if (!quiet) AnsiConsole.MarkupLine("[yellow]write file (dry)[/] {0}", path);
            return WriteResult.Created;
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        if (!quiet) AnsiConsole.MarkupLine("[green]write file[/] {0}", path);
        return WriteResult.Created;
    }

    public async Task<WriteResult> AppendGitignoreAsync(string root, string[] lines, bool dryRun, bool quiet, CancellationToken ct = default)
    {
        var path = Path.Combine(root, ".gitignore");

        if (!File.Exists(path))
        {
            return await EnsureFileAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, dryRun, quiet, ct);
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await File.ReadAllLinesAsync(path, ct))
            existing.Add(l);

        var toAdd = lines.Where(l => !existing.Contains(l)).ToArray();

        if (toAdd.Length == 0)
        {
            if (!quiet) AnsiConsole.MarkupLine("[grey]skip file[/] {0} (gitignore up-to-date)", path);
            return WriteResult.Skipped;
        }

        if (dryRun)
        {
            if (!quiet) AnsiConsole.MarkupLine("[yellow]append (dry)[/] {0}", path);
            return WriteResult.Created;
        }

        await using var sw = new StreamWriter(path, append: true, new UTF8Encoding(false));
        foreach (var l in toAdd)
            await sw.WriteLineAsync(l.AsMemory(), ct);

        if (!quiet) AnsiConsole.MarkupLine("[green]append[/] {0}", path);
        return WriteResult.Created;
    }
}
