using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>
/// Represents 'extra_hosts' which can be either:
///  - a list of "host:ip" strings (short syntax)
///  - a mapping of host -> ip (long syntax)
/// </summary>
public sealed class ExtraHosts
{
    [YamlMember(Alias = "__list__", ApplyNamingConventions = false)]
    public List<string>? AsList { get; set; }

    [YamlMember(Alias = "__map__", ApplyNamingConventions = false)]
    public Dictionary<string,string>? AsMap { get; set; }

    public static ExtraHosts FromList(List<string> list) => new() { AsList = list };
    public static ExtraHosts FromMap(Dictionary<string,string> map) => new() { AsMap = map };
}