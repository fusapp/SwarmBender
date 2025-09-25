using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Logging : ComposeNode
{
    [YamlMember(Alias = "driver", ApplyNamingConventions = false)]
    public string? Driver { get; set; }

    [YamlMember(Alias = "options", ApplyNamingConventions = false)]
    public Dictionary<string, string>? Options { get; set; }
}