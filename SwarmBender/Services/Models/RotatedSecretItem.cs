namespace SwarmBender.Services.Models;

public sealed record RotatedSecretItem(
    string Key,
    string SecretName,
    bool Created,
    bool MapUpdated,
    int OldVersionsPruned
);