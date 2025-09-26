using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;
using SwarmBender.Core.Data.Models;
using SecretModel = SwarmBender.Core.Data.Compose.Secret;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Expands token placeholders on the typed compose model.
    /// Supported patterns: ${NAME} and {{NAME}}.
    /// Sources: SB_* defaults + config.tokens.user (user overrides).
    /// Service-specific token SB_SERVICE_NAME is injected per service.
    /// </summary>
    public sealed class TokenExpandStage : IRenderStage
    {
        public int Order => 700;

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null. LoadTemplateStage must run before TokenExpandStage.");

            var baseTokens = BuildTokens(ctx);
            var svcs = ctx.Working.Services;
            if (svcs is null || svcs.Count == 0)
                return Task.CompletedTask;

            foreach (var (svcName, svc) in svcs)
            {
                ct.ThrowIfCancellationRequested();

                // per-service implicit token
                var tokens = new Dictionary<string, string>(baseTokens, StringComparer.OrdinalIgnoreCase)
                {
                    ["SB_SERVICE_NAME"] = svcName
                };

                // simple strings
                svc.Image           = ReplaceTokens(svc.Image, tokens);
                svc.User            = ReplaceTokens(svc.User, tokens);
                svc.WorkingDir      = ReplaceTokens(svc.WorkingDir, tokens);
                svc.StopGracePeriod = ReplaceTokens(svc.StopGracePeriod, tokens);
                svc.StopSignal      = ReplaceTokens(svc.StopSignal, tokens);

                // list-or-string fields
                ExpandListOrString(svc.Command, tokens);
                ExpandListOrString(svc.Entrypoint, tokens);
                ExpandListOrString(svc.EnvFile, tokens);
                ExpandListOrString(svc.Dns, tokens);
                ExpandListOrString(svc.DnsSearch, tokens);

                // list<string> fields
                ExpandStringList(svc.Devices, tokens);
                ExpandStringList(svc.Tmpfs, tokens);
                ExpandStringList(svc.CapAdd, tokens);
                ExpandStringList(svc.CapDrop, tokens);
                ExpandStringList(svc.Profiles, tokens);
                ExpandStringList(svc.DnsOpt, tokens);

                // list-or-dict fields
                ExpandListOrDict(svc.Environment, tokens);
                ExpandListOrDict(svc.Labels, tokens);

                // deploy.labels (dictionary inside ListOrDict)
                if (svc.Deploy?.Labels is not null)
                    ExpandListOrDict(svc.Deploy.Labels, tokens);

                // logging
                if (svc.Logging is not null)
                {
                    svc.Logging.Driver = ReplaceTokens(svc.Logging.Driver, tokens);
                    if (svc.Logging.Options is not null)
                    {
                        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (k, v) in svc.Logging.Options)
                            opts[ReplaceTokens(k, tokens) ?? k] = ReplaceTokens(v, tokens) ?? v;
                        svc.Logging.Options = opts;
                    }
                }

                // healthcheck
                if (svc.Healthcheck is not null)
                {
                    ExpandListOrString(svc.Healthcheck.Test, tokens);
                    svc.Healthcheck.Interval    = ReplaceTokens(svc.Healthcheck.Interval, tokens);
                    svc.Healthcheck.Timeout     = ReplaceTokens(svc.Healthcheck.Timeout, tokens);
                    svc.Healthcheck.StartPeriod = ReplaceTokens(svc.Healthcheck.StartPeriod, tokens);
                    // Retries is int?; token expansion not applicable.
                }

                // volumes (mounts) – textual fields only
                if (svc.Volumes is not null)
                {
                    foreach (var m in svc.Volumes)
                    {
                        m.Source = ReplaceTokens(m.Source, tokens);
                        m.Target = ReplaceTokens(m.Target, tokens);
                        // If you add more string fields later, expand here.
                    }
                }

                // ports – textual fields only
                if (svc.Ports is not null)
                {
                    foreach (var p in svc.Ports)
                    {
                        p.Protocol = ReplaceTokens(p.Protocol, tokens);
                        p.Mode     = ReplaceTokens(p.Mode, tokens);
                    }
                }

                // secret refs
                if (svc.Secrets is not null)
                {
                    foreach (var r in svc.Secrets)
                    {
                        r.Source = ReplaceTokens(r.Source, tokens);
                        r.Target = ReplaceTokens(r.Target, tokens);
                    }
                }

                // config refs
                if (svc.Configs is not null)
                {
                    foreach (var r in svc.Configs)
                    {
                        r.Source = ReplaceTokens(r.Source, tokens);
                        r.Target = ReplaceTokens(r.Target, tokens);
                    }
                }

                // extra_hosts (map)
                if (svc.ExtraHosts is not null && svc.ExtraHosts.AsMap is not null)
                {
                    var newMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (host, ip) in svc.ExtraHosts.AsMap)
                    {
                        var newKey = ReplaceTokens(host, tokens) ?? host;
                        newMap[newKey] = ReplaceTokens(ip, tokens) ?? ip;
                    }
                    svc.ExtraHosts.AsMap = newMap;
                }

                // networks (long syntax attachments)
                if (svc.Networks?.AsMap is not null)
                {
                    foreach (var (_, att) in svc.Networks.AsMap)
                    {
                        if (att is null) continue;
                        if (att.Aliases is not null)
                            ExpandStringList(att.Aliases, tokens);

                        // If ServiceNetworkAttachment has other string fields (e.g., ipv4_address),
                        // expand them here similarly:
                        att.Ipv4Address = ReplaceTokens(att.Ipv4Address, tokens);
                        att.Ipv6Address = ReplaceTokens(att.Ipv6Address, tokens);
                    }
                }

                // x-sb / custom blocks (in place)
                ExpandCustomBlockInPlace(svc, tokens);
            }

            // root-level secrets: rename keys + expand string fields
            if (ctx.Working.Secrets is { Count: > 0 })
            {
                var newMap = new Dictionary<string, SecretModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, sec) in ctx.Working.Secrets)
                {
                    var newName = ReplaceTokens(name, baseTokens) ?? name;
                    ExpandCustomBlockInPlace(sec, baseTokens);
                    sec.Name            = ReplaceTokens(sec.Name, baseTokens);
                    sec.File            = ReplaceTokens(sec.File, baseTokens);
                    if (sec.External is not null)
                        sec.External.Name = ReplaceTokens(sec.External.Name, baseTokens);
                    newMap[newName] = sec;
                }
                ctx.Working.Secrets = newMap;
            }

            // root-level custom
            ExpandCustomBlockInPlace(ctx.Working, baseTokens);

            return Task.CompletedTask;
        }

        // ------------ helpers ------------
        private static Dictionary<string, string> BuildTokens(RenderContext ctx)
        {
            var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SB_STACK_ID"] = ctx.Request.StackId,
                ["SB_ENV"]      = ctx.Request.Env,
            };
            if (ctx.Config?.Tokens?.User is { Count: > 0 })
                foreach (var kv in ctx.Config.Tokens.User)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        bag[kv.Key] = kv.Value ?? string.Empty;
            return bag;
        }

        private static void ExpandListOrString(ListOrString? los, IDictionary<string,string> tokens)
        {
            if (los is null) return;
            if (los.AsList is not null)
            {
                for (int i = 0; i < los.AsList.Count; i++)
                    los.AsList[i] = ReplaceTokens(los.AsList[i], tokens);
            }
            else if (los.AsString is not null)
            {
                los.AsString = ReplaceTokens(los.AsString, tokens);
            }
        }

        private static void ExpandListOrDict(ListOrDict? lod, IDictionary<string,string> tokens)
        {
            if (lod is null) return;

            if (lod.AsMap is not null)
            {
                var map = lod.AsMap;
                var keys = new List<string>(map.Keys);
                foreach (var k in keys)
                    map[k] = ReplaceTokens(map[k], tokens);
                return;
            }

            if (lod.AsList is not null)
            {
                var list = lod.AsList;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (string.IsNullOrEmpty(item)) continue;

                    var eq = item.IndexOf('=', StringComparison.Ordinal);
                    if (eq < 0)
                    {
                        // bare item: tümü expand
                        list[i] = ReplaceTokens(item, tokens);
                    }
                    else
                    {
                        // "KEY=VALUE": hem key hem value expand
                        var key = item[..eq];
                        var val = item[(eq + 1)..];
                        var newKey = ReplaceTokens(key, tokens) ?? key;
                        var newVal = ReplaceTokens(val, tokens) ?? val;
                        list[i] = newKey + "=" + newVal;
                    }
                }
            }
        }

        private static void ExpandStringList(List<string>? list, IDictionary<string,string> tokens)
        {
            if (list is null) return;
            for (int i = 0; i < list.Count; i++)
                list[i] = ReplaceTokens(list[i], tokens);
        }

        // mutate ComposeNode.Custom in place (property is read-only)
        private static void ExpandCustomBlockInPlace(ComposeNode node, IDictionary<string,string> tokens)
        {
            if (node.Custom is null || node.Custom.Count == 0) return;
            var expanded = ExpandObject(node.Custom, tokens);
            node.Custom.Clear();
            foreach (var kv in expanded)
                node.Custom[kv.Key] = kv.Value;
        }

        private static IDictionary<string, object?> ExpandObject(IDictionary<string, object?> obj, IDictionary<string,string> tokens)
        {
            var res = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in obj)
                res[ReplaceTokens(k, tokens) ?? k] = ExpandValue(v, tokens);
            return res;
        }

        private static object? ExpandValue(object? v, IDictionary<string,string> tokens)
            => v switch
            {
                null => null,
                string s => ReplaceTokens(s, tokens),
                IDictionary<string, object?> map => ExpandObject(map, tokens),
                IEnumerable list => list.Cast<object?>().Select(x => ExpandValue(x, tokens)).ToList(),
                _ => v
            };

        // Supports ${NAME} and {{NAME}}
        private static readonly Regex RxDollar = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
        private static readonly Regex RxBraces = new(@"\{\{([A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);

        private static string? ReplaceTokens(string? input, IDictionary<string,string> tokens)
        {
            if (string.IsNullOrEmpty(input)) return input;

            static string Expand(Regex rx, string s, IDictionary<string,string> tks)
                => rx.Replace(s, m => tks.TryGetValue(m.Groups[1].Value, out var val) ? (val ?? string.Empty) : m.Value);

            return Expand(RxBraces, Expand(RxDollar, input!, tokens), tokens);
        }
    }
}