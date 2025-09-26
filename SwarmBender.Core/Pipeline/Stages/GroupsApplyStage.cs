using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Applies per-group service fragments to services that declare 'x-sb-groups'.
    /// Looks for:
    ///   stacks/all/{env}/groups/{group}/service.yml(a)
    ///   stacks/{stackId}/{env}/groups/{group}/service.yml(a)
    /// Order per service = order in x-sb-groups; within a group: all/* first, then stack/*; last wins.
    /// </summary>
    public sealed class GroupsApplyStage : IRenderStage
    {
        public int Order => 350;

        private readonly IFileSystem _fs;
        private readonly IYamlEngine _yaml;

        public GroupsApplyStage(IFileSystem fs, IYamlEngine yaml)
        {
            _fs = fs;
            _yaml = yaml;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null. LoadTemplateStage must run before GroupsApplyStage.");
            if (ctx.Working.Services is null || ctx.Working.Services.Count == 0)
                return;

            var stackId = ctx.Request.StackId;
            var env     = ctx.Request.Env;

            foreach (var (svcName, svc) in ctx.Working.Services)
            {
                ct.ThrowIfCancellationRequested();

                var groups = svc.X_Sb_Groups;
                if (groups is null || groups.Count == 0)
                    continue;

                foreach (var g in groups)
                {
                    // files in deterministic order:
                    var files = new[]
                    {
                        $"stacks/all/{env}/groups/{g}/service.yml",
                        $"stacks/all/{env}/groups/{g}/service.yaml",
                        $"stacks/{stackId}/{env}/groups/{g}/service.yml",
                        $"stacks/{stackId}/{env}/groups/{g}/service.yaml",
                    };

                    foreach (var rel in files)
                    {
                        var full = Path.Combine(ctx.RootPath, rel);
                        if (!_fs.FileExists(full)) continue;

                        var frag = await _yaml.LoadYamlAsync<Service>(full, ct).ConfigureAwait(false);
                        if (frag is null) continue;

                        MergeServiceInto(svc, frag);
                        // Bonus: support stray 'replicas' in fragment custom bag (maps to deploy.replicas)
                        if (frag.Custom is { Count: > 0 } && frag.Custom.TryGetValue("replicas", out var repObj))
                        {
                            if (TryToInt(repObj, out var replicas))
                            {
                                svc.Deploy ??= new Deploy();
                                svc.Deploy.Replicas = replicas;
                            }
                        }
                    }
                }
            }
        }

        private static bool TryToInt(object? o, out int v)
        {
            if (o is int i) { v = i; return true; }
            if (o is long l && l is >= int.MinValue and <= int.MaxValue) { v = (int)l; return true; }
            if (o is string s && int.TryParse(s, out var p)) { v = p; return true; }
            v = default; return false;
        }

        // ---- merges (shallow, last-wins) ----

        private static void MergeServiceInto(Service target, Service overlay)
        {
            // simple strings
            if (!string.IsNullOrEmpty(overlay.Image))           target.Image = overlay.Image;
            if (!string.IsNullOrEmpty(overlay.User))            target.User  = overlay.User;
            if (!string.IsNullOrEmpty(overlay.WorkingDir))      target.WorkingDir = overlay.WorkingDir;
            if (!string.IsNullOrEmpty(overlay.StopGracePeriod)) target.StopGracePeriod = overlay.StopGracePeriod;
            if (!string.IsNullOrEmpty(overlay.StopSignal))      target.StopSignal = overlay.StopSignal;

            // list-or-string
            target.Command    = MergeListOrString(target.Command,    overlay.Command);
            target.Entrypoint = MergeListOrString(target.Entrypoint, overlay.Entrypoint);
            target.EnvFile    = MergeListOrString(target.EnvFile,    overlay.EnvFile);
            target.Dns        = MergeListOrString(target.Dns,        overlay.Dns);
            target.DnsSearch  = MergeListOrString(target.DnsSearch,  overlay.DnsSearch);

            // list-or-dict
            target.Environment = MergeListOrDict(target.Environment, overlay.Environment);
            target.Labels      = MergeListOrDict(target.Labels,      overlay.Labels);

            // lists
            target.Devices  = MergeStringList(target.Devices,  overlay.Devices);
            target.Tmpfs    = MergeStringList(target.Tmpfs,    overlay.Tmpfs);
            target.CapAdd   = MergeStringList(target.CapAdd,   overlay.CapAdd);
            target.CapDrop  = MergeStringList(target.CapDrop,  overlay.CapDrop);
            target.Profiles = MergeStringList(target.Profiles, overlay.Profiles);
            target.DnsOpt   = MergeStringList(target.DnsOpt,   overlay.DnsOpt);

            // networks
            target.Networks = MergeServiceNetworks(target.Networks, overlay.Networks);

            // logging
            if (overlay.Logging is not null)
            {
                target.Logging ??= new Logging();
                if (!string.IsNullOrEmpty(overlay.Logging.Driver))
                    target.Logging.Driver = overlay.Logging.Driver;
                if (overlay.Logging.Options is { Count: > 0 })
                {
                    target.Logging.Options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in overlay.Logging.Options)
                        target.Logging.Options[kv.Key] = kv.Value;
                }
            }

            // healthcheck
            if (overlay.Healthcheck is not null)
            {
                target.Healthcheck ??= new Healthcheck();
                var o = overlay.Healthcheck;
                if (o.Test is not null)
                {
                    if (o.Test.AsList is { Count: > 0 })
                        target.Healthcheck.Test = ListOrString.FromList(new List<string>(o.Test.AsList));
                    else if (!string.IsNullOrWhiteSpace(o.Test.AsString))
                        target.Healthcheck.Test = ListOrString.FromString(o.Test.AsString!);
                }
                if (!string.IsNullOrEmpty(o.Interval))       target.Healthcheck.Interval = o.Interval;
                if (!string.IsNullOrEmpty(o.Timeout))        target.Healthcheck.Timeout  = o.Timeout;
                if (o.Retries.HasValue)                      target.Healthcheck.Retries  = o.Retries;
                if (!string.IsNullOrEmpty(o.StartPeriod))    target.Healthcheck.StartPeriod = o.StartPeriod;
            }

            // deploy (labels merge + replicas, etc.)
            if (overlay.Deploy is not null)
            {
                target.Deploy ??= new Deploy();

                // replicas
                if (overlay.Deploy.Replicas.HasValue)
                    target.Deploy.Replicas = overlay.Deploy.Replicas;

                // labels (normalize to map and merge)
                if (overlay.Deploy.Labels is not null)
                    target.Deploy.Labels = MergeListOrDict(target.Deploy.Labels, overlay.Deploy.Labels);
            }
        }

        private static ListOrString? MergeListOrString(ListOrString? left, ListOrString? right)
        {
            if (right is null) return left;
            if (left is null)  return right;

            if (right.AsList is not null)
            {
                left.AsList ??= new();
                foreach (var s in right.AsList) left.AsList.Add(s);
                left.AsString = null;
                return left;
            }
            if (right.AsString is not null)
            {
                left.AsList   = null;
                left.AsString = right.AsString;
                return left;
            }
            return left;
        }

        private static ListOrDict? MergeListOrDict(ListOrDict? left, ListOrDict? right)
        {
            if (right is null) return left;
            if (left  is null) return right;

            // normalize both to dict and last-wins
            var map = ToDict(left);
            foreach (var kv in ToDict(right))
                map[kv.Key] = kv.Value;

            return ListOrDict.FromMap(map);
        }

        private static Dictionary<string, string> ToDict(ListOrDict? lod)
        {
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lod is null) return res;

            if (lod.AsMap is not null)
            {
                foreach (var kv in lod.AsMap)
                    res[kv.Key] = kv.Value ?? string.Empty;
            }
            else if (lod.AsList is not null)
            {
                foreach (var item in lod.AsList)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    var eq = item.IndexOf('=', StringComparison.Ordinal);
                    if (eq < 0) res[item.Trim()] = string.Empty;
                    else
                    {
                        var k = item[..eq].Trim();
                        var v = item[(eq + 1)..];
                        if (!string.IsNullOrEmpty(k)) res[k] = v;
                    }
                }
            }
            return res;
        }

        private static List<string>? MergeStringList(List<string>? left, List<string>? right)
        {
            if (right is null) return left;
            if (left  is null) return new List<string>(right);
            foreach (var s in right) left.Add(s);
            return left;
        }

        private static ServiceNetworks? MergeServiceNetworks(ServiceNetworks? left, ServiceNetworks? right)
        {
            if (right is null) return left;
            if (left  is null) return right;

            // list mode: union-append (dedup by case-insensitive compare)
            if (right.AsList is { Count: > 0 })
            {
                left.AsList ??= new();
                foreach (var n in right.AsList)
                    if (!left.AsList.Contains(n, StringComparer.OrdinalIgnoreCase))
                        left.AsList.Add(n);
            }

            // map mode: right wins per key
            if (right.AsMap is { Count: > 0 })
            {
                left.AsMap ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in right.AsMap)
                    left.AsMap[kv.Key] = kv.Value;
            }

            return left;
        }
    }
}