using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Ensures all environment values are strings while keeping the "map" form.
    /// - If service.Environment is a map: convert every value to string.
    /// - If service.Environment is a list: parse KEY=VALUE and rebuild a map, values as string.
    /// This avoids YAML boolean/number typing issues in Compose/Swarm.
    /// </summary>
    public sealed class EnvStringifyStage : IRenderStage
    {
        // Run after SecretsAttachStage (650). Pick a slightly higher order.
        public int Order => 710;

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working?.Services is null || ctx.Working.Services.Count == 0)
                return Task.CompletedTask;

            foreach (var (svcName, svc) in ctx.Working.Services)
            {
                ct.ThrowIfCancellationRequested();
                if (svc.Environment is null) continue;

                svc.Environment = NormalizeToStringMap(svc.Environment);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns a ListOrDict in "map" form whose values are guaranteed to be strings.
        /// </summary>
        private static ListOrDict NormalizeToStringMap(ListOrDict env)
        {
            // Case 1: Already a map -> stringify all values
            if (env.AsMap is not null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in env.AsMap)
                    map[kv.Key] = ToStringValue(kv.Value);
                return ListOrDict.FromMap(map);
            }

            // Case 2: List (KEY=VALUE) -> parse and rebuild as map (string values)
            if (env.AsList is not null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in env.AsList)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;

                    var idx = item.IndexOf('=', StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        // KEY with empty value
                        var keyOnly = item.Trim();
                        if (!string.IsNullOrEmpty(keyOnly))
                            map[keyOnly] = string.Empty;
                    }
                    else
                    {
                        var key = item[..idx].Trim();
                        var val = item[(idx + 1)..]; // keep as-is (string)
                        if (!string.IsNullOrEmpty(key))
                            map[key] = val;
                    }
                }
                return ListOrDict.FromMap(map);
            }

            // Nothing set -> keep as-is
            return env;
        }

        /// <summary>
        /// Normalize any object-like value to a string suitable for YAML/Compose.
        /// NOTE: We deliberately return string so serializer does not emit boolean/number nodes.
        /// </summary>
        private static string ToStringValue(string? v)
        {
            // Already string
            if (v is null) return string.Empty;

            // Trim is safe; avoids accidental spaces becoming part of the scalar
            var s = v.Trim();

            // Optional: canonicalize boolean-ish spellings to lowercase
            // (purely cosmetic; Compose sees a string either way)
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return "true";
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return "false";

            return s;
        }
    }
}