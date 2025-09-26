using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

/// <summary>
/// Represents 'external' which can be either:
///   - a boolean: external: true|false
///   - a mapping: external: { name: actual_resource_name }
/// </summary>
public sealed class ExternalDef
{
    [YamlMember(Alias = "__bool__", ApplyNamingConventions = false)]
    public bool? AsBool { get; set; }

    [YamlMember(Alias = "name", ApplyNamingConventions = false)]
    public string? Name { get; set; }

    public static ExternalDef FromBool(bool v) => new() { AsBool = v };
    public static ExternalDef FromName(string name) => new() { AsBool = true, Name = name };

    public bool IsExternal => AsBool == true || !string.IsNullOrWhiteSpace(Name);
}