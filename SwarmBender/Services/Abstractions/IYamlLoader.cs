using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Loads YAML files as dictionary or untyped object.
/// </summary>
public interface IYamlLoader
{
    Task<IDictionary<string, object?>> LoadYamlAsync(string filePath, CancellationToken ct = default);
    Task<object?> LoadYamlUntypedAsync(string filePath, CancellationToken ct = default);
}
