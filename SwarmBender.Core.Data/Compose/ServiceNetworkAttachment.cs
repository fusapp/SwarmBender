using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>Service-level network attachment options (long syntax).</summary>
public sealed class ServiceNetworkAttachment
{
    [YamlMember(Alias = "aliases", ApplyNamingConventions = false)]
    public List<string>? Aliases { get; set; }

    [YamlMember(Alias = "priority", ApplyNamingConventions = false)]
    public int? Priority { get; set; }

    [YamlMember(Alias = "ipv4_address", ApplyNamingConventions = false)]
    public string? Ipv4Address { get; set; }

    [YamlMember(Alias = "ipv6_address", ApplyNamingConventions = false)]
    public string? Ipv6Address { get; set; }

    [YamlMember(Alias = "link_local_ips", ApplyNamingConventions = false)]
    public List<string>? LinkLocalIps { get; set; }
}