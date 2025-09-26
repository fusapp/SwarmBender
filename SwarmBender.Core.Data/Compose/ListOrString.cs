using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>Represents fields that can be either a single string or a list of strings (command, entrypoint).</summary>
public sealed class ListOrString : ComposeNode
{
    [YamlMember(Alias = "__string__", ApplyNamingConventions = false)]
    public string? AsString { get; set; }

    [YamlMember(Alias = "__list__", ApplyNamingConventions = false)]
    public List<string>? AsList { get; set; }

    public static ListOrString FromString(string s) => new() { AsString = s };
    public static ListOrString FromList(List<string> l) => new() { AsList = l };
}