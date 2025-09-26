using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Collects environment variables from JSON files under:
    ///   - stacks/all/{env}/env/*.json
    ///   - stacks/{stackId}/{env}/env/*.json
    /// plus any extra directories listed in SbConfig.Providers.File.ExtraJsonDirs.
    /// Files are flattened to key/value pairs and merged with last-wins semantics.
    /// </summary>
    public sealed class EnvJsonCollectStage : IRenderStage
    {
        public int Order => 300;

        private readonly IFileSystem _fs;

        public EnvJsonCollectStage(IFileSystem fs) => _fs = fs;

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            // Build the ordered list of directories (relative to repo root) to probe
            var dirs = new List<string>
            {
                $"stacks/all/{ctx.Request.Env}/env",
                $"stacks/{ctx.Request.StackId}/{ctx.Request.Env}/env"
            };

            // Append any extra directories from config (if provided)
            var extra = ctx.Config?.Providers?.File?.ExtraJsonDirs;
            if (extra is { Count: > 0 })
            {
                foreach (var d in extra)
                {
                    dirs.Add(ReplaceTokens(d, ctx.Request.StackId, ctx.Request.Env));
                }
            }

            // Resolve files (alphabetical per directory), then process in listed order
            var filesOrdered = new List<string>();
            foreach (var dir in dirs)
            {
                // We expect patterns like "stacks/.../env" (a folder). Glob "*.json" inside.
                var pattern = MakeFolderPattern(dir, "*.json");
                var hits = _fs.GlobFiles(ctx.RootPath, pattern)
                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                filesOrdered.AddRange(hits);
            }

            foreach (var file in filesOrdered)
            {
                ct.ThrowIfCancellationRequested();

                var json = await _fs.ReadAllTextAsync(Path.Combine(ctx.RootPath, file), ct).ConfigureAwait(false);
                var doc = JsonDocument.Parse(json);

                // Flatten to keys using both dot (.) and double-underscore (__) forms.
                var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                FlattenJson(doc.RootElement, prefix: null, flat);

                // Merge into ctx.Env (last file wins)
                foreach (var kv in flat)
                    ctx.Env[kv.Key] = kv.Value;
            }
        }

        private static string ReplaceTokens(string input, string stackId, string env)
            => input.Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{env}",     env,     StringComparison.OrdinalIgnoreCase);

        private static string MakeFolderPattern(string folder, string mask)
        {
            // Ensure trailing separator stripped; glob is relative to repo root.
            var f = folder.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(f) ? mask : $"{f}/{mask}".Replace('\\','/'); // normalize
        }

        /// <summary>
        /// Flattens a JSON element into key/value pairs. For each logical path "A.B.C",
        /// we emit both "A.B.C" and "A__B__C" to maximize compatibility with consumers.
        /// </summary>
        private static void FlattenJson(JsonElement el, string? prefix, IDictionary<string, string> sink)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        var keyDot = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                        FlattenJson(prop.Value, keyDot, sink);
                    }
                    break;

                case JsonValueKind.Array:
                    // Represent array items by index: Key.0, Key.1, ...
                    int i = 0;
                    foreach (var item in el.EnumerateArray())
                    {
                        var keyDot = string.IsNullOrEmpty(prefix) ? i.ToString() : $"{prefix}.{i}";
                        FlattenJson(item, keyDot, sink);
                        i++;
                    }
                    break;

                case JsonValueKind.String:
                    EmitBoth(prefix!, el.GetString() ?? string.Empty, sink);
                    break;

                case JsonValueKind.Number:
                    EmitBoth(prefix!, el.ToString(), sink);
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    EmitBoth(prefix!, el.GetBoolean().ToString(), sink);
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    // Skip nulls/undefined
                    break;
            }
        }

        private static void EmitBoth(string dottedKey, string value, IDictionary<string, string> sink)
        {
            if (string.IsNullOrEmpty(dottedKey)) return;

            // A.B.C
            sink[dottedKey] = value;

            // A__B__C
            var dd = dottedKey.Replace('.', '_');   // A_B_C
            dd = dd.Replace("_", "__");             // A__B__C
            sink[dd] = value;
        }
    }
}