using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Service : ComposeNode
{
    [YamlMember(Alias = "image", ApplyNamingConventions = false)]
    public string? Image { get; set; }

    [YamlMember(Alias = "build", ApplyNamingConventions = false)]
    public object? Build { get; set; } // Swarm dışı; validasyonda reddedilecek

    [YamlMember(Alias = "command", ApplyNamingConventions = false)]
    public ListOrString? Command { get; set; }

    [YamlMember(Alias = "entrypoint", ApplyNamingConventions = false)]
    public ListOrString? Entrypoint { get; set; }

    [YamlMember(Alias = "environment", ApplyNamingConventions = false)]
    public ListOrDict? Environment { get; set; }

    [YamlMember(Alias = "labels", ApplyNamingConventions = false)]
    public ListOrDict? Labels { get; set; }

    [YamlMember(Alias = "ports", ApplyNamingConventions = false)]
    public List<PortMapping>? Ports { get; set; } // "80:80" | { target, published, ... }

    [YamlMember(Alias = "volumes", ApplyNamingConventions = false)]
    public List<Mount>? Volumes { get; set; }

    [YamlMember(Alias = "secrets", ApplyNamingConventions = false)]
    public List<ServiceSecretRef>? Secrets { get; set; }

    [YamlMember(Alias = "configs", ApplyNamingConventions = false)]
    public List<ServiceConfigRef>? Configs { get; set; }

    [YamlMember(Alias = "deploy", ApplyNamingConventions = false)]
    public Deploy? Deploy { get; set; }

    [YamlMember(Alias = "logging", ApplyNamingConventions = false)]
    public Logging? Logging { get; set; }

    [YamlMember(Alias = "healthcheck", ApplyNamingConventions = false)]
    public Healthcheck? Healthcheck { get; set; }

    [YamlMember(Alias = "depends_on", ApplyNamingConventions = false)]
    public object? DependsOn { get; set; } // list | map (isteğe bağlı 2. tur tiplenir)

    // ----- x-sb -----
    [YamlMember(Alias = "x-sb-secrets", ApplyNamingConventions = false)]
    public Dictionary<string, string>? X_Sb_Secrets { get; set; }

    [YamlMember(Alias = "x-sb-groups", ApplyNamingConventions = false)]
    public List<string>? X_Sb_Groups { get; set; }
    
    [YamlMember(Alias = "networks", ApplyNamingConventions = false)]
    public ServiceNetworks? Networks { get; set; }
    
    [YamlMember(Alias = "env_file", ApplyNamingConventions = false)]
    public ListOrString? EnvFile { get; set; }   // string | string[]

    [YamlMember(Alias = "extra_hosts", ApplyNamingConventions = false)]
    public ExtraHosts? ExtraHosts { get; set; }
    
    [YamlMember(Alias = "ulimits", ApplyNamingConventions = false)]
    public Ulimits? Ulimits { get; set; }

    [YamlMember(Alias = "sysctls", ApplyNamingConventions = false)]
    public Sysctls? Sysctls { get; set; }
    
    [YamlMember(Alias = "dns", ApplyNamingConventions = false)]
    public ListOrString? Dns { get; set; }           // string | string[]

    [YamlMember(Alias = "dns_search", ApplyNamingConventions = false)]
    public ListOrString? DnsSearch { get; set; }     // string | string[]

    [YamlMember(Alias = "dns_opt", ApplyNamingConventions = false)]
    public List<string>? DnsOpt { get; set; }   
    [YamlMember(Alias = "user", ApplyNamingConventions = false)]
    public string? User { get; set; }                 // "app" | "1000" | "1000:1000"

    [YamlMember(Alias = "working_dir", ApplyNamingConventions = false)]
    public string? WorkingDir { get; set; }           // "/app"

    [YamlMember(Alias = "stop_grace_period", ApplyNamingConventions = false)]
    public string? StopGracePeriod { get; set; }      // "10s", "1m30s"

    [YamlMember(Alias = "stop_signal", ApplyNamingConventions = false)]
    public string? StopSignal { get; set; }           // "SIGTERM"
    [YamlMember(Alias = "cap_add", ApplyNamingConventions = false)]
    public List<string>? CapAdd { get; set; }      // ["NET_ADMIN", "SYS_TIME"]

    [YamlMember(Alias = "cap_drop", ApplyNamingConventions = false)]
    public List<string>? CapDrop { get; set; }     // ["ALL"] vb.

    [YamlMember(Alias = "devices", ApplyNamingConventions = false)]
    public List<string>? Devices { get; set; }     // ["/dev/ttyUSB0:/dev/ttyUSB0", "/dev/fuse:/dev/fuse:rw"]

    [YamlMember(Alias = "tmpfs", ApplyNamingConventions = false)]
    public List<string>? Tmpfs { get; set; }       // ["/run","/tmp"]
    [YamlMember(Alias = "profiles", ApplyNamingConventions = false)]
    public List<string>? Profiles { get; set; } 
}