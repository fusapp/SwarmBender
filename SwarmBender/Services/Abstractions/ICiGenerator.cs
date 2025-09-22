using SwarmBender.Services.Models;

namespace SwarmBender.Services.Abstractions;

public interface ICiGenerator
{
    Task<CiGenResult> GenerateAsync(CiGenRequest request, CancellationToken ct = default);
}