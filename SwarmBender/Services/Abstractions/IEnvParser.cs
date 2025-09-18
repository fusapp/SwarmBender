using System.Collections.Generic;

namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Parses/normalizes environment name inputs.
/// </summary>
public interface IEnvParser
{
    (List<string> Valid, List<string> Invalid) Normalize(IEnumerable<string> envsRaw);
}
