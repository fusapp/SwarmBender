using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>Represents fields that can be a single string or a list of strings (e.g., command, entrypoint).</summary>
public sealed class ScalarOrList : ComposeNode
{
    [YamlMember(Alias = "__scalar__", ApplyNamingConventions = false)]
    public string? AsScalar { get; set; }

    [YamlMember(Alias = "__list__", ApplyNamingConventions = false)]
    public List<string>? AsList { get; set; }

    public static ScalarOrList FromScalar(string s) => new() { AsScalar = s };
    public static ScalarOrList FromList(List<string> l) => new() { AsList = l };
}