using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class Mount
{
    [YamlMember(Alias = "type", ApplyNamingConventions = false)]
    public string? Type { get; set; } // "volume"|"bind"|"tmpfs"|"npipe"

    [YamlMember(Alias = "source", ApplyNamingConventions = false)]
    public string? Source { get; set; }

    [YamlMember(Alias = "target", ApplyNamingConventions = false)]
    public string? Target { get; set; }

    [YamlMember(Alias = "read_only", ApplyNamingConventions = false)]
    public bool? ReadOnly { get; set; }

    // bind-specific
    [YamlMember(Alias = "bind", ApplyNamingConventions = false)]
    public BindOptions? Bind { get; set; }

    // volume-specific
    [YamlMember(Alias = "volume", ApplyNamingConventions = false)]
    public VolumeOptions? Volume { get; set; }

    // tmpfs-specific
    [YamlMember(Alias = "tmpfs", ApplyNamingConventions = false)]
    public TmpfsOptions? Tmpfs { get; set; }
}

public sealed class BindOptions
{
    [YamlMember(Alias = "propagation", ApplyNamingConventions = false)]
    public string? Propagation { get; set; } // "rprivate"|"private"|...
}

public sealed class VolumeOptions
{
    [YamlMember(Alias = "nocopy", ApplyNamingConventions = false)]
    public bool? NoCopy { get; set; }
}

public sealed class TmpfsOptions
{
    [YamlMember(Alias = "size", ApplyNamingConventions = false)]
    public int? Size { get; set; } // bytes
}