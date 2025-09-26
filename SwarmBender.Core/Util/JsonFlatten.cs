using System.Text.Json;

namespace SwarmBender.Core.Util;

/// <summary>Flattens JSON objects to "A__B__C" keys (Docker-style double underscore).</summary>
public static class JsonFlatten
{
    public static IDictionary<string, string> Flatten(JsonElement root)
    {
        var bag = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        Recurse(root, prefix: null, bag);
        return bag;
    }

    private static void Recurse(JsonElement node, string? prefix, IDictionary<string, string> bag)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}__{prop.Name}";
                    Recurse(prop.Value, key, bag);
                }
                break;
            case JsonValueKind.Array:
                // Represent array as JSON string (verbatim) to preserve structure
                bag[prefix ?? ""] = node.GetRawText();
                break;
            case JsonValueKind.String:
                bag[prefix ?? ""] = node.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                bag[prefix ?? ""] = node.GetRawText();
                break;
        }
    }
}