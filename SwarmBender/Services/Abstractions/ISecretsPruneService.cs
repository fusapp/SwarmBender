namespace SwarmBender.Services.Abstractions;

public interface ISecretsPruneService
{
    Task<SecretsPruneResult> PruneAsync(SecretsPruneRequest request, CancellationToken ct = default);
}

public sealed record SecretsPruneRequest(string? Scope, string? Environment, int KeepLatest = 2, bool DryRun = false, string? ReportPath = null);
public sealed record SecretsPruneResult(IReadOnlyList<string> Kept, IReadOnlyList<string> Removed, bool DryRun);