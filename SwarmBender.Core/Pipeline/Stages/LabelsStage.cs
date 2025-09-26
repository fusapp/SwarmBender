using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Normalizes and enriches service deploy.labels:
    /// - Ensures labels exist in map form (KEY -> VALUE).
    /// - Merges root-level x-sb.labels (global) into every service (first).
    /// - Merges service-level x-sb.labels (overrides global on conflicts).
    /// Merge policy: last-wins.
    /// </summary>
    public sealed class LabelsStage : IRenderStage
    {
        public int Order => 600;

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException(
                    "Working model is null. LoadTemplateStage must run before LabelsStage.");

            var services = ctx.Working.Services;
            if (services is null || services.Count == 0)
                return Task.CompletedTask;

            // Read global x-sb.labels from compose root (if any)
            var globalLabels = ReadXsBLabels(ctx.Working);

            foreach (var (name, svc) in services)
            {
                ct.ThrowIfCancellationRequested();

                svc.Deploy ??= new Deploy();
                // Normalize current labels to a dict
                var current = ToDictionary(svc.Deploy.Labels);

                // 1) apply global labels first
                foreach (var kv in globalLabels)
                    current[kv.Key] = kv.Value;

                // 2) apply service-level x-sb.labels (overrides global)
                var svcLabels = ReadXsBLabels(svc);
                foreach (var kv in svcLabels)
                    current[kv.Key] = kv.Value;

                // write back as map
                svc.Deploy.Labels = ListOrDict.FromMap(
                    new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Extracts x-sb.labels map from a ComposeNode.Custom if present.
        /// Expected structure:
        ///   node.Custom["x-sb"] as Dictionary<string,object?> containing
        ///   "labels" as Dictionary&lt;string,object?&gt; (values stringified).
        /// </summary>
        private static Dictionary<string, string> ReadXsBLabels(ComposeNode node)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (node.Custom is null || node.Custom.Count == 0)
                return result;

            if (!node.Custom.TryGetValue("x-sb", out var xsbObj) || xsbObj is null)
                return result;

            if (xsbObj is not IDictionary<string, object?> xsb)
                return result;

            if (!xsb.TryGetValue("labels", out var labelsObj) || labelsObj is null)
                return result;

            if (labelsObj is IDictionary<string, object?> map)
            {
                foreach (var (k, v) in map)
                {
                    var key = k?.ToString();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    result[key] = v?.ToString() ?? string.Empty;
                }
            }

            return result;
        }

        /// <summary>
        /// Converts ListOrDict labels to a dictionary. List items may be "KEY=VALUE" or "KEY".
        /// </summary>
        private static Dictionary<string, string> ToDictionary(ListOrDict? labels)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (labels is null) return dict;

            if (labels.AsMap is not null)
            {
                foreach (var kv in labels.AsMap)
                    dict[kv.Key] = kv.Value ?? string.Empty;
                return dict;
            }

            if (labels.AsList is not null)
            {
                foreach (var item in labels.AsList)
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