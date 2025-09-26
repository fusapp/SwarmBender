using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public sealed class DockerCliSecretsRunner : ISecretsEngineRunner
{
    private readonly string _docker;
    private readonly string _host;

    public DockerCliSecretsRunner(SecretsEngineArgs args)
    {
        _docker = args?.DockerPath ?? "docker";
        _host = args?.DockerHost ?? "";
    }

    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken ct)
    {
        var (ok, stdout) = await Run($"{_docker} {HostArg()} secret ls --format \"{{{{.Name}}}}\"", null, ct);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ok)
            foreach (var l in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                set.Add(l.Trim());
        return set;
    }

    public Task CreateAsync(string name, string value, IDictionary<string, string>? labels, CancellationToken ct)
    {
        var labelArgs = (labels is { Count: > 0 })
            ? string.Join(" ", labels.Select(kv => $"--label {Esc(kv.Key)}={Esc(kv.Value)}"))
            : "";
        var cmd = $"{_docker} {HostArg()} secret create {labelArgs} {Esc(name)} -";
        return Run(cmd, value, ct);
    }

    public Task RemoveAsync(string name, CancellationToken ct)
        => Run($"{_docker} {HostArg()} secret rm {Esc(name)}", null, ct);

    private string HostArg() => string.IsNullOrEmpty(_host) ? "" : $"--host={_host}";
    private static string Esc(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static async Task<(bool ok, string stdout)> Run(string cmd, string? stdin, CancellationToken ct)
    {
        // minimal process runner
        var psi = new System.Diagnostics.ProcessStartInfo("bash", $"-lc {Esc(cmd)}")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(outTask, errTask);
        p.WaitForExit();
        var ok = p.ExitCode == 0;
        if (!ok) throw new Exception($"docker error: {errTask.Result}");
        return (ok, outTask.Result ?? "");
    }
}