namespace SwarmBender.Services.Abstractions;

public sealed record InfisicalUploadItem(string FlatKey, string InfisicalKey, string Action);
public sealed record InfisicalUploadResult(bool DryRun, IReadOnlyList<InfisicalUploadItem> Items);

public sealed record InfisicalUploadRequest(
    string RootPath,
    string Env,
    string Scope,
    IDictionary<string, string> Items,          // flattened
    IReadOnlyList<string> IncludeOverride,      // optional include filters; empty = provider defaults
    bool DryRun
);

public interface IInfisicalClient
{
    Task<InfisicalUploadResult> UploadAsync(InfisicalUploadRequest request, CancellationToken ct = default);
}