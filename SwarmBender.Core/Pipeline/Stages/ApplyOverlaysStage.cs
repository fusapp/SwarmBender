using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Applies overlays in a configurable order:
    ///  - Expands wildcard ("*") service overlay to all services (deep-merge).
    ///  - Deep-merges named services in overlay into working model (last wins).
    ///  - Shallow-merges all top-level nodes EXCEPT Services (Services are handled above).
    /// Files are applied in the order they are discovered for each pattern; last applied wins.
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
            var env     = ctx.Request.Env;

            // 1) Get overlay order from config; fallback to defaults
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
                    .Replace("{env}",     env,     StringComparison.OrdinalIgnoreCase);

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

                // 3a) Spread wildcard ("*") service overlay onto each concrete service (deep-merge)
                ApplyWildcardServicesOverlay(ctx.Working!, overlay);

                // 3b) Deep-merge named services (skip "*")
                MergeNamedServices(ctx.Working!, overlay);

                // 3c) Shallow top-level merge for everything EXCEPT Services
                ShallowTopLevelMergeExceptServices(ctx.Working!, overlay);

                // If you keep an "applied files" list on context, append here.
                // ctx.AppliedOverlays?.Add(file);
            }
        }

        // --- helpers ---------------------------------------------------------

        /// <summary>
        /// Supports "*.y?(a)ml" by expanding to both *.yml and *.yaml masks (relative to repo root).
        /// </summary>
        private static IEnumerable<string> ExpandMasks(string mask)
        {
            if (mask.Contains("y?(a)ml", StringComparison.OrdinalIgnoreCase))
            {
                yield return mask.Replace("y?(a)ml", "yml", StringComparison.OrdinalIgnoreCase);
                yield return mask.Replace("y?(a)ml", "yaml", StringComparison.OrdinalIgnoreCase);
                yield break;
            }

            if (mask.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                mask.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                yield return mask;
                yield break;
            }

            yield return mask + ".yml";
            yield return mask + ".yaml";
        }

        /// <summary>
        /// Deep-merge named services from overlay into target (skips "*").
        /// If a service does not exist on the target, adds it; otherwise merges field-wise.
        /// </summary>
        private static void MergeNamedServices(ComposeFile target, ComposeFile overlay)
        {
            if (overlay?.Services is null || overlay.Services.Count == 0) return;

            target.Services ??= new(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, osvc) in overlay.Services)
            {
                if (name == "*" || osvc is null) continue;

                if (!target.Services.TryGetValue(name, out var tsvc) || tsvc is null)
                {
                    target.Services[name] = osvc;
                }
                else
                {
                    MergeServiceInto(tsvc, osvc);
                }
            }
        }

        /// <summary>
        /// Shallow top-level merge for typed ComposeFile, but SKIPS Services (handled elsewhere).
        /// - IDictionary&lt;string, ?&gt;: add/overwrite keys from overlay
        /// - IList: append overlay items
        /// - Scalar/complex refs: overwrite if overlay value != null
        /// </summary>
        private static void ShallowTopLevelMergeExceptServices(ComposeFile target, ComposeFile overlay)
        {
            if (overlay is null) return;

            var tType = typeof(ComposeFile);
            foreach (var prop in tType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                if (string.Equals(prop.Name, nameof(ComposeFile.Services), StringComparison.Ordinal))
                    continue; // Services are deep-merged separately

                var oVal = prop.GetValue(overlay);
                if (oVal is null) continue;

                var tVal = prop.GetValue(target);

                // IDictionary<string, ?>
                if (IsStringKeyDictionary(prop.PropertyType, out _))
                {
                    if (tVal is null)
                    {
                        prop.SetValue(target, oVal);
                        continue;
                    }

                    var tDict = (IDictionary)tVal;
                    var oDict = (IDictionary)oVal;
                    foreach (DictionaryEntry entry in oDict)
                        tDict[entry.Key] = entry.Value; // last wins
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

                // scalar/complex → overwrite
                prop.SetValue(target, oVal);
            }
        }

        private static bool IsStringKeyDictionary(Type type, out Type[] genArgs)
        {
            genArgs = Type.EmptyTypes;

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

        /// <summary>
        /// Applies the special "*" service overlay to each concrete service in the working model.
        /// Then removes "*" from overlay so it won't leak into the final Services dictionary.
        /// </summary>
        private static void ApplyWildcardServicesOverlay(ComposeFile working, ComposeFile overlay)
        {
            if (overlay?.Services is null || working?.Services is null) return;

            if (!overlay.Services.TryGetValue("*", out var wildcard) || wildcard is null)
                return;

            foreach (var (svcName, svc) in working.Services)
            {
                if (svc is null) continue;
                MergeServiceInto(svc, wildcard);
            }

            overlay.Services.Remove("*");
        }

        /// <summary>
        /// Deep-ish merge for a single service: field-wise, preserving non-null target values
        /// unless overlay explicitly provides a replacement.
        /// </summary>
        private static void MergeServiceInto(Service target, Service from)
        {
            if (from is null) return;

            // --- simple strings ---
            target.Image           = from.Image           ?? target.Image;
            target.User            = from.User            ?? target.User;
            target.WorkingDir      = from.WorkingDir      ?? target.WorkingDir;
            target.StopSignal      = from.StopSignal      ?? target.StopSignal;
            target.StopGracePeriod = from.StopGracePeriod ?? target.StopGracePeriod;

            // --- ListOrString (replace if provided) ---
            target.Command    = from.Command    ?? target.Command;
            target.Entrypoint = from.Entrypoint ?? target.Entrypoint;
            target.EnvFile    = from.EnvFile    ?? target.EnvFile;
            target.Dns        = from.Dns        ?? target.Dns;
            target.DnsSearch  = from.DnsSearch  ?? target.DnsSearch;

            // --- List<string> (replace if non-empty) ---
            if (from.Devices  is { Count: > 0 }) target.Devices  = new(from.Devices);
            if (from.Tmpfs    is { Count: > 0 }) target.Tmpfs    = new(from.Tmpfs);
            if (from.CapAdd   is { Count: > 0 }) target.CapAdd   = new(from.CapAdd);
            if (from.CapDrop  is { Count: > 0 }) target.CapDrop  = new(from.CapDrop);
            if (from.Profiles is { Count: > 0 }) target.Profiles = new(from.Profiles);
            if (from.DnsOpt   is { Count: > 0 }) target.DnsOpt   = new(from.DnsOpt);

            // --- ListOrDict (map-merge; list => concat/replace policy below) ---
            target.Environment = MergeListOrDict(target.Environment, from.Environment);
            target.Labels      = MergeListOrDict(target.Labels,      from.Labels);

            // --- logging (field-wise) ---
            if (from.Logging is not null)
            {
                target.Logging ??= new Logging();
                target.Logging.Driver = from.Logging.Driver ?? target.Logging.Driver;

                if (from.Logging.Options is { Count: > 0 })
                {
                    target.Logging.Options ??= new(StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in from.Logging.Options)
                        target.Logging.Options[k] = v;
                }
            }

            // --- healthcheck (field-wise) ---
            if (from.Healthcheck is not null)
            {
                target.Healthcheck ??= new Healthcheck();
                target.Healthcheck.Test        = from.Healthcheck.Test        ?? target.Healthcheck.Test;
                target.Healthcheck.Interval    = from.Healthcheck.Interval    ?? target.Healthcheck.Interval;
                target.Healthcheck.Timeout     = from.Healthcheck.Timeout     ?? target.Healthcheck.Timeout;
                target.Healthcheck.StartPeriod = from.Healthcheck.StartPeriod ?? target.Healthcheck.StartPeriod;
                target.Healthcheck.Retries     = from.Healthcheck.Retries     ?? target.Healthcheck.Retries;
            }

            // --- deploy (deep) ---
            if (from.Deploy is not null)
            {
                target.Deploy ??= new Deploy();

                // replicas
                if (from.Deploy.Replicas.HasValue)
                    target.Deploy.Replicas = from.Deploy.Replicas;

                // labels (ListOrDict)
                target.Deploy.Labels = MergeListOrDict(target.Deploy.Labels, from.Deploy.Labels);

                // update_config
                if (from.Deploy.UpdateConfig is not null)
                {
                    target.Deploy.UpdateConfig ??= new UpdateConfig();
                    var s = target.Deploy.UpdateConfig;
                    var o = from.Deploy.UpdateConfig;
                    if (o.Parallelism.HasValue) s.Parallelism = o.Parallelism;
                    s.Delay = o.Delay ?? s.Delay;
                    s.Order = o.Order ?? s.Order;
                }

                // restart_policy
                if (from.Deploy.RestartPolicy is not null)
                {
                    target.Deploy.RestartPolicy ??= new RestartPolicy();
                    var s = target.Deploy.RestartPolicy;
                    var o = from.Deploy.RestartPolicy;
                    s.Condition   = o.Condition   ?? s.Condition;
                    s.Delay       = o.Delay       ?? s.Delay;
                    if (o.MaxAttempts.HasValue) s.MaxAttempts = o.MaxAttempts;
                    s.Window      = o.Window      ?? s.Window;
                }
            }

            // --- volumes/ports/secrets/configs (replace if non-empty) ---
            if (from.Volumes is { Count: > 0 }) target.Volumes = new(from.Volumes);
            if (from.Ports   is { Count: > 0 }) target.Ports   = new(from.Ports);
            if (from.Secrets is { Count: > 0 }) target.Secrets = new(from.Secrets);
            if (from.Configs is { Count: > 0 }) target.Configs = new(from.Configs);

            // --- networks (short/long) ---
            target.Networks = from.Networks ?? target.Networks;

            // --- extra_hosts / ulimits / sysctls ---
            target.ExtraHosts = MergeExtraHosts(target.ExtraHosts, from.ExtraHosts);
            target.Ulimits    = MergeUlimits(target.Ulimits,       from.Ulimits);
            target.Sysctls    = MergeSysctls(target.Sysctls,       from.Sysctls);

            // --- x-sb meta (kept in model, filtered at serialize if needed) ---
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

        private static ListOrDict? MergeListOrDict(ListOrDict? left, ListOrDict? right)
        {
            if (right is null) return left;
            if (left  is null) return right;

            // map-map: key-wise merge (right wins)
            if (left.AsMap is not null && right.AsMap is not null)
            {
                foreach (var kv in right.AsMap)
                    left.AsMap[kv.Key] = kv.Value;
                return left;
            }

            // list-list: append
            if (left.AsList is not null && right.AsList is not null)
            {
                left.AsList.AddRange(right.AsList);
                return left;
            }

            // mixed: prefer right (deterministic)
            return right;
        }

        private static ServiceNetworks? MergeServiceNetworks(ServiceNetworks? left, ServiceNetworks? right)
        {
            if (right is null) return left;
            if (left  is null) return right;

            if (left.AsMap is not null && right.AsMap is not null)
            {
                foreach (var kv in right.AsMap)
                    left.AsMap[kv.Key] = kv.Value;
                return left;
            }

            if (left.AsList is not null && right.AsList is not null)
            {
                left.AsList.AddRange(right.AsList);
                return left;
            }

            return right;
        }

        private static ListOrString? MergeListOrString(ListOrString? left, ListOrString? right)
            => right ?? left;

        private static ExtraHosts? MergeExtraHosts(ExtraHosts? left, ExtraHosts? right)
        {
            if (right is null) return left;
            if (left  is null) return right;

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
            if (left  is null) return right;

            if (right.Map.Count == 0) return left;

            foreach (var kv in right.Map)
            {
                var key    = kv.Key;
                var rEntry = kv.Value;
                if (rEntry is null) continue;

                if (!left.Map.TryGetValue(key, out var lEntry) || lEntry is null)
                {
                    left.Map[key] = rEntry;
                    continue;
                }

                var lObj = lEntry.IsObject;
                var rObj = rEntry.IsObject;

                if (lObj && rObj)
                {
                    if (rEntry.Soft.HasValue) lEntry.Soft = rEntry.Soft;
                    if (rEntry.Hard.HasValue) lEntry.Hard = rEntry.Hard;
                }
                else
                {
                    left.Map[key] = rEntry; // override for other combinations
                }
            }

            return left;
        }

        private static Sysctls? MergeSysctls(Sysctls? left, Sysctls? right)
        {
            if (right is null) return left;
            if (left  is null) return right;

            if (right.Map.Count == 0) return left;

            foreach (var kv in right.Map)
                left.Map[kv.Key] = kv.Value;

            return left;
        }
    }
}