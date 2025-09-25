using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Deploy : ComposeNode
{
    [YamlMember(Alias = "mode", ApplyNamingConventions = false)]
    public string? Mode { get; set; } // "replicated" | "global"

    [YamlMember(Alias = "replicas", ApplyNamingConventions = false)]
    public int? Replicas { get; set; } // only when mode=replicated

    [YamlMember(Alias = "resources", ApplyNamingConventions = false)]
    public DeployResources? Resources { get; set; }

    [YamlMember(Alias = "restart_policy", ApplyNamingConventions = false)]
    public RestartPolicy? RestartPolicy { get; set; }

    [YamlMember(Alias = "update_config", ApplyNamingConventions = false)]
    public UpdateConfig? UpdateConfig { get; set; }

    [YamlMember(Alias = "rollback_config", ApplyNamingConventions = false)]
    public UpdateConfig? RollbackConfig { get; set; }

    [YamlMember(Alias = "placement", ApplyNamingConventions = false)]
    public Placement? Placement { get; set; }

    [YamlMember(Alias = "labels", ApplyNamingConventions = false)]
    public ListOrDict? Labels { get; set; }

    [YamlMember(Alias = "endpoint_mode", ApplyNamingConventions = false)]
    public string? EndpointMode { get; set; }
}

public sealed class DeployResources
{
    [YamlMember(Alias = "limits", ApplyNamingConventions = false)]
    public ResourceSpec? Limits { get; set; }

    [YamlMember(Alias = "reservations", ApplyNamingConventions = false)]
    public ResourceSpec? Reservations { get; set; }
}

public sealed class ResourceSpec
{
    [YamlMember(Alias = "cpus", ApplyNamingConventions = false)]
    public string? Cpus { get; set; } // "0.50" vb.

    [YamlMember(Alias = "memory", ApplyNamingConventions = false)]
    public string? Memory { get; set; } // "512M", "1G"
}

public sealed class RestartPolicy
{
    [YamlMember(Alias = "condition", ApplyNamingConventions = false)]
    public string? Condition { get; set; } // "none"|"on-failure"|"any"

    [YamlMember(Alias = "delay", ApplyNamingConventions = false)]
    public string? Delay { get; set; } // "5s"

    [YamlMember(Alias = "max_attempts", ApplyNamingConventions = false)]
    public int? MaxAttempts { get; set; }

    [YamlMember(Alias = "window", ApplyNamingConventions = false)]
    public string? Window { get; set; }
}

public sealed class UpdateConfig
{
    [YamlMember(Alias = "parallelism", ApplyNamingConventions = false)]
    public int? Parallelism { get; set; }

    [YamlMember(Alias = "delay", ApplyNamingConventions = false)]
    public string? Delay { get; set; }

    [YamlMember(Alias = "order", ApplyNamingConventions = false)]
    public string? Order { get; set; } // "stop-first" | "start-first"

    [YamlMember(Alias = "failure_action", ApplyNamingConventions = false)]
    public string? FailureAction { get; set; } // "continue"|"rollback" (rollback_config için de geçerli)
}

public sealed class Placement
{
    [YamlMember(Alias = "constraints", ApplyNamingConventions = false)]
    public List<string>? Constraints { get; set; }

    [YamlMember(Alias = "preferences", ApplyNamingConventions = false)]
    public List<PlacementPreference>? Preferences { get; set; }

    [YamlMember(Alias = "max_replicas_per_node", ApplyNamingConventions = false)]
    public int? MaxReplicasPerNode { get; set; }
}

public sealed class PlacementPreference
{
    [YamlMember(Alias = "spread", ApplyNamingConventions = false)]
    public string? Spread { get; set; } // örn: "node.labels.az"
}