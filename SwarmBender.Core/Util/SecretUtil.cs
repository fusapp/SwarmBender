using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmBender.Core.Util;

internal static class SecretUtil
{
    public static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public static string VersionSuffix(string value, string? mode)
        => string.Equals(mode, "content-sha", StringComparison.OrdinalIgnoreCase)
            ? ShortSha256(value, 16)
            : "v1";

    public static string MakeExternalName(string? template, string stackId, string svcName, string env, string key, string version)
        => (template ?? "sb_{scope}_{env}_{key}_{version}")
            .Replace("{scope}",   $"{stackId}_{svcName}", StringComparison.OrdinalIgnoreCase)
            .Replace("{env}",     env,                    StringComparison.OrdinalIgnoreCase)
            .Replace("{key}",     key,                    StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version,                StringComparison.OrdinalIgnoreCase);

    public static string ShortSha256(string content, int hexLen)
    {
        using var sha = SHA256.Create();
        var hex = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty)));
        return hex.Substring(0, Math.Clamp(hexLen, 4, hex.Length));
    }
}