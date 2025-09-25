using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>
/// ulimits:
///   nproc: 65535
///   nofile:
///     soft: 20000
///     hard: 40000
/// </summary>
public sealed class UlimitEntry
{
    // Tek değerli kullanımda (nproc: 65535) doldurulur
    [YamlMember(Alias = "__single__", ApplyNamingConventions = false)]
    public int? Single { get; set; }

    // Nesne kullanımında (nofile: { soft, hard }) doldurulur
    [YamlMember(Alias = "soft", ApplyNamingConventions = false)]
    public int? Soft { get; set; }

    [YamlMember(Alias = "hard", ApplyNamingConventions = false)]
    public int? Hard { get; set; }

    public static UlimitEntry FromSingle(int v) => new() { Single = v };
    public bool IsObject => Soft.HasValue || Hard.HasValue;
}

public sealed class Ulimits
{
    // name -> entry
    public Dictionary<string, UlimitEntry> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
}