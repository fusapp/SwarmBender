using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Healthcheck : ComposeNode
{
    [YamlMember(Alias = "test", ApplyNamingConventions = false)]
    public ListOrString? Test { get; set; }   // string | string[]

    [YamlMember(Alias = "interval", ApplyNamingConventions = false)]
    public string? Interval { get; set; }

    [YamlMember(Alias = "timeout", ApplyNamingConventions = false)]
    public string? Timeout { get; set; }

    [YamlMember(Alias = "retries", ApplyNamingConventions = false)]
    public int? Retries { get; set; }

    [YamlMember(Alias = "start_period", ApplyNamingConventions = false)]
    public string? StartPeriod { get; set; }
}