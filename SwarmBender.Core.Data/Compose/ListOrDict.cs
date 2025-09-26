using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>Represents fields that can be either a list of "KEY=VAL" or a mapping.</summary>
public sealed class ListOrDict : ComposeNode
{
    [YamlMember(Alias = "__list__", ApplyNamingConventions = false)]
    public List<string>? AsList { get; set; }

    [YamlMember(Alias = "__map__", ApplyNamingConventions = false)]
    public Dictionary<string, string>? AsMap { get; set; }

    public static ListOrDict FromMap(Dictionary<string, string> map)
        => new() { AsMap = map };

    public static ListOrDict FromList(List<string> list)
        => new() { AsList = list };
}