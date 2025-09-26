
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Secretizes selected env keys (by config.secretize.paths):
    /// - Removes matched keys from service environment.
    /// - Creates root-level external secrets using nameTemplate/versionMode.
    /// - Attaches secret references to affected services (last-wins).
    /// </summary>
    public sealed class SecretsAttachStage : IRenderStage
    {
        public int Order => 650;

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null. LoadTemplateStage must run before SecretsAttachStage.");

            var secretize = ctx.Config?.Secretize;
            var secretsCfg = ctx.Config?.Secrets;

            if (secretize is not { Enabled: true } || secretize.Paths is null || secretize.Paths.Count == 0)
                return Task.CompletedTask;

            var services = ctx.Working.Services;
            if (services is null || services.Count == 0)
                return Task.CompletedTask;

            // root-level secrets dict must be Dictionary<string, Secret>
            ctx.Working.Secrets ??= new Dictionary<string, Data.Compose.Secret>(StringComparer.OrdinalIgnoreCase);

            var patterns = secretize.Paths.Select(WildcardToRegex).ToArray();

            foreach (var (svcName, svc) in services)
            {
                ct.ThrowIfCancellationRequested();

                var envMap = ToDictionary(svc.Environment);
                if (envMap.Count == 0) continue;

                var matchedKeys = envMap.Keys.Where(k => patterns.Any(rx => rx.IsMatch(k))).ToList();
                if (matchedKeys.Count == 0) continue;

                svc.Secrets ??= new List<ServiceSecretRef>();

                foreach (var key in matchedKeys)
                {
                    
                    var value = envMap[key] ?? string.Empty;

                    var versionSuffix =
                        secretsCfg?.VersionMode?.Equals("content-sha", StringComparison.OrdinalIgnoreCase) == true
                        ? ShortSha256(value, 16)
                        : "v1";

                    var externalName = (secretsCfg?.NameTemplate ?? "sb_{scope}_{env}_{key}_{version}")
                        .Replace("{scope}",   $"{ctx.Request.StackId}_{svcName}", StringComparison.OrdinalIgnoreCase)
                        .Replace("{env}",     ctx.Request.Env,                    StringComparison.OrdinalIgnoreCase)
                        .Replace("{key}",     key,                                StringComparison.OrdinalIgnoreCase)
                        .Replace("{version}", versionSuffix,                      StringComparison.OrdinalIgnoreCase);

                    // Register root external secret (idempotent)
                    if (!ctx.Working.Secrets.ContainsKey(externalName))
                    {
                        ctx.Working.Secrets[externalName] = new Data.Compose.Secret()
                        {
                            // Compose spec: external can be "true" or an object that carries name.
                            // Our ExternalDef.FromName sets AsBool=true and Name=<externalName>.
                            External = ExternalDef.FromName(externalName),
                            // Secret.Name left null (external name is already carried in External).
                        };
                    }

                    svc.Secrets ??= new List<ServiceSecretRef>();
                    bool alreadyAttached = svc.Secrets.Exists(s =>
                        s.Source.Equals(externalName, StringComparison.OrdinalIgnoreCase));

                    if (!alreadyAttached)
                    {
                        svc.Secrets.Add(new ServiceSecretRef
                        {
                            Source = externalName,
                            Target = null,
                            Mode = 288 // 0440
                        });
                    }

                    envMap.Remove(key);
                }

                // Write back remaining env as map
                svc.Environment = ListOrDict.FromMap(envMap);
            }

            return Task.CompletedTask;
        }

        private static Dictionary<string, string> ToDictionary(ListOrDict? env)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (env is null) return dict;

            if (env.AsMap is not null)
            {
                foreach (var kv in env.AsMap)
                    dict[kv.Key] = kv.Value ?? string.Empty;
                return dict;
            }

            if (env.AsList is not null)
            {
                foreach (var item in env.AsList)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    var idx = item.IndexOf('=', StringComparison.Ordinal);
                    if (idx < 0) dict[item.Trim()] = string.Empty;
                    else
                    {
                        var k = item[..idx].Trim();
                        var v = item[(idx + 1)..];
                        if (!string.IsNullOrEmpty(k)) dict[k] = v;
                    }
                }
            }

            return dict;
        }

        private static Regex WildcardToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private static string ShortSha256(string content, int hexLen)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty));
            var hex = Convert.ToHexString(bytes);
            return hex.Substring(0, Math.Clamp(hexLen, 4, hex.Length));
        }
    }
}