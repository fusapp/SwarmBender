using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>
/// Represents service 'networks' which can be either:
///  - a list of network names (short syntax)
///  - a mapping of name -> ServiceNetworkAttachment (long syntax)
/// </summary>
public sealed class ServiceNetworks
{
    // short syntax: ["frontend","backend"]
    [YamlMember(Alias = "__list__", ApplyNamingConventions = false)]
    public List<string>? AsList { get; set; }

    // long syntax: { frontend: { aliases:[...], ipv4_address: ... }, ... }
    [YamlMember(Alias = "__map__", ApplyNamingConventions = false)]
    public Dictionary<string, ServiceNetworkAttachment>? AsMap { get; set; }

    public static ServiceNetworks FromList(List<string> list) => new() { AsList = list };
    public static ServiceNetworks FromMap(Dictionary<string, ServiceNetworkAttachment> map) => new() { AsMap = map };
}