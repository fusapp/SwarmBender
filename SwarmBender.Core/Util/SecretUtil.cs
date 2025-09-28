using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmBender.Core.Util;

internal static class SecretUtil
{
    // ---- Public API --------------------------------------------------------

    /// <summary>
    /// Dış secret adını üretir.
    /// Varsayılan şablon: "{stackId}_{key}{_version}"
    /// - {key}          : external key (ConnectionStrings_MSSQL_Master)
    /// - {key_compose}  : compose kanonu (ConnectionStrings__MSSQL__Master)
    /// - {key_flat}     : düz noktalı (ConnectionStrings.MSSQL.Master)
    /// - {_version}     : version boşsa "" (eklenmez), doluysa "_{version}"
    /// - {version}      : versiyon değeri (başında '_' yok)
    /// Geri uyumluluk için {scope}, {service}, {env} token’ları da desteklenir.
    /// </summary>
    public static string BuildExternalName(
        string? nameTemplate,
        string stackId,
        string serviceName,
        string env,
        string key,
        string value,
        string? versionMode)
    {
        var envNorm = env?.Trim().ToLowerInvariant() ?? "dev";
        var svcNorm = serviceName?.Trim() ?? string.Empty;

        var keyOrig     = key?.Trim() ?? string.Empty;
        var keyCompose  = ToComposeCanon(keyOrig);
        var keyFlat     = ToFlatCanon(keyOrig);
        var keyExternal = ToExternalKey(keyOrig);

        var version = VersionSuffix(value ?? string.Empty, versionMode);

        return MakeExternalName(
            template: nameTemplate,
            stackId: stackId,
            service: svcNorm,
            env: envNorm,
            keyExternal: keyExternal,
            keyCompose: keyCompose,
            keyFlat: keyFlat,
            version: version
        );
    }

    /// <summary>
    /// Aynı anahtarın "." ve "__" varyantları birlikte geldiyse "__" olanı tercih et.
    /// </summary>
    public static List<string> CanonicalizeKeys(IEnumerable<string> keys)
    {
        var list = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count <= 1) return list;

        var underscoreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in list)
            if (k.Contains("__")) underscoreSet.Add(k);

        var result = new List<string>(list.Count);
        foreach (var k in list)
        {
            if (k.Contains('.'))
            {
                var underscoreAlt = k.Replace(".", "__");
                if (underscoreSet.Contains(underscoreAlt))
                    continue; // "__" varsa "." formunu at
            }
            result.Add(k);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Compose/env için kanon: '.' → '__'</summary>
    public static string ToComposeCanon(string key)
        => string.IsNullOrWhiteSpace(key) ? "" : key.Replace(".", "__");

    /// <summary>Düz noktalı kanon: '__' → '.'</summary>
    public static string ToFlatCanon(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        return key.Contains("__", StringComparison.Ordinal)
            ? key.Replace("__", ".")
            : key;
    }

    /// <summary>
    /// Dış-secret adı için anahtar: segmentleri tek alt çizgi ile birleştir.
    /// ("ConnectionStrings__MSSQL__Master" → "ConnectionStrings_MSSQL_Master")
    /// ("ConnectionStrings.MSSQL.Master" → aynı sonuç)
    /// </summary>
    public static string ToExternalKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        string[] parts = key.Contains("__", StringComparison.Ordinal)
            ? key.Split(new[] {"__"}, StringSplitOptions.RemoveEmptyEntries)
            : key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", parts);
    }

    public static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Version değeri: "content-sha" → kısa SHA; "none"/boş → "";
    /// Diğerleri → "v1" (geri uyum).
    /// </summary>
    public static string VersionSuffix(string value, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "";

        if (mode.Equals("content-sha", StringComparison.OrdinalIgnoreCase))
            return ShortSha256(value, 16);

        // default/legacy: v1
        return "v1";
    }

    // ---- Internal helpers ---------------------------------------------------

    /// <summary>
    /// Şablondan dış-secret adı üretimi. Varsayılan "{stackId}_{key}{_version}".
    /// </summary>
    private static string MakeExternalName(
        string? template,
        string stackId,
        string service,
        string env,
        string keyExternal,
        string keyCompose,
        string keyFlat,
        string version)
    {
        // Yeni varsayılan: <stackId>_<key>[_<version>]
        var t = string.IsNullOrWhiteSpace(template)
            ? "{stackId}_{key}{_version}"
            : template;

        // {_version} akıllı ek: version boşsa hiç ekleme
        var withVersion = string.IsNullOrEmpty(version) ? "" : "_" + version;

        // Eski {scope} için geri uyum (stackId_service)
        var scope = string.IsNullOrEmpty(service) ? stackId : $"{stackId}_{service}";

        var raw = t.Replace("{scope}", scope, StringComparison.OrdinalIgnoreCase)
                   .Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                   .Replace("{service}", service, StringComparison.OrdinalIgnoreCase)
                   .Replace("{env}", env, StringComparison.OrdinalIgnoreCase)
                   .Replace("{key_compose}", keyCompose, StringComparison.OrdinalIgnoreCase)
                   .Replace("{key_flat}", keyFlat, StringComparison.OrdinalIgnoreCase)
                   .Replace("{key}", keyExternal, StringComparison.OrdinalIgnoreCase)
                   .Replace("{_version}", withVersion, StringComparison.OrdinalIgnoreCase)
                   .Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

        return SanitizeDockerSecretName(raw);
    }

    /// <summary>
    /// Docker secret adı kuralları: sadece [A-Za-z0-9-_.], baş/son alnum, makul uzunluk.
    /// </summary>
    private static string SanitizeDockerSecretName(string raw)
    {
        // 1) illegal karakterleri '_' yap
        var sanitized = Regex.Replace(raw, @"[^A-Za-z0-9\-_.]", "_");

        // 2) çok uzunsa sıkıştır (64 yeterli; gerekirse arttırılabilir)
        const int maxLen = 64;
        if (sanitized.Length > maxLen)
        {
            var head = sanitized.Substring(0, 24);
            var tail = sanitized.Substring(sanitized.Length - 24);
            var mid = sanitized.Substring(24, sanitized.Length - 48);
            sanitized = $"{head}{ShortSha256(mid, 8)}{tail}";
            if (sanitized.Length > maxLen)
                sanitized = sanitized.Substring(0, maxLen);
        }

        // 3) baş/son alfanumerik olsun
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