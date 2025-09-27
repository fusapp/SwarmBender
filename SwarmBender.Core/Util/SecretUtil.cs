using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmBender.Core.Util;

internal static class SecretUtil
{
    
    public static string BuildExternalName(
        string? nameTemplate,
        string stackId,
        string serviceName,
        string env,
        string key,
        string value,
        string? versionMode)
    {
        // Normalize – HER İKİ tarafta aynı kural:
        var envNorm = env?.Trim().ToLowerInvariant() ?? "dev";
        var svcNorm = serviceName?.Trim() ?? "";
        var keyNorm = key?.Trim() ?? "";

        var versionSuffix = SecretUtil.VersionSuffix(value ?? string.Empty, versionMode);

        // Varolan MakeNameWithDockerFallback mantığını burada topla:
        return SecretUtil.MakeNameWithDockerFallback(
            nameTemplate,
            stackId,
            svcNorm,
            envNorm,
            keyNorm,
            versionSuffix
        );
    }
    
    public static string ToComposeCanon(string key)
        => string.IsNullOrWhiteSpace(key) ? ""
            : key.Replace(".", "__");
    
    public static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public static string VersionSuffix(string value, string? mode)
        => string.Equals(mode, "content-sha", StringComparison.OrdinalIgnoreCase)
            ? ShortSha256(value, 16)
            : "v1";

    // Back-compat helper: serviceScope = $"{stackId}_{service}"
    // Yeni template {stackId}_{env}_{key}_{version} kullanıldığı için service opsiyonel.
    public static string MakeNameWithDockerFallback(
        string? template,
        string stackId,
        string service,        // kullanılmayabilir; template belirler
        string env,
        string key,
        string version)
    {
        var t = string.IsNullOrWhiteSpace(template)
            ? "sb_{stackId}_{env}_{key}_{version}"
            : template;

        // support both {scope} and individual tokens
        var scope = $"{stackId}_{service}";

        var raw = t.Replace("{scope}", scope, StringComparison.OrdinalIgnoreCase)
                   .Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                   .Replace("{service}", service, StringComparison.OrdinalIgnoreCase)
                   .Replace("{env}", env, StringComparison.OrdinalIgnoreCase)
                   .Replace("{key}", key, StringComparison.OrdinalIgnoreCase)
                   .Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

        // Docker secret name rules:
        // - allowed: [a-zA-Z0-9-_.]
        // - max 64 chars
        // - start/end must be alnum
        // 1) sanitize chars
        var sanitized = Regex.Replace(raw, @"[^A-Za-z0-9\-_.]", "_");

        // 2) compress if too long (keep head/tail, hash middle)
        const int maxLen = 64;
        if (sanitized.Length > maxLen)
        {
            // keep prefix/suffix and hash the rest
            var head = sanitized.Substring(0, 24);
            var tail = sanitized.Substring(sanitized.Length - 24);
            var mid = sanitized.Substring(24, sanitized.Length - 48);
            sanitized = $"{head}{ShortSha256(mid, 8)}{tail}";
            if (sanitized.Length > maxLen)
                sanitized = sanitized.Substring(0, maxLen);
        }

        // 3) ensure start/end alnum
        sanitized = TrimToAlnumEdges(sanitized);
        if (sanitized.Length == 0) sanitized = "sb_secret_" + ShortSha256(raw, 8);

        return sanitized;
    }

    public static string ShortSha256(string content, int hexLen)
    {
        using var sha = SHA256.Create();
        var hex = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty)));
        return hex.Substring(0, Math.Clamp(hexLen, 4, hex.Length));
    }

    private static string TrimToAlnumEdges(string s)
    {
        int i = 0, j = s.Length - 1;
        while (i <= j && !char.IsLetterOrDigit(s[i])) i++;
        while (j >= i && !char.IsLetterOrDigit(s[j])) j--;
        if (i > j) return string.Empty;
        return s.Substring(i, j - i + 1);
    }
}