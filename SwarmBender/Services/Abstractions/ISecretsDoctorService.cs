namespace SwarmBender.Services.Abstractions;


/// <summary>Performs consistency checks between secrets-map and Docker Engine.</summary>
public interface ISecretsDoctorService
{
    Task<int> DiagnoseAsync(string rootPath, string env, string scope, bool quiet, CancellationToken ct = default);
}