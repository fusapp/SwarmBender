using System.Text.RegularExpressions;
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;


public sealed class SecretNameStrategy : ISecretNameStrategy
{
    private static readonly Regex UnsafeRx = new(@"[^A-Za-z0-9_]+", RegexOptions.Compiled);

    public string BuildName(string scope, string env, string key, string versionSuffix, string nameTemplate)
    {
        string sanitize(string s) => UnsafeRx.Replace(s ?? string.Empty, "_").Trim('_');
        var result = nameTemplate
            .Replace("{scope}", sanitize(scope))
            .Replace("{env}", sanitize(env))
            .Replace("{key}", sanitize(key))
            .Replace("{version}", sanitize(versionSuffix));
        return result;
    }
}