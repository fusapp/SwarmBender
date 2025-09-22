namespace SwarmBender.Services.Abstractions;

public interface IMetadataValidator
{
    Task<MetaValidationResult> ValidateAsync(string rootPath, CancellationToken ct = default);
}

public sealed record MetaValidationResult(
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<MetaIssue> Issues
);

public enum MetaIssueKind { Error, Warning }

public sealed record MetaIssue(
    MetaIssueKind Kind,
    string File,
    string Message,
    string? Path = null
);