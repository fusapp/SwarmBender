

using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public sealed class DockerDotnetSecretsRunner : ISecretsEngineRunner, IAsyncDisposable
{
    private readonly IDockerClient _client;

    public DockerDotnetSecretsRunner(SecretsEngineArgs args)
    {
        // Endpoint çözümü: args.DockerHost -> env(DOCKER_HOST) -> unix:///var/run/docker.sock
        var endpoint = !string.IsNullOrWhiteSpace(args?.DockerHost)
            ? args!.DockerHost
            : (Environment.GetEnvironmentVariable("DOCKER_HOST")
                ?? "unix:///var/run/docker.sock");

        var cfg = new DockerClientConfiguration(new Uri(endpoint));
        _client = cfg.CreateClient();
    }

    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken ct)
    {
        try
        {
            var items = await _client.Secrets.ListAsync(ct);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in items)
            {
                var name = s.Spec?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    set.Add(name!);
            }
            return set;
        }
        catch (Exception ex)
        {
            throw new Exception($"List secrets failed (endpoint={_client.Configuration.EndpointBaseUri}): {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    public async Task CreateAsync(string name, string value, IDictionary<string, string>? labels, CancellationToken ct)
    {
        // Docker API: SecretsCreateParameters(Data, Name, Labels)
        var p = new SecretSpec
        {
            Name = name,
            Data = Encoding.UTF8.GetBytes(value ?? string.Empty),
            Labels = labels is { Count: > 0 } ? new Dictionary<string, string>(labels) : null
        };

        try
        {
            await _client.Secrets.CreateAsync(p, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Zaten varsa sessiz geç (CLI runner’daki idempotent davranışla uyumlu)
        }
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        // DELETE /secrets/{id} => önce ID bul
        var items = await _client.Secrets.ListAsync(ct);
        var hit = items.FirstOrDefault(s => string.Equals(s.Spec?.Name, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return;

        await _client.Secrets.DeleteAsync(hit.ID, ct);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}