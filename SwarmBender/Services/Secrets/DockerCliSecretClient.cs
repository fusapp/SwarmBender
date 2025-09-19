using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>
/// IDockerSecretClient implementation that talks to Docker Swarm via the docker CLI.
/// Requires 'docker' binary on PATH and must run on a Swarm manager node.
/// Uses: secret ls/create/rm/inspect.
/// </summary>
public sealed class DockerCliSecretClient : IDockerSecretClient
{
    private readonly string _dockerPath;
    private readonly string? _dockerHost; // e.g. unix:///var/run/docker.sock or Windows npipe

    public DockerCliSecretClient(string? dockerPath = null, string? dockerHost = null)
    {
        _dockerPath = string.IsNullOrWhiteSpace(dockerPath) ? "docker" : dockerPath!;
        _dockerHost = string.IsNullOrWhiteSpace(dockerHost) ? null : dockerHost;
    }

    public async Task<IReadOnlyCollection<string>> ListNamesAsync(CancellationToken ct = default)
    {
        var args = Args("secret", "ls", "--format", "{{.Name}}");
        var (code, stdout, stderr) = await RunAsync(args, ct);
        if (code != 0)
            throw new InvalidOperationException($"docker secret ls failed: {stderr}");

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public async Task<IReadOnlyCollection<DockerSecretInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        var names = await ListNamesAsync(ct);
        var list = new List<DockerSecretInfo>(names.Count);

        foreach (var name in names)
        {
            // CreatedAt
            var (c1, createdStr, e1) = await RunAsync(Args("secret", "inspect", "--format", "{{.CreatedAt}}", name), ct);
            if (c1 != 0) throw new InvalidOperationException($"docker secret inspect failed: {e1}");
            DateTimeOffset? createdAt = null;
            if (DateTimeOffset.TryParse(createdStr.Trim(), out var dto)) createdAt = dto;

            // Labels as JSON
            var (c2, labelsJson, e2) = await RunAsync(Args("secret", "inspect", "--format", "{{json .Spec.Labels}}", name), ct);
            if (c2 != 0) throw new InvalidOperationException($"docker secret inspect (labels) failed: {e2}");

            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!string.IsNullOrWhiteSpace(labelsJson) && labelsJson.Trim() != "null")
                {
                    using var doc = JsonDocument.Parse(labelsJson);
                    foreach (var p in doc.RootElement.EnumerateObject())
                        labels[p.Name] = p.Value.GetString() ?? "";
                }
            }
            catch { /* ignore malformed */ }

            list.Add(new DockerSecretInfo(name, createdAt, labels));
        }

        return list;
    }

    public async Task<bool> EnsureCreatedAsync(string name, string content, IDictionary<string, string>? labels, CancellationToken ct = default)
    {
        // skip if already exists
        var existing = await ListNamesAsync(ct);
        if (existing.Contains(name, StringComparer.Ordinal)) return false;

        // docker secret create [--label k=v]* name -
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(_dockerHost)) args.Add($"--host={_dockerHost}");
        args.AddRange(new[] { "secret", "create" });
        if (labels is not null)
            foreach (var kv in labels)
                args.AddRange(new[] { "--label", $"{kv.Key}={kv.Value}" });
        args.Add(name);
        args.Add("-");

        var psi = new ProcessStartInfo(_dockerPath, JoinArgs(args))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker");
        var payload = Encoding.UTF8.GetBytes(content ?? string.Empty);
        await p.StandardInput.BaseStream.WriteAsync(payload, 0, payload.Length, ct);
        await p.StandardInput.FlushAsync(ct);
        p.StandardInput.Close();

        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode == 0) return true;
        if (stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase)) return false;

        throw new InvalidOperationException($"docker secret create failed (code {p.ExitCode}): {stderr}\n{stdout}");
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        var (code, _, _) = await RunAsync(Args("secret", "rm", name), ct);
        return code == 0;
    }

    // ---- helpers ----
    private List<string> Args(params string[] items)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(_dockerHost)) list.Add($"--host={_dockerHost}");
        list.AddRange(items);
        return list;
    }

    private async Task<(int code, string stdout, string stderr)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_dockerPath, JoinArgs(args))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string JoinArgs(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(a.Contains(' ') || a.Contains('"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a);
        }
        return sb.ToString();
    }
}