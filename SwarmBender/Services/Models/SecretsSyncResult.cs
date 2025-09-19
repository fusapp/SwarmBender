namespace SwarmBender.Services.Models;

public sealed record SecretsSyncResult(
    int Created,
    int Skipped,
    string MapPath,
    IReadOnlyList<string> Entries);