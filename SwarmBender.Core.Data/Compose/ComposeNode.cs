using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>
/// All compose nodes derive here. Holds unknown/non-schema fields.
/// </summary>
public abstract class ComposeNode
{
    /// <summary>
    /// Non-schema or unknown fields captured for round-trip (including non x-* keys).
    /// </summary>
    [YamlIgnore] // will be populated by a custom converter in next step
    public Dictionary<string, object?> Custom { get; } = new();
}