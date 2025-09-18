namespace SwarmBender.Services.Models;

/// <summary>Result of init operation.</summary>
public sealed record InitResult(
    int CreatedCount,
    int SkippedCount,
    IReadOnlyList<string> InvalidEnvs);
