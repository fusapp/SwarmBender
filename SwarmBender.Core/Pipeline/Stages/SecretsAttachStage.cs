using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;
using SwarmBender.Core.Util;
using System.Linq; // <-- LINQ

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

            var secretize  = ctx.Config?.Secretize;
            var secretsCfg = ctx.Config?.Secrets;

            if (secretize is not { Enabled: true } || secretize.Paths is null || secretize.Paths.Count == 0)
                return Task.CompletedTask;

            var services = ctx.Working.Services;
            if (services is null || services.Count == 0)
                return Task.CompletedTask;

            // root-level secrets dict must be Dictionary<string, Secret>
            ctx.Working.Secrets ??= new Dictionary<string, Data.Compose.Secret>(StringComparer.OrdinalIgnoreCase);

            var patterns = secretize.Paths.Select(SecretUtil.WildcardToRegex).ToArray();

            foreach (var (svcName, svc) in services)
            {
                ct.ThrowIfCancellationRequested();

                var envMap = ToDictionary(svc.Environment);
                if (envMap.Count == 0) continue;

                // Match + canonicalize (prefer "__" variant if both "A.B" and "A__B" exist)
                var matchedRaw = envMap.Keys.Where(k => patterns.Any(rx => rx.IsMatch(k)));
                var matchedKeys = CanonicalizeKeys(matchedRaw);

                if (matchedKeys.Count == 0) continue;

                svc.Secrets ??= new List<ServiceSecretRef>();

                foreach (var key in matchedKeys)
                {
                    var value = envMap[key] ?? string.Empty;

                    var externalName = SecretUtil.BuildExternalName(
                        secretsCfg?.NameTemplate,
                        ctx.Request.StackId,
                        svcName,
                        ctx.Request.Env,
                        key,
                        value,
                        secretsCfg?.VersionMode
                    );

                    // Register root external secret (idempotent)
                    if (!ctx.Working.Secrets.ContainsKey(externalName))
                    {
                        ctx.Working.Secrets[externalName] = new Data.Compose.Secret
                        {
                            // Compose spec: external can be "true" or an object that carries name.
                            // Our ExternalDef.FromName sets AsBool=true and Name=<externalName>.
                            External = ExternalDef.FromBool(true),
                            Name = externalName
                            // Secret.Name left null (external name is already carried in External).
                        };
                    }

                    // Attach once
                    bool alreadyAttached = svc.Secrets.Exists(s =>
                        s.Source.Equals(externalName, StringComparison.OrdinalIgnoreCase));

                    if (!alreadyAttached)
                    {
                        svc.Secrets.Add(new ServiceSecretRef
                        {
                            Source = externalName,
                            Target = key,
                            Mode = 288 // 0440
                        });
                    }

                    // Remove plain env value (do not leak)
                    envMap.Remove(key);
                }

                // Write back remaining env as map
                svc.Environment = ListOrDict.FromMap(envMap);

                // Safety: dedupe secret refs by Source
                if (svc.Secrets.Count > 1)
                {
                    svc.Secrets = svc.Secrets
                        .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();
                }
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

        // Prefer "__" version if both "A.B" and "A__B" exist.
        private static List<string> CanonicalizeKeys(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            if (list.Count <= 1) return list;

            var underscoreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in list)
                if (k.Contains("__"))
                    underscoreSet.Add(k);

            var result = new List<string>(list.Count);
            foreach (var k in list)
            {
                if (k.Contains('.'))
                {
                    var underscoreAlt = k.Replace(".", "__");
                    if (underscoreSet.Contains(underscoreAlt))
                        continue; // drop dot-form if __-form exists
                }
                result.Add(k);
            }

            // de-dup case-insensitively
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}