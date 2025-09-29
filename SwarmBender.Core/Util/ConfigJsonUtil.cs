using System.Text.Json;

namespace SwarmBender.Core.Util;


internal static class ConfigJsonUtil
{
    public static string UnflattenToJson(
        IDictionary<string, string> flat,
        bool indented = true)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var (k, v) in flat)
            Insert(root, SplitKey(k), ParseScalar(v));

        var normalized = DictsWithSequentialNumericKeysToArrays(root);
        var opts = new JsonSerializerOptions { WriteIndented = indented };
        return JsonSerializer.Serialize(normalized, opts);
    }

    // --- helpers ---

    private static string[] SplitKey(string key)
        => key.Split(new[] { "__" }, StringSplitOptions.None);

    private static void Insert(
        IDictionary<string, object> node,
        IReadOnlyList<string> parts,
        object value,
        int idx = 0)
    {
        var part = parts[idx];

        if (idx == parts.Count - 1)
        {
            // leaf
            node[part] = value;
            return;
        }

        if (!node.TryGetValue(part, out var child) ||
            child is not IDictionary<string, object> dictChild)
        {
            dictChild = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            node[part] = dictChild;
        }

        Insert(dictChild, parts, value, idx + 1);
    }

    private static object DictsWithSequentialNumericKeysToArrays(object obj)
    {
        if (obj is IDictionary<string, object> map)
        {
            // önce altları dönüştür
            var keys = map.Keys.ToList();
            foreach (var k in keys)
                map[k] = DictsWithSequentialNumericKeysToArrays(map[k]);

            // tüm anahtarlar sayısal mı?
            var numeric = new List<(int n, object v)>();
            foreach (var k in map.Keys)
            {
                if (int.TryParse(k, out var n))
                    numeric.Add((n, map[k]));
                else
                    return map; // sayısal olmayan anahtar var -> dizi değil
            }

            if (numeric.Count == 0) return map;

            // 0..max aralığı eksiksiz mi?
            var max = numeric.Max(t => t.n);
            if (numeric.Count != max + 1) return map;

            // sırayla listeye dönüştür
            var arr = new List<object>(new object[max + 1]);
            foreach (var (n, v) in numeric)
                arr[n] = v;

            return arr;
        }

        if (obj is IEnumerable<object> list)
        {
            // list içindekileri de normalize et
            return list.Select(DictsWithSequentialNumericKeysToArrays).ToList();
        }

        return obj;
    }

    private static object ParseScalar(string s)
    {
        // bool
        if (bool.TryParse(s, out var b)) return b;
        // int
        if (int.TryParse(s, out var i)) return i;
        // long
        if (long.TryParse(s, out var l)) return l;
        // double/decimal istersen buraya eklenebilir; çoğu config’te string kalması daha güvenli.
        return s;
    }
}