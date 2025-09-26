using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Config : ComposeNode
{ 
    [YamlMember(Alias = "file", ApplyNamingConventions = false)]
    public string? File { get; set; }

    [YamlMember(Alias = "name", ApplyNamingConventions = false)]
    public string? Name { get; set; }

    [YamlMember(Alias = "external", ApplyNamingConventions = false)]
    public ExternalDef? External { get; set; }
}