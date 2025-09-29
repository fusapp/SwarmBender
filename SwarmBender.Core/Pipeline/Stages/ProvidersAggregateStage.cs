using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Providers.Azure;
using SwarmBender.Core.Providers.Infisical;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Enriches ctx.Env by aggregating from providers in the configured order:
    ///   file (already handled by EnvJsonCollectStage) -> env -> azure-kv -> infisical
    /// Providers merge with last-wins semantics.
    /// </summary>
    [StageUsage(PipelineMode.ConfigExport, PipelineMode.StackRender)]
    public sealed class ProvidersAggregateStage : IRenderStage
    {
        public int Order => 400;

        private readonly IFileSystem _fs;
        private readonly IAzureKvCollector _kv;
        private readonly IInfisicalCollector _inf;

        public ProvidersAggregateStage(IFileSystem fs, IAzureKvCollector kv, IInfisicalCollector inf)
        {
            _fs = fs;
            _kv = kv;
            _inf = inf;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            var envBag = ctx.Env;

            var order = ctx.Config?.Providers?.Order?.Select(o => o.Type)?.ToList();
            if (order is null || order.Count == 0)
                order = new List<string> { "file", "env", "azure-kv", "infisical" };

            foreach (var type in order)
            {
                ct.ThrowIfCancellationRequested();

                switch (type.Trim().ToLowerInvariant())
                {
                    case "file":
                        // Already collected at EnvJsonCollectStage
                        break;

                    case "env":
                        await MergeFromProcessEnvironmentAsync(ctx, envBag, ct).ConfigureAwait(false);
                        break;

                    case "azure-kv":
                        if (ctx.Config?.Providers?.AzureKv is { Enabled: true } akv)
                        {
                            var scope = $"{ctx.Request.StackId}/{ctx.Request.Env}";
                            var data = await _kv.CollectAsync(akv, scope, ct).ConfigureAwait(false);
                            foreach (var kvp in data)
                                envBag[kvp.Key] = kvp.Value;
                        }
                        break;

                    case "infisical":
                        if (ctx.Config?.Providers?.Infisical is { Enabled: true } inf)
                        {
                            var scope = $"{ctx.Request.StackId}/{ctx.Request.Env}";
                            var data = await _inf.CollectAsync(inf, ctx.Request.StackId,ctx.Request.Env, ct).ConfigureAwait(false);
                            foreach (var kvp in data)
                                envBag[kvp.Key] = kvp.Value;
                        }
                        break;

                    default:
                        // Unknown provider: ignore
                        break;
                }
            }
        }

        /// <summary>
        /// Reads OS env vars, filters by allowlist patterns gathered from configured files,
        /// and merges matches into envBag (last-wins).
        /// </summary>
        private async Task MergeFromProcessEnvironmentAsync(RenderContext ctx, Dictionary<string, string> envBag, CancellationToken ct)
        {
            var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var search = ctx.Config?.Providers?.Env?.AllowlistFileSearch;
            if (search is { Count: > 0 })
            {
                foreach (var patt in search)
                {
                    ct.ThrowIfCancellationRequested();

                    var resolved = patt
                        .Replace("{stackId}", ctx.Request.StackId, StringComparison.OrdinalIgnoreCase)
                        .Replace("{env}",     ctx.Request.Env,     StringComparison.OrdinalIgnoreCase);

                    // Glob returns repo-root relative paths (our IFileSystem contract),
                    // but be defensive if absolute leaks in.
                    var files = _fs.GlobFiles(ctx.RootPath, resolved)
                                   .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

                    foreach (var f in files)
                    {
                        var abs = Path.IsPathRooted(f) ? f : Path.Combine(ctx.RootPath, f);

                        try
                        {
                            var json = await _fs.ReadAllTextAsync(abs, ct).ConfigureAwait(false);
                            var arr = JsonSerializer.Deserialize<string[]>(json);
                            if (arr is null) continue;

                            foreach (var s in arr)
                                if (!string.IsNullOrWhiteSpace(s))
                                    patterns.Add(s);
                        }
                        catch
                        {
                            // Ignore malformed or missing files to keep CLI robust
                        }
                    }
                }
            }

            if (patterns.Count == 0) return;

            var regexes = patterns.Select(WildcardToRegex).ToArray();

            var proc = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry de in proc)
            {
                if (de.Key is not string key) continue;
                var val = de.Value?.ToString() ?? string.Empty;

                if (regexes.Any(rx => rx.IsMatch(key)))
                    envBag[key] = val; // last-wins
            }
        }

        private static Regex WildcardToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern)
                               .Replace(@"\*", ".*")
                               .Replace(@"\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}