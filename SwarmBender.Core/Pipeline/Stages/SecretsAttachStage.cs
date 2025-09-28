using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;
using SwarmBender.Core.Util;
using System.Linq;

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

            var matchers = secretize.Paths.Select(SecretUtil.WildcardToRegex).ToArray();

            foreach (var (svcName, svc) in services)
            {
                ct.ThrowIfCancellationRequested();

                var envMap = ToDictionary(svc.Environment);
                if (envMap.Count == 0) continue;

                // 1) Eşleşen key'leri topla: hem orijinal hem kanonik form matcher'dan geçebilir
                var matchedRaw = envMap.Keys.Where(k =>
                {
                    var canon = SecretUtil.ToComposeCanon(k);
                    return matchers.Any(rx => rx.IsMatch(k)) || matchers.Any(rx => rx.IsMatch(canon));
                });

                // 2) "." ve "__" varyantı birlikteyse "__" olanı tercih et
                var matchedKeys = SecretUtil.CanonicalizeKeys(matchedRaw);
                if (matchedKeys.Count == 0) continue;

                svc.Secrets ??= new List<ServiceSecretRef>();

                foreach (var key in matchedKeys)
                {
                    var keyCanon = SecretUtil.ToComposeCanon(key);

                    // Değeri güvenli al: önce orijinal, yoksa kanonik
                    string value = string.Empty;
                    if (!envMap.TryGetValue(key, out value) && !envMap.TryGetValue(keyCanon, out value))
                        value = string.Empty;

                    // External secret adı (service adı template'e bağlı olarak kullanılabilir/ kullanılmayabilir)
                    var externalName = SecretUtil.BuildExternalName(
                        secretsCfg?.NameTemplate,
                        ctx.Request.StackId,
                        svcName,
                        ctx.Request.Env,
                        keyCanon,
                        value,
                        secretsCfg?.VersionMode
                    );

                    // Root-level external secret kaydı (idempotent)
                    if (!ctx.Working.Secrets.ContainsKey(externalName))
                    {
                        ctx.Working.Secrets[externalName] = new Data.Compose.Secret
                        {
                            External = ExternalDef.FromBool(true),
                            Name = externalName
                        };
                    }

                    // Servise bir kez ekle (Source'e göre)
                    bool alreadyAttached = svc.Secrets.Exists(s =>
                        s.Source.Equals(externalName, StringComparison.OrdinalIgnoreCase));

                    if (!alreadyAttached)
                    {
                        svc.Secrets.Add(new ServiceSecretRef
                        {
                            Source = externalName,
                            Target = keyCanon, // compose kanonu → runtime'da file olarak /run/secrets/<Target>
                            Mode = 292 // 0444
                        });
                    }

                    // Ortamdan sızdırmayı engelle: hem orijinal hem kanonik anahtarı temizle
                    envMap.Remove(key);
                    envMap.Remove(keyCanon);
                }

                // Kalan env'i geriye yaz
                svc.Environment = ListOrDict.FromMap(envMap);

                // Güvenlik: secret referanslarını Source'a göre tekilleştir
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
    }
}