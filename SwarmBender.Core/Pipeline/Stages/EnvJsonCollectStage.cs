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
    /// plus any extra directories from SbConfig.Providers.File.ExtraJsonDirs.
    /// Files are flattened (dot + double-underscore forms) and merged (last-wins).
    /// </summary>
    [StageUsage(PipelineMode.ConfigExport, PipelineMode.StackRender)]
    public sealed class EnvJsonCollectStage : IRenderStage
    {
        public int Order => 300;

        private readonly IFileSystem _fs;

        public EnvJsonCollectStage(IFileSystem fs) => _fs = fs;

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            var dirs = new List<string>
            {
                $"stacks/all/common/env",
                $"stacks/all/{ctx.Request.Env}/env",
                $"stacks/{ctx.Request.StackId}/{ctx.Request.Env}/env"
            };

            var extra = ctx.Config?.Providers?.File?.ExtraJsonDirs;
            if (extra is { Count: > 0 })
            {
                foreach (var d in extra)
                    dirs.Add(ReplaceTokens(d, ctx.Request.StackId, ctx.Request.Env));
            }

            // Resolve files (alphabetical per directory), then process in listed order
            var filesOrdered = new List<string>();
            foreach (var dir in dirs)
            {
                var pattern = MakeFolderPattern(dir, "*.json");
                var hits = _fs.GlobFiles(ctx.RootPath, pattern)
                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                filesOrdered.AddRange(hits);
            }

            foreach (var file in filesOrdered)
            {
                ct.ThrowIfCancellationRequested();

                var abs = Path.IsPathRooted(file) ? file : Path.Combine(ctx.RootPath, file);

                string json;
                try
                {
                    json = await _fs.ReadAllTextAsync(abs, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to read JSON file: {abs}", ex);
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(json);
                }
                catch (JsonException jx)
                {
                    throw new FormatException($"Invalid JSON in {abs} (pos {jx.LineNumber}:{jx.BytePositionInLine}).", jx);
                }

                var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                FlattenJson(doc.RootElement, prefix: null, flat);

                foreach (var kv in flat)
                    ctx.Env[kv.Key] = kv.Value;
            }
        }

        private static string ReplaceTokens(string input, string stackId, string env)
            => input.Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{env}",     env,     StringComparison.OrdinalIgnoreCase);

        private static string MakeFolderPattern(string folder, string mask)
        {
            var f = folder.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(f) ? mask : $"{f}/{mask}".Replace('\\','/');
        }

        /// <summary>
        /// Flatten JSON into key/value pairs. For each logical path "A.B.C",
        /// emit both "A.B.C" and "A__B__C".
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
                    int i = 0;
                    foreach (var item in el.EnumerateArray())
                    {
                        var keyDot = string.IsNullOrEmpty(prefix) ? i.ToString() : $"{prefix}.{i}";
                        FlattenJson(item, keyDot, sink);
                        i++;
                    }
                    break;

                case JsonValueKind.String:
                    if (!string.IsNullOrEmpty(prefix))
                        EmitDouble(prefix, el.GetString() ?? string.Empty, sink);
                    break;

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    if (!string.IsNullOrEmpty(prefix))
                        EmitDouble(prefix, el.ToString(), sink);
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    // ignore
                    break;
            }
        }
        
        private static void EmitDouble(string dottedKey, string value, IDictionary<string, string> sink)
        {
            if (string.IsNullOrEmpty(dottedKey)) return;
            // Sadece A__B__C üret
            var dd = dottedKey.Replace(".", "__"); // A_B_C
            //dd = dd.Replace("_", "__");           // A__B__C
            sink[dd] = value;
        }
    }
}