using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmBender.Core.Util;

internal static class SecretUtil
{
    private const int DockerMaxLen = 64;
    private static readonly Regex RxDockerSafe = new(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62})[A-Za-z0-9]$",
        RegexOptions.Compiled);

    /// <summary>Converts wildcard (*, ?) to case-insensitive regex.</summary>
    public static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Returns version suffix based on mode. For "content-sha" returns 16-hex, else "v1".</summary>
    public static string VersionSuffix(string value, string? mode)
        => string.Equals(mode, "content-sha", StringComparison.OrdinalIgnoreCase)
            ? ShortSha256(value, 16)
            : "v1";

    /// <summary>Simple template expansion (no length/charset validation).</summary>
    public static string MakeExternalName(string? template, string stackId, string svcName, string env, string key, string version)
        => (template ?? "sb_{scope}_{env}_{key}_{version}")
            .Replace("{scope}",   $"{stackId}_{svcName}", StringComparison.OrdinalIgnoreCase)
            .Replace("{env}",     env,                    StringComparison.OrdinalIgnoreCase)
            .Replace("{key}",     key,                    StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version,                StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the first hexLen chars of SHA-256(content) as uppercase hex.</summary>
    public static string ShortSha256(string content, int hexLen)
    {
        using var sha = SHA256.Create();
        var hex = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty)));
        return hex.Substring(0, Math.Clamp(hexLen, 4, hex.Length));
    }

    /// <summary>
    /// Preferred: Generates a Docker-safe (<=64 chars) secret name that’s stable and collision-resistant.
    /// Example: sb_sso_api_dev_0F3A7C21_D7AFC949
    /// - keyHash8 = SHA-256(key) first 8 hex
    /// - ver8     = shortened version to 8 hex (or hashed if not hex)
    /// </summary>
    public static string MakeDockerSafeName(
        string stackId,
        string serviceName,
        string env,
        string key,
        string version,
        string prefix = "sb")
    {
        var ver8 = ShortenHex(version, 8);
        var keyHash8 = ShortSha256(key, 8);

        var basePart = $"{stackId}_{serviceName}_{env}";
        var baseSlug = Slug(basePart);

        var fixedTail = $"_{keyHash8}_{ver8}";
        var head = $"{prefix}_";

        var maxBaseLen = DockerMaxLen - head.Length - fixedTail.Length;
        if (maxBaseLen < 1) maxBaseLen = 1;

        var trimmedBase = TrimMiddle(baseSlug, maxBaseLen);
        var candidate = head + trimmedBase + fixedTail;

        candidate = EnsureAlnumEdges(candidate);

        // Final hard clamp to 64 if needed (maintain tail uniqueness)
        if (candidate.Length > DockerMaxLen)
            candidate = candidate.Substring(0, DockerMaxLen);

        // If after clamp the end is non-alnum, fix it
        candidate = EnsureAlnumEdges(candidate);
        if (candidate.Length == 0) candidate = "x";

        return candidate;
    }

    /// <summary>
    /// Tries template first, and if it violates Docker constraints (charset/length/edges),
    /// falls back to MakeDockerSafeName.
    /// </summary>
    public static string MakeNameWithDockerFallback(
        string? template,
        string stackId,
        string serviceName,
        string env,
        string key,
        string version,
        string prefix = "sb")
    {
        var scope = $"{stackId}_{serviceName}";
        var templ = MakeExternalName(template, stackId, serviceName, env, key, version);

        // sanitize template: slug + edge fix
        var slug = Slug(templ);
        slug = EnsureAlnumEdges(slug);

        if (IsDockerSafeName(slug))
            return slug;

        // If too long or still invalid, fallback to robust generator
        return MakeDockerSafeName(stackId, serviceName, env, key, version, prefix);
    }

    /// <summary>Checks if name satisfies Docker secret rules.</summary>
    public static bool IsDockerSafeName(string name) => RxDockerSafe.IsMatch(name);

    /// <summary>Normalizes any string to a slug that only contains [A-Za-z0-9._-] and dedupes separators.</summary>
    public static string Slug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "x";
        var t = Regex.Replace(s, @"[^A-Za-z0-9._-]+", "_");
        t = Regex.Replace(t, @"[_\.-]{2,}", "_");
        t = t.Trim('_', '-', '.');
        return string.IsNullOrEmpty(t) ? "x" : t;
    }

    /// <summary>Keeps start and end alnum as required by Docker; strips non-alnum edges.</summary>
    public static string EnsureAlnumEdges(string s)
    {
        s = s.Trim();
        s = Regex.Replace(s, @"^[^A-Za-z0-9]+", "");
        s = Regex.Replace(s, @"[^A-Za-z0-9]+$", "");
        return s;
    }

    /// <summary>Middle-trims to maxLen using "..." in the middle if necessary.</summary>
    public static string TrimMiddle(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        if (maxLen <= 3) return s[..maxLen];
        var keep = maxLen - 3;
        var left = keep / 2;
        var right = keep - left;
        return s[..left] + "..." + s[^right..];
    }

    /// <summary>
    /// If input looks like hex, returns first toLen hex chars (upper).
    /// Otherwise, hashes the input and returns first toLen hex of SHA-256.
    /// </summary>
    public static string ShortenHex(string value, int toLen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new string('0', Math.Max(4, toLen));

        var onlyHex = Regex.Replace(value, "[^0-9A-Fa-f]", "");
        if (onlyHex.Length >= toLen)
            return onlyHex[..toLen].ToUpperInvariant();

        // Not enough hex → hash the original and take hex
        return ShortSha256(value, toLen);
    }
}