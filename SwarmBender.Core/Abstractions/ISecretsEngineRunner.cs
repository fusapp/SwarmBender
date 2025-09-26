namespace SwarmBender.Core.Abstractions;

public interface ISecretsEngineRunner
{
    Task<IReadOnlySet<string>> ListAsync(CancellationToken ct); // mevcut secret adları
    Task CreateAsync(string name, string value, IDictionary<string,string>? labels, CancellationToken ct);
    Task RemoveAsync(string name, CancellationToken ct);
}