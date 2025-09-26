using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Applies the aggregated environment bag (ctx.Env) to each service's environment (ListOrDict).
    /// Merge rule is last-wins: ctx.Env overrides existing entries with the same key.
    /// </summary>
    public sealed class EnvironmentApplyStage : IRenderStage
    {
        public int Order => 500;

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException(
                    "Working model is null. LoadTemplateStage must run before EnvironmentApplyStage.");

            if (ctx.Env is null || ctx.Env.Count == 0)
                return Task.CompletedTask; // nothing to apply

            var services = ctx.Working.Services;
            if (services is null || services.Count == 0)
                return Task.CompletedTask;

            foreach (var (svcName, svc) in services)
            {
                ct.ThrowIfCancellationRequested();

                // Convert existing env → dictionary
                var baseDict = ToDictionary(svc.Environment);

                // Merge global ctx.Env
                foreach (var kv in ctx.Env)
                    baseDict[kv.Key] = kv.Value;

                // Write back as map
                svc.Environment = ListOrDict.FromMap(
                    new Dictionary<string, string>(baseDict, StringComparer.OrdinalIgnoreCase));
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
                    if (idx < 0)
                    {
                        dict[item.Trim()] = string.Empty;
                    }
                    else
                    {
                        var key = item[..idx].Trim();
                        var val = item[(idx + 1)..];
                        if (!string.IsNullOrEmpty(key))
                            dict[key] = val;
                    }
                }
            }

            return dict;
        }
    }
}