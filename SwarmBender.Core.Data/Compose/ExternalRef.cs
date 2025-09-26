using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class ExternalRef
{
    [YamlMember(Alias = "external", ApplyNamingConventions = false)]
    public bool? External { get; set; }

    [YamlMember(Alias = "name", ApplyNamingConventions = false)]
    public string? Name { get; set; }
}