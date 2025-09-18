using SwarmBender.Services.Models;
using System.Threading;
using System.Threading.Tasks;

namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Initializes scaffolding for solution root or specific stack.
/// </summary>
public interface IInitExecutor
{
    Task<InitResult> ExecuteAsync(InitRequest request, CancellationToken ct = default);
}
