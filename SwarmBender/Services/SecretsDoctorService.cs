using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

public sealed class SecretsDoctorService : ISecretsDoctorService
{
    public Task<int> DiagnoseAsync(string rootPath, string env, string scope, bool quiet, CancellationToken ct = default)
    {
        // TODO: Implement consistency checks between secrets-map and Engine
        return Task.FromResult(0);
    }
}