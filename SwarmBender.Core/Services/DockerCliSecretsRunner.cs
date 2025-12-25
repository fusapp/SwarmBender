using System.Diagnostics;
using System.Text.RegularExpressions;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public sealed partial class DockerCliSecretsRunner : ISecretsEngineRunner
{
    private readonly string _docker;
    private readonly string _host;

    // Validation pattern for secret names: alphanumeric, underscores, hyphens, dots
    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled)]
    private static partial Regex SafeNamePattern();

    public DockerCliSecretsRunner(SecretsEngineArgs args)
    {
        _docker = ValidateExecutablePath(args?.DockerPath ?? "docker");
        _host = args?.DockerHost ?? "";
    }

    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken ct)
    {
        var args = BuildArgsList("secret", "ls", "--format", "{{.Name}}");
        var (ok, stdout) = await RunAsync(args, null, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ok)
            foreach (var l in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                set.Add(l.Trim());
        return set;
    }

    public async Task CreateAsync(string name, string value, IDictionary<string, string>? labels, CancellationToken ct)
    {
        ValidateSecretName(name);

        var args = BuildArgsList("secret", "create");

        if (labels is { Count: > 0 })
        {
            foreach (var kv in labels)
            {
                ValidateLabelKeyValue(kv.Key, kv.Value);
                args.Add("--label");
                args.Add($"{kv.Key}={kv.Value}");
            }
        }

        args.Add(name);
        args.Add("-");

        await RunAsync(args, value, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        ValidateSecretName(name);
        var args = BuildArgsList("secret", "rm", name);
        await RunAsync(args, null, ct).ConfigureAwait(false);
    }

    private List<string> BuildArgsList(params string[] baseArgs)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(_host))
        {
            args.Add($"--host={_host}");
        }
        args.AddRange(baseArgs);
        return args;
    }

    private static void ValidateSecretName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Secret name cannot be empty.", nameof(name));

        if (name.Length > 256)
            throw new ArgumentException("Secret name is too long (max 256 characters).", nameof(name));

        if (!SafeNamePattern().IsMatch(name))
            throw new ArgumentException(
                $"Secret name '{name}' contains invalid characters. Only alphanumeric, underscores, hyphens, and dots are allowed.",
                nameof(name));
    }

    private static void ValidateLabelKeyValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Label key cannot be empty.", nameof(key));

        if (!SafeNamePattern().IsMatch(key))
            throw new ArgumentException($"Label key '{key}' contains invalid characters.", nameof(key));

        // Value can contain more characters but not shell metacharacters
        if (value.IndexOfAny(['`', '$', '(', ')', '{', '}', ';', '|', '&', '<', '>', '\n', '\r']) >= 0)
            throw new ArgumentException($"Label value contains invalid characters.", nameof(value));
    }

    private static string ValidateExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Docker executable path cannot be empty.", nameof(path));

        // Only allow simple executable name or absolute path
        if (path.Contains("..") || path.IndexOfAny(['`', '$', '(', ')', '{', '}', ';', '|', '&', '<', '>']) >= 0)
            throw new ArgumentException($"Docker executable path '{path}' contains invalid characters.", nameof(path));

        return path;
    }

    private async Task<(bool ok, string stdout)> RunAsync(List<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_docker)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var p = new Process { StartInfo = psi };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start docker process: {ex.Message}", ex);
        }

        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            p.StandardInput.Close();
        }
        else
        {
            p.StandardInput.Close();
        }

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);

        var ok = p.ExitCode == 0;
        if (!ok)
            throw new InvalidOperationException($"Docker command failed (exit code {p.ExitCode}): {errTask.Result}");

        return (ok, outTask.Result ?? "");
    }
}