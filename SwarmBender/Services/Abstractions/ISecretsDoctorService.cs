namespace SwarmBender.Services.Abstractions;


/// <summary>Performs consistency checks between secrets-map and Docker Engine.</summary>
public interface ISecretsDoctorService
{
    Task<SecretsDoctorResult> CheckAsync(SecretsDoctorRequest request, CancellationToken ct = default);
}

public sealed record SecretsDoctorRequest(string RootPath, string Environment, string? MapPath = null);
public sealed record SecretsDoctorResult(
    string MapPath,
    IReadOnlyList<string> MissingOnEngine,
    IReadOnlyList<string> OrphanedOnEngine,
    IReadOnlyDictionary<string, List<string>> MultiVersions
);