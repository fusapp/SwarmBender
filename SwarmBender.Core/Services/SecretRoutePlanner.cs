using System.Text.RegularExpressions;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public static class SecretRoutePlanner
{
    public static RoutePlan Build(
        IReadOnlyList<SecretRouteRule> rules,
        string fallbackPathTemplate,
        string stackId,
        string envSlug,
        string key)
    {
        // İlk eşleşen kural
        foreach (var r in rules ?? Array.Empty<SecretRouteRule>())
        {
            if (r.Match is { Count: > 0 } && r.Match.Any(p => Glob(key, p)))
                return ToPlan(r, stackId, envSlug);
        }

        // Fallback: tek pathTemplate’i hem read hem write olarak kullan
        var write = Expand(fallbackPathTemplate, stackId, envSlug);
        return new RoutePlan(new List<string> { write }, write, false);

        static RoutePlan ToPlan(SecretRouteRule r, string stackId, string envSlug)
        {
            string write = Expand(r.WritePath ?? "/", stackId, envSlug);
            var reads = (r.ReadPaths ?? new List<string> { write })
                .Select(p => Expand(p, stackId, envSlug))
                .ToList();
            return new RoutePlan(reads, write, r.Migrate ?? false);
        }

        static string Expand(string t, string stackId, string envSlug)
        {
            if (string.IsNullOrWhiteSpace(t)) t = "/";
            t = t.Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                .Replace("{env}", envSlug, StringComparison.OrdinalIgnoreCase);
            return t.StartsWith("/") ? t : "/" + t;
        }
    }

    private static bool Glob(string text, string pattern)
    {
        if (pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }
    
    
    
}

public sealed record RoutePlan(
    IReadOnlyList<string> ReadPaths,
    string WritePath,
    bool Migrate);