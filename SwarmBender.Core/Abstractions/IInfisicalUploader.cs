using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Abstractions;

public interface IInfisicalUploader
{
    Task<UploadReport> UploadAsync(
        string repoRoot,
        string stackId,
        string env,
        SbConfig cfg,
        bool force = false,
        bool dryRun = false,
        bool showValues = false,
        CancellationToken ct = default);
}

public sealed record UploadItemResult(
    string Key,
    string? ExistingValue,
    string? NewValue,
    string Reason // "created" | "updated" | "skipped-same" | "skipped-filtered" | "skipped-missing-token" | "error: ..."
);

public sealed class UploadReport
{
    public int Created { get; }
    public int Updated { get; }
    public int SkippedSame { get; }
    public int SkippedOther { get; }
    public int SkippedFiltered { get; }
    public int SkippedMissingToken { get; }
    public List<UploadItemResult> Items { get; }

    public UploadReport(
        int created, int updated, int skippedSame, int skippedOther,
        int skippedFiltered, int skippedMissingToken, List<UploadItemResult> items)
    {
        Created = created;
        Updated = updated;
        SkippedSame = skippedSame;
        SkippedOther = skippedOther;
        SkippedFiltered = skippedFiltered;
        SkippedMissingToken = skippedMissingToken;
        Items = items;
    }
}