using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class ServiceConfigRef
{
    [YamlMember(Alias = "source", ApplyNamingConventions = false)]
    public string? Source { get; set; }

    [YamlMember(Alias = "target", ApplyNamingConventions = false)]
    public string? Target { get; set; }

    [YamlMember(Alias = "uid", ApplyNamingConventions = false)]
    public string? Uid { get; set; }

    [YamlMember(Alias = "gid", ApplyNamingConventions = false)]
    public string? Gid { get; set; }

    [YamlMember(Alias = "mode", ApplyNamingConventions = false)]
    public int? Mode { get; set; }
}