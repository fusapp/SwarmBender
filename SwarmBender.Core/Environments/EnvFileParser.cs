using System.Text;

namespace SwarmBender.Core.Environments;

/// <summary>
/// Compose .env dosyası kurallarına yakın, pratik parser:
/// - Yorum: '#' satır başında veya boşluk sonrası (tırnak dışı)
/// - 'export ' öneki destekli (yok sayılır)
/// - Anahtar: ENV_KEY (a–Z,0–9,_)
/// - Değer: raw | "quoted" (escape: \n, \t, \", \\) | 'single-quoted' (literal)
/// - Boş değer: KEY=  veya sadece KEY (empty)
/// </summary>
public static class EnvFileParser
{
    public static Dictionary<string,string> Parse(string content)
    {
        var dict = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        using var sr = new StringReader(content);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#')) continue;

            // 'export ' öneki
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                trimmed = trimmed.Substring(7).TrimStart();

            // yorumları tırnak dışı kes: KEY= "a # not comment"
            var effective = StripTrailingCommentOutsideQuotes(trimmed);
            if (effective.Length == 0) continue;

            // KEY[=VALUE]
            var eq = effective.IndexOf('=');
            string key, val;
            if (eq < 0)
            {
                key = effective.Trim();
                val = string.Empty;
            }
            else
            {
                key = effective.Substring(0, eq).Trim();
                val = effective.Substring(eq + 1);
                val = Unquote(val.Trim());
            }

            if (key.Length == 0) continue;
            dict[key] = val;
        }
        return dict;
    }

    private static string StripTrailingCommentOutsideQuotes(string s)
    {
        bool dq = false, sq = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' && !sq) dq = !dq;
            else if (c == '\'' && !dq) sq = !sq;
            else if (c == '#' && !dq && !sq)
                return s[..i].TrimEnd();
        }
        return s;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder();
            for (int i = 1; i < v.Length - 1; i++)
            {
                var c = v[i];
                if (c == '\\' && i + 1 < v.Length - 1)
                {
                    var n = v[++i];
                    sb.Append(n switch {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => n
                    });
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'')
            return v.Substring(1, v.Length - 2);
        return v;
    }
}