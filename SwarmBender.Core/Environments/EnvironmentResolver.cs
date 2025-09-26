using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Environments;

/// <summary>
/// env_file + environment (list|map) birleşimi; last-wins.
/// Çoklu env_file varsa soldan sağa yüklenir, her biri bir sonrakini override edebilir.
/// </summary>
public static class EnvironmentResolver
{
    public static Dictionary<string,string> ResolveForService(
        Service svc,
        string basePath,           // compose dosyasının bulunduğu dizin
        IFileSystem fs)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

        // 1) env_file
        foreach (var path in EnumerateEnvFiles(svc.EnvFile))
        {
            var full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(basePath, path);
            if (!fs.FileExists(full)) continue;
            var content = fs.ReadAllTextAsync(full,CancellationToken.None).GetAwaiter().GetResult();
            var parsed = EnvFileParser.Parse(content);
            foreach (var kv in parsed) result[kv.Key] = kv.Value;
        }

        // 2) environment (list | map) -> last wins
        if (svc.Environment is { } env)
        {
            if (env.AsMap is { } map)
            {
                foreach (var kv in map) result[kv.Key] = kv.Value ?? string.Empty;
            }
            else if (env.AsList is { } list)
            {
                foreach (var item in list)
                {
                    // desteklenen: KEY=V | KEY (empty) | "KEY=V with ="
                    var idx = item.IndexOf('=');
                    if (idx < 0) { result[item] = string.Empty; continue; }
                    var key = item.Substring(0, idx);
                    var val = item.Substring(idx + 1);
                    result[key] = val;
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateEnvFiles(ListOrString? envFile)
    {
        if (envFile is null) yield break;
        if (envFile.AsString is { } s) { yield return s; yield break; }
        if (envFile.AsList is { } list)
            foreach (var f in list) yield return f;
    }
}