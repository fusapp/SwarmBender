namespace SwarmBender.Core.Util;

/// <summary>Deep merge of YAML-like objects. Right side wins. Lists are concatenated (can be refined per key).</summary>
public static class DeepMerge
{
    public static IDictionary<string, object?> Merge(IDictionary<string, object?> left, IDictionary<string, object?> right)
    {
        foreach (var kv in right)
        {
            if (left.TryGetValue(kv.Key, out var lv) && lv is IDictionary<string, object?> ld &&
                kv.Value is IDictionary<string, object?> rd)
            {
                Merge(ld, rd);
            }
            else
            {
                left[kv.Key] = kv.Value;
            }
        }
        return left;
    }
}