namespace SwarmBender.Services.Models;

public sealed record SecretsRotateResult(
    IReadOnlyList<RotatedSecretItem> Items,
    string MapPath
);