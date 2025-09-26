using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Expands token placeholders on the typed compose model according to the latest Service schema.
    /// Supported patterns: ${NAME} and {{NAME}}.
    /// Sources: SB_* defaults + config.tokens.user (user overrides).
    /// Fields covered:
    /// - service: image, command, entrypoint, environment, labels, env_file, dns, dns_search,
    ///            user, working_dir, stop_grace_period, stop_signal,
    ///            devices, tmpfs, cap_add, cap_drop, profiles, dns_opt
    /// - deploy.labels (if present)
    /// </summary>
    public sealed class TokenExpandStage : IRenderStage
    {
        public int Order => 700;

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null. LoadTemplateStage must run before TokenExpandStage.");

            var tokens = BuildTokens(ctx);

            var svcs = ctx.Working.Services;
            if (svcs is null || svcs.Count == 0)
                return Task.CompletedTask;

            foreach (var (svcName, svc) in svcs)
            {
                ct.ThrowIfCancellationRequested();

                // string fields
                svc.Image          = ReplaceTokens(svc.Image, tokens);
                svc.User           = ReplaceTokens(svc.User, tokens);
                svc.WorkingDir     = ReplaceTokens(svc.WorkingDir, tokens);
                svc.StopGracePeriod= ReplaceTokens(svc.StopGracePeriod, tokens);
                svc.StopSignal     = ReplaceTokens(svc.StopSignal, tokens);

                // ListOrString fields
                ExpandListOrString(svc.Command, tokens);
                ExpandListOrString(svc.Entrypoint, tokens);
                ExpandListOrString(svc.EnvFile, tokens);
                ExpandListOrString(svc.Dns, tokens);
                ExpandListOrString(svc.DnsSearch, tokens);

                // List<string> fields
                ExpandStringList(svc.Devices, tokens);
                ExpandStringList(svc.Tmpfs, tokens);
                ExpandStringList(svc.CapAdd, tokens);
                ExpandStringList(svc.CapDrop, tokens);
                ExpandStringList(svc.Profiles, tokens);
                ExpandStringList(svc.DnsOpt, tokens);

                // ListOrDict fields
                ExpandListOrDict(svc.Environment, tokens);
                ExpandListOrDict(svc.Labels, tokens);

                // deploy.labels (dictionary)
                if (svc.Deploy?.Labels is not null)
                    ExpandListOrDict(svc.Deploy.Labels, tokens);

                // NOTE: We can extend to Healthcheck, Logging.Options, ExtraHosts, Sysctls, Ulimits later if needed.
            }

            return Task.CompletedTask;
        }

        // ---------------- helpers ----------------

        private static Dictionary<string, string> BuildTokens(RenderContext ctx)
        {
            var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SB_STACK_ID"] = ctx.Request.StackId,
                ["SB_ENV"]      = ctx.Request.Env,
            };

            var user = ctx.Config?.Tokens?.User;
            if (user is { Count: > 0 })
            {
                foreach (var kv in user)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        bag[kv.Key] = kv.Value ?? string.Empty;
            }
            return bag;
        }

        private static void ExpandListOrString(ListOrString? los, IDictionary<string,string> tokens)
        {
            if (los is null) return;

            if (los.AsList is not null)
            {
                for (int i = 0; i < los.AsList.Count; i++)
                    los.AsList[i] = ReplaceTokens(los.AsList[i], tokens);
                return;
            }

            if (los.AsString is not null)
                los.AsString = ReplaceTokens(los.AsString, tokens);
        }

        private static void ExpandListOrDict(ListOrDict? lod, IDictionary<string,string> tokens)
        {
            if (lod is null) return;

            if (lod.AsMap is not null)
            {
                var map = lod.AsMap;
                var keys = new List<string>(map.Keys);
                foreach (var k in keys)
                {
                    map[k] = ReplaceTokens(map[k], tokens);
                }
                return;
            }

            if (lod.AsList is not null)
            {
                // items like "KEY=VALUE" or bare keys
                var list = lod.AsList;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (string.IsNullOrEmpty(item)) continue;

                    var eq = item.IndexOf('=', StringComparison.Ordinal);
                    if (eq < 0)
                    {
                        list[i] = ReplaceTokens(item, tokens); // bare item can still have tokens
                    }
                    else
                    {
                        var key = item[..eq];
                        var val = item[(eq + 1)..];
                        list[i] = key + "=" + ReplaceTokens(val, tokens);
                    }
                }
            }
        }

        private static void ExpandStringDictionary(Dictionary<string,string>? map, IDictionary<string,string> tokens)
        {
            if (map is null) return;
            var keys = new List<string>(map.Keys);
            foreach (var k in keys)
                map[k] = ReplaceTokens(map[k], tokens);
        }

        private static void ExpandStringList(List<string>? list, IDictionary<string,string> tokens)
        {
            if (list is null) return;
            for (int i = 0; i < list.Count; i++)
                list[i] = ReplaceTokens(list[i], tokens);
        }

        // Supports ${NAME} and {{NAME}}
        private static readonly Regex RxDollar = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
        private static readonly Regex RxBraces = new(@"\{\{([A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);

        private static string? ReplaceTokens(string? input, IDictionary<string,string> tokens)
        {
            if (string.IsNullOrEmpty(input)) return input;

            static string Expand(Regex rx, string s, IDictionary<string,string> tks)
            {
                return rx.Replace(s, m =>
                {
                    var key = m.Groups[1].Value;
                    return tks.TryGetValue(key, out var val) ? (val ?? string.Empty) : m.Value;
                });
            }

            var s1 = Expand(RxDollar, input!, tokens);
            var s2 = Expand(RxBraces, s1, tokens);
            return s2;
        }
    }
}