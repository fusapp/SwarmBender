using SwarmBender.Services.Models;

namespace SwarmBender.Services.Abstractions;

/// <summary>Rotate (create new version) for one or more secrets and update the secrets-map.</summary>
public interface ISecretsRotateService
{
    Task<SecretsRotateResult> RotateAsync(SecretsRotateRequest request, CancellationToken ct = default);
}