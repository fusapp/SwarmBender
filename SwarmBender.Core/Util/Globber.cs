using System.Text.RegularExpressions;

namespace SwarmBender.Core.Util;

public static class Globber
{
    public static string ExpandPlaceholders(string pattern) => pattern; // placeholders handled earlier

    public static bool IsMatch(string text, string pattern)
    {
        // convert a simple glob to regex
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase);
    }
}