using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Network : ComposeNode
{
    [YamlMember(Alias = "name", ApplyNamingConventions = false)]
    public string? Name { get; set; }

    [YamlMember(Alias = "external", ApplyNamingConventions = false)]
    public ExternalDef? External { get; set; }

    [YamlMember(Alias = "driver", ApplyNamingConventions = false)]
    public string? Driver { get; set; }

    [YamlMember(Alias = "driver_opts", ApplyNamingConventions = false)]
    public Dictionary<string, string>? DriverOpts { get; set; }

    [YamlMember(Alias = "labels", ApplyNamingConventions = false)]
    public Dictionary<string, string>? Labels { get; set; }
}