using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Loads YAML files.</summary>
public sealed class YamlLoader : IYamlLoader
{
    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();

    public async Task<IDictionary<string, object?>> LoadYamlAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return new Dictionary<string, object?>();

        var text = await File.ReadAllTextAsync(filePath, ct);
        var obj = Deserializer.Deserialize<object?>(text);
        return ConvertToDictionary(obj) ?? new Dictionary<string, object?>();
    }

    public async Task<object?> LoadYamlUntypedAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return null;

        var text = await File.ReadAllTextAsync(filePath, ct);
        return Deserializer.Deserialize<object?>(text);
    }

    private static IDictionary<string, object?>? ConvertToDictionary(object? obj)
    {
        if (obj is null) return null;

        if (obj is IDictionary<object, object> dict)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in dict)
            {
                var key = kv.Key?.ToString() ?? string.Empty;
                result[key] = ConvertNode(kv.Value);
            }
            return result;
        }
        return null;
    }

    private static object? ConvertNode(object? node)
    {
        if (node is null) return null;

        if (node is IDictionary<object, object> d)
        {
            var res = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d)
                res[kv.Key?.ToString() ?? ""] = ConvertNode(kv.Value);
            return res;
        }
        if (node is IList<object> list)
        {
            return list.Select(ConvertNode).ToList();
        }
        return node;
    }
}
