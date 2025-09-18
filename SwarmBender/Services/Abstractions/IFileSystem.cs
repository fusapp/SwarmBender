using SwarmBender.Services.Models;
using System.Threading;
using System.Threading.Tasks;

namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Thin async wrapper over file I/O for testability and verbose output.
/// </summary>
public interface IFileSystem
{
    Task<WriteResult> EnsureDirectoryAsync(string path, bool dryRun, bool quiet, CancellationToken ct = default);
    Task<WriteResult> EnsureFileAsync(string path, string content, bool dryRun, bool quiet, CancellationToken ct = default);
    Task<WriteResult> AppendGitignoreAsync(string root, string[] lines, bool dryRun, bool quiet, CancellationToken ct = default);
}
