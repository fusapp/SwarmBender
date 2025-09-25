namespace SwarmBender.Core.Util;

/// <summary>Matches flattened keys against secretize patterns.</summary>
public static class SecretMatch
{
    public static bool IsSecret(string key, IEnumerable<string> globs)
    {
        foreach (var g in globs)
            if (Glob(g, key)) return true;
        return false;
    }

    private static bool Glob(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        int pi = 0, ti = 0, star = -1, match = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti])))
            { pi++; ti++; continue; }

            if (pi < pattern.Length && pattern[pi] == '*')
            { star = pi++; match = ti; continue; }

            if (star != -1)
            { pi = star + 1; ti = ++match; continue; }

            return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }
}