using SwarmBender.Services.Models;
using System.Threading;
using System.Threading.Tasks;

namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Validates stacks against policies and basic schema.
/// </summary>
public interface IValidator
{
    Task<ValidateResult> ValidateAsync(ValidateRequest request, CancellationToken ct = default);
}
