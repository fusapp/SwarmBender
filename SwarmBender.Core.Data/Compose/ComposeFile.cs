using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>Main compose document.</summary>
public sealed class ComposeFile : ComposeNode
{
    [YamlMember(Alias = "version", ApplyNamingConventions = false)]
    public string? Version { get; set; } // deprecated in spec, still round-tripped.  [oai_citation:1‡GitHub](https://raw.githubusercontent.com/compose-spec/compose-go/refs/heads/main/schema/compose-spec.json)

    [YamlMember(Alias = "name", ApplyNamingConventions = false)]
    public string? Name { get; set; }

    [YamlMember(Alias = "services", ApplyNamingConventions = false)]
    public Dictionary<string, Service> Services { get; set; } = new();

    [YamlMember(Alias = "networks", ApplyNamingConventions = false)]
    public Dictionary<string, Network>? Networks { get; set; }

    [YamlMember(Alias = "volumes", ApplyNamingConventions = false)]
    public Dictionary<string, Volume>? Volumes { get; set; }

    [YamlMember(Alias = "secrets", ApplyNamingConventions = false)]
    public Dictionary<string, Secret>? Secrets { get; set; }

    [YamlMember(Alias = "configs", ApplyNamingConventions = false)]
    public Dictionary<string, Config>? Configs { get; set; }

    // ----- x-sb extensions on root -----
    [YamlMember(Alias = "x-sb-groups", ApplyNamingConventions = false)]
    public List<string>? X_Sb_Groups { get; set; }

    [YamlMember(Alias = "x-sb-multi-tenant", ApplyNamingConventions = false)]
    public bool? X_Sb_MultiTenant { get; set; }
}