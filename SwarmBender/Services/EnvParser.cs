using System.Text.RegularExpressions;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Default implementation of IEnvParser.</summary>
public sealed class EnvParser : IEnvParser
{
    private static readonly Regex Rx = new("^[a-z0-9][-_.a-z0-9]*$", RegexOptions.Compiled);

    public (List<string> Valid, List<string> Invalid) Normalize(IEnumerable<string> envsRaw)
    {
        var list = new List<string>();
        foreach (var token in envsRaw.SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            list.Add(token.ToLowerInvariant());

        list = list.Distinct().ToList();

        var invalid = list.Where(e => !Rx.IsMatch(e)).ToList();
        var valid = list.Where(e => Rx.IsMatch(e)).ToList();
        return (valid, invalid);
    }
}
