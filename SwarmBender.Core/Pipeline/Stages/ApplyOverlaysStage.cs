using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose; // ComposeFile (root model)
using SwarmBender.Core.Util; // DeepMerge

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Applies overlays using contract-configured order (SbConfig.Render.OverlayOrder),
    /// or falls back to defaults if not provided. Files are applied in ascending
    /// alphabetical order per glob; last applied wins on collisions.
    /// </summary>
    public sealed class ApplyOverlaysStage : IRenderStage
    {
        public int Order => 200;

        private readonly IFileSystem _fs;
        private readonly IYamlEngine _yaml;

        public ApplyOverlaysStage(IFileSystem fs, IYamlEngine yaml)
        {
            _fs = fs;
            _yaml = yaml;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException(
                    "Working model is null. LoadTemplateStage must run before ApplyOverlaysStage.");

            var stackId = ctx.Request.StackId;
            var env = ctx.Request.Env;

            // 1) Overlay order from config; fallback to defaults
            var patterns = (ctx.Config?.Render?.OverlayOrder is { Count: > 0 } configured)
                ? configured
                : new List<string>
                {
                    "stacks/all/{env}/stack/*.y?(a)ml",
                    "stacks/{stackId}/{env}/stack/*.y?(a)ml"
                };

            // 2) Resolve placeholders and collect files (pattern order preserved)
            var allFilesOrdered = new List<string>();
            foreach (var patt in patterns)
            {
                var resolved = patt
                    .Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{env}", env, StringComparison.OrdinalIgnoreCase);

                foreach (var mask in ExpandMasks(resolved))
                {
                    foreach (var f in _fs.GlobFiles(ctx.RootPath, mask))
                        allFilesOrdered.Add(f);
                }
            }

            // 3) Apply overlays in order (last wins)
            foreach (var file in allFilesOrdered)
            {
                ct.ThrowIfCancellationRequested();

                var overlay = await _yaml.LoadYamlAsync<ComposeFile>(file, ct).ConfigureAwait(false);
                if (overlay is null) continue;

                // 3a) Spread wildcard ("*") service overlay onto each concrete service
                ApplyWildcardServicesOverlay(ctx.Working, overlay);

                // 3b) Shallow merge top-level nodes (services/configs/secrets/networks/volumes, vb.)
                ShallowTopLevelMerge(ctx.Working, overlay);

                // (opsiyonel) applied listesi tutuluyorsa ekle:
                // ctx.AppliedOverlays?.Add(file);
            }
        }

        // Supports "*.y?(a)ml" by expanding to both *.yml and *.yaml.
// Returns masks relative to repo root so that IFileSystem.GlobFiles(root, mask) çalışsın.
        private static IEnumerable<string> ExpandMasks(string mask)
        {
            if (mask.Contains("y?(a)ml", StringComparison.OrdinalIgnoreCase))
            {
                yield return mask.Replace("y?(a)ml", "yml");
                yield return mask.Replace("y?(a)ml", "yaml");
                yield break;
            }

            // Belirli bir uzantı verilmişse olduğu gibi bırak
            if (mask.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                mask.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                yield return mask;
                yield break;
            }

            // Aksi halde iki varyasyonu da dene
            yield return mask + ".yml";
            yield return mask + ".yaml";
        }

        /// <summary>
        /// Expands a glob pattern relative to the given root using IFileSystem.GlobFiles.
        /// Supports *.y?(a)ml by trying both yml and yaml masks while preserving
        /// the original order per pattern.
        /// </summary>
        private IEnumerable<string> ExpandFiles(string root, string pattern)
        {
            var absPattern = Path.IsPathRooted(pattern) ? pattern : Path.Combine(root, pattern);

            // Support for "y?(a)ml" -> try yml and yaml variants
            if (pattern.Contains("y?(a)ml", StringComparison.OrdinalIgnoreCase))
            {
                var pYml = absPattern.Replace("y?(a)ml", "yml", StringComparison.OrdinalIgnoreCase);
                var pYaml = absPattern.Replace("y?(a)ml", "yaml", StringComparison.OrdinalIgnoreCase);

                var list = new List<string>();
                list.AddRange(_fs.GlobFiles(root, MakeRelativeToRoot(root, pYml)));
                list.AddRange(_fs.GlobFiles(root, MakeRelativeToRoot(root, pYaml)));

                return list.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            }

            // Otherwise pass-through
            return _fs.GlobFiles(root, MakeRelativeToRoot(root, absPattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        }

        private static string MakeRelativeToRoot(string root, string absOrRel)
        {
            // IFileSystem.GlobFiles(root, pattern) expects a pattern relative to root;
            // if absolute came in and starts with root, trim the prefix.
            if (Path.IsPathRooted(absOrRel))
            {
                var normRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normPath = Path.GetFullPath(absOrRel);
                if (normPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = normPath.Substring(normRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return rel;
                }
            }

            return absOrRel;
        }

        /// <summary>
        /// Shallow top-level merge for typed ComposeFile:
        /// - IDictionary&lt;string, T&gt; props: add/overwrite keys from overlay
        /// - IList props: append overlay items
        /// - Scalar/complex refs: overwrite if overlay value != null
        /// </summary>
        private static void ShallowTopLevelMerge(ComposeFile target, ComposeFile overlay)
        {
            if (overlay is null) return;

            var tType = typeof(ComposeFile);
            foreach (var prop in tType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                var oVal = prop.GetValue(overlay);
                if (oVal is null) continue;

                var tVal = prop.GetValue(target);

                // IDictionary<string, ?>
                if (IsStringKeyDictionary(prop.PropertyType, out var dictTypes))
                {
                    // Ensure target dictionary exists
                    if (tVal is null)
                    {
                        prop.SetValue(target, oVal);
                        continue;
                    }

                    var tDict = (IDictionary)tVal;
                    var oDict = (IDictionary)oVal;
                    foreach (DictionaryEntry entry in oDict)
                    {
                        tDict[entry.Key] = entry.Value; // last-wins
                    }

                    continue;
                }

                // IList
                if (typeof(IList).IsAssignableFrom(prop.PropertyType))
                {
                    if (tVal is null)
                    {
                        prop.SetValue(target, oVal);
                        continue;
                    }

                    var tList = (IList)tVal;
                    var oList = (IList)oVal;
                    foreach (var item in oList) tList.Add(item);
                    continue;
                }

                // scalars/complex refs: overwrite
                prop.SetValue(target, oVal);
            }
        }

        private static bool IsStringKeyDictionary(Type type, out Type[] genArgs)
        {
            genArgs = Type.EmptyTypes;

            // Handle IDictionary<string, T> or concrete types implementing it
            var dictIface = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                ? type
                : type.GetInterfaces().FirstOrDefault(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

            if (dictIface is null) return false;

            var args = dictIface.GetGenericArguments();
            if (args.Length == 2 && args[0] == typeof(string))
            {
                genArgs = args;
                return true;
            }

            return false;
        }

        private static void ApplyWildcardServicesOverlay(ComposeFile working, ComposeFile overlay)
        {
            if (overlay?.Services is null || working?.Services is null) return;

            // "*" pseudo-service present?
            if (!overlay.Services.TryGetValue("*", out var wildcard) || wildcard is null)
                return;

            foreach (var (svcName, svc) in working.Services)
            {
                if (svc is null) continue;
                MergeServiceInto(svc, wildcard);
            }

            // remove wildcard from overlay so that later shallow merge doesn't add it back
            overlay.Services.Remove("*");
        }

        private static void MergeServiceInto(Service target, Service from)
        {
            // Shallow per-property union (null-coalescing + deep-ish merges for complex alanlar)
            // 1) logging
            if (from.Logging is not null)
                target.Logging ??= new Logging();
            if (from.Logging is not null && target.Logging is not null)
                MergeLogging(target.Logging, from.Logging);

            // 2) deploy.labels (ListOrDict)
            if (from.Deploy?.Labels is not null)
            {
                target.Deploy ??= new Deploy();
                target.Deploy.Labels = MergeListOrDict(target.Deploy.Labels, from.Deploy.Labels);
            }

            // 3) environment (ListOrDict)
            if (from.Environment is not null)
                target.Environment = MergeListOrDict(target.Environment, from.Environment);

            // 4) labels (service level)
            if (from.Labels is not null)
                target.Labels = MergeListOrDict(target.Labels, from.Labels);

            // 5) healthcheck, logging driver vs. gibi basit alanları kopyala (null ise doldur, doluysa bırak)
            target.Healthcheck ??= from.Healthcheck;
            target.User ??= from.User;
            target.WorkingDir ??= from.WorkingDir;
            target.StopSignal ??= from.StopSignal;
            target.StopGracePeriod ??= from.StopGracePeriod;

            // 6) ports/volumes/secrets/configs gibi listeler -> concat (tekrar kontrolü istiyorsan distinct yapabilirsin)
            if (from.Ports is { Count: > 0 })
            {
                target.Ports ??= new();
                target.Ports.AddRange(from.Ports);
            }

            if (from.Volumes is { Count: > 0 })
            {
                target.Volumes ??= new();
                target.Volumes.AddRange(from.Volumes);
            }

            if (from.Secrets is { Count: > 0 })
            {
                target.Secrets ??= new();
                target.Secrets.AddRange(from.Secrets);
            }

            if (from.Configs is { Count: > 0 })
            {
                target.Configs ??= new();
                target.Configs.AddRange(from.Configs);
            }

            // 7) networks (ServiceNetworks) -> smart merge
            if (from.Networks is not null)
                target.Networks = MergeServiceNetworks(target.Networks, from.Networks);

            // 8) dns/dns_search/dns_opt vb.
            target.Dns = MergeListOrString(target.Dns, from.Dns);
            target.DnsSearch = MergeListOrString(target.DnsSearch, from.DnsSearch);
            if (from.DnsOpt is { Count: > 0 })
            {
                target.DnsOpt ??= new();
                target.DnsOpt.AddRange(from.DnsOpt);
            }

            // 9) extra_hosts, ulimits, sysctls
            target.ExtraHosts = MergeExtraHosts(target.ExtraHosts, from.ExtraHosts);
            target.Ulimits = MergeUlimits(target.Ulimits, from.Ulimits);
            target.Sysctls = MergeSysctls(target.Sysctls, from.Sysctls);

            // 10) x-sb alanları
            if (from.X_Sb_Secrets is { Count: > 0 })
            {
                target.X_Sb_Secrets ??= new();
                foreach (var kv in from.X_Sb_Secrets)
                    target.X_Sb_Secrets[kv.Key] = kv.Value;
            }

            if (from.X_Sb_Groups is { Count: > 0 })
            {
                target.X_Sb_Groups ??= new();
                target.X_Sb_Groups.AddRange(from.X_Sb_Groups);
            }
        }

        private static void MergeLogging(Logging target, Logging from)
        {
            // driver overwrite if provided
            target.Driver ??= from.Driver;

            // options map merge (right wins)
            if (from.Options is { Count: > 0 })
            {
                target.Options ??= new();
                foreach (var kv in from.Options)
                    target.Options[kv.Key] = kv.Value;
            }
        }

        private static ListOrDict? MergeListOrDict(ListOrDict? left, ListOrDict? right)
        {
            if (right is null) return left;
            if (left is null) return right;

            // map-map -> right wins by key
            if (left.AsMap is not null && right.AsMap is not null)
            {
                foreach (var kv in right.AsMap)
                    left.AsMap[kv.Key] = kv.Value;
                return left;
            }

            // list-list -> concat
            if (left.AsList is not null && right.AsList is not null)
            {
                left.AsList.AddRange(right.AsList);
                return left;
            }

            // mixed: tercih map (daha deterministik)
            return right;
        }

        private static ServiceNetworks? MergeServiceNetworks(ServiceNetworks? left, ServiceNetworks? right)
        {
            if (right is null) return left;
            if (left is null) return right;

            // map-map → key bazlı merge (right wins)
            if (left.AsMap is not null && right.AsMap is not null)
            {
                foreach (var kv in right.AsMap)
                    left.AsMap[kv.Key] = kv.Value;
                return left;
            }

            // list-list → concat
            if (left.AsList is not null && right.AsList is not null)
            {
                left.AsList.AddRange(right.AsList);
                return left;
            }

            // Karışık durum (list ↔ map) → deterministik olsun diye right'ı tercih et
            // (Compose'ta her iki kısa/uzun sözdizimi destekleniyor; tek tipe indirerek belirsizliği önlüyoruz)
            return right;
        }

// ListOrString için “right wins unless null”
        private static ListOrString? MergeListOrString(ListOrString? left, ListOrString? right)
            => right ?? left;

        private static ExtraHosts? MergeExtraHosts(ExtraHosts? left, ExtraHosts? right)
        {
            if (right is null) return left;
            if (left is null) return right;

            if (right.AsList is { Count: > 0 })
            {
                left.AsList ??= new();
                left.AsList.AddRange(right.AsList);
            }

            if (right.AsMap is { Count: > 0 })
            {
                left.AsMap ??= new();
                foreach (var kv in right.AsMap)
                    left.AsMap[kv.Key] = kv.Value;
            }

            return left;
        }

        private static Ulimits? MergeUlimits(Ulimits? left, Ulimits? right)
        {
            if (right is null) return left;
            if (left is null) return right;

            if (right.Map.Count == 0) return left;

            foreach (var kv in right.Map)
            {
                var key = kv.Key;
                var rEntry = kv.Value;

                // right'ta null yok sayılır (olasılık düşük ama defansif)
                if (rEntry is null)
                    continue;

                if (!left.Map.TryGetValue(key, out var lEntry) || lEntry is null)
                {
                    // left'te yoksa doğrudan ekle
                    left.Map[key] = rEntry;
                    continue;
                }

                // İkisi de var → tip kombinasyonlarına göre davran
                var lObj = lEntry.IsObject;
                var rObj = rEntry.IsObject;

                if (lObj && rObj)
                {
                    // object-object: alan bazlı merge (right dolu alanları yazar)
                    if (rEntry.Soft.HasValue) lEntry.Soft = rEntry.Soft;
                    if (rEntry.Hard.HasValue) lEntry.Hard = rEntry.Hard;
                }
                else
                {
                    // Diğer tüm durumlarda right tüm girdiyi override eder
                    // (single→single, object→single, single→object)
                    left.Map[key] = rEntry;
                }
            }

            return left;
        }

        private static Sysctls? MergeSysctls(Sysctls? left, Sysctls? right)
        {
            if (right is null) return left;
            if (left is null) return right;

            if (right.Map.Count == 0) return left;

            foreach (var kv in right.Map)
            {
                // overwrite or add
                left.Map[kv.Key] = kv.Value;
            }

            return left;
        }
    }
}