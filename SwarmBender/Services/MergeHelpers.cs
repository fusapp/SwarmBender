using System.Text.Json;

namespace SwarmBender.Services;

/// <summary>
/// Utility helpers for merging YAML/JSON nodes represented as object graphs:
/// - Dictionaries are merged recursively
/// - Lists are replaced by the latter (simple rule to avoid accidental concatenation)
/// - Scalars are overwritten by the latter
/// Also provides helpers to normalize labels and environment structures.
/// </summary>
public static class MergeHelpers
{
    public static IDictionary<string, object?> DeepMerge(IDictionary<string, object?> baseMap, IDictionary<string, object?> overlay)
    {
        foreach (var kv in overlay)
        {
            if (kv.Value is IDictionary<string, object?> oMap)
            {
                if (baseMap.TryGetValue(kv.Key, out var existing) && existing is IDictionary<string, object?> bMap)
                {
                    baseMap[kv.Key] = DeepMerge(bMap, oMap);
                }
                else
                {
                    baseMap[kv.Key] = CloneMap(oMap);
                }
            }
            else
            {
                baseMap[kv.Key] = kv.Value;
            }
        }
        return baseMap;
    }

    public static IDictionary<string, object?> CloneMap(IDictionary<string, object?> map)
        => map.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    public static IDictionary<string, string> NormalizeLabels(object? node)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is null) return res;

        if (node is IDictionary<string, object?> map)
        {
            foreach (var kv in map)
                res[kv.Key] = kv.Value?.ToString() ?? string.Empty;
        }
        else if (node is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                var s = item?.ToString() ?? "";
                var idx = s.IndexOf('=');
                if (idx > 0)
                    res[s[..idx]] = s[(idx+1)..];
                else if (!string.IsNullOrWhiteSpace(s))
                    res[s] = string.Empty;
            }
        }
        return res;
    }

    public static object LabelsToYaml(IDictionary<string, string> labels)
        => labels.ToDictionary(k => k.Key, v => (object?)v.Value, StringComparer.OrdinalIgnoreCase);

    public static IDictionary<string, string> NormalizeEnvironment(object? node)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is null) return res;

        if (node is IDictionary<string, object?> map)
        {
            foreach (var kv in map)
                res[kv.Key] = kv.Value?.ToString() ?? string.Empty;
        }
        else if (node is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                var s = item?.ToString() ?? "";
                var idx = s.IndexOf('=');
                if (idx > 0)
                    res[s[..idx]] = s[(idx+1)..];
                else if (!string.IsNullOrWhiteSpace(s))
                    res[s] = string.Empty;
            }
        }
        return res;
    }

    public static object EnvironmentToYaml(IDictionary<string, string> env)
        => env.ToDictionary(k => k.Key, v => (object?)v.Value, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> ReadJsonKeys(JsonElement el, string prefix = "")
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var name = string.IsNullOrEmpty(prefix) ? prop.Name : prefix + "__" + prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var k in ReadJsonKeys(prop.Value, name))
                        yield return k;
                }
                else
                {
                    yield return name;
                }
            }
        }
    }
}
