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
    /// Each provider merges with last-wins semantics.
    /// </summary>
    public sealed class ProvidersAggregateStage : IRenderStage
    {
        public int Order => 400;

        private readonly IFileSystem _fs;
        private readonly IAzureKvCollector _kv;
        private readonly IInfisicalCollector _inf;

        public ProvidersAggregateStage(
            IFileSystem fs,
            IAzureKvCollector kv,
            IInfisicalCollector inf)
        {
            _fs = fs;
            _kv = kv;
            _inf = inf;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            // Ensure env bag exists
            var envBag = ctx.Env;

            // Determine provider order from config or fallback
            var order = ctx.Config?.Providers?.Order?.Select(o => o.Type)?.ToList();
            if (order is null || order.Count == 0)
                order = new List<string> { "file", "env", "azure-kv", "infisical" };

            foreach (var type in order)
            {
                ct.ThrowIfCancellationRequested();

                switch (type.Trim().ToLowerInvariant())
                {
                    case "file":
                        // Already applied by EnvJsonCollectStage (kept for clarity).
                        break;

                    case "env":
                        MergeFromProcessEnvironment(ctx, envBag);
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
                            var data = await _inf.CollectAsync(inf, scope, ct).ConfigureAwait(false);
                            foreach (var kvp in data)
                                envBag[kvp.Key] = kvp.Value;
                        }
                        break;

                    default:
                        // Unknown provider name: ignore gracefully.
                        break;
                }
            }
        }

        /// <summary>
        /// Reads OS environment variables and merges an allow-listed subset into ctx.Env.
        /// Allowlist patterns are gathered from files defined in:
        ///   config.providers.env.allowlistFileSearch (supports {stackId}/{env} tokens, globbed via IFileSystem).
        /// Patterns are simple wildcards, e.g., "ConnectionStrings__*", "REDIS_*".
        /// </summary>
        private void MergeFromProcessEnvironment(RenderContext ctx, Dictionary<string, string> envBag)
        {
            // Collect allowlist patterns from configured files (JSON array of strings).
            var patterns = new List<string>();
            var search = ctx.Config?.Providers?.Env?.AllowlistFileSearch;
            if (search is { Count: > 0 })
            {
                foreach (var patt in search)
                {
                    var resolved = patt
                        .Replace("{stackId}", ctx.Request.StackId, StringComparison.OrdinalIgnoreCase)
                        .Replace("{env}",     ctx.Request.Env,     StringComparison.OrdinalIgnoreCase);

                    foreach (var file in _fs.GlobFiles(ctx.RootPath, resolved))
                    {
                        // Each file is expected to be a JSON array of strings (patterns)
                        var full = Path.Combine(ctx.RootPath, file);
                        var json = _fs.ReadAllTextAsync(full, CancellationToken.None).GetAwaiter().GetResult();
                        try
                        {
                            var arr = JsonSerializer.Deserialize<string[]>(json);
                            if (arr is not null)
                                patterns.AddRange(arr.Where(s => !string.IsNullOrWhiteSpace(s)));
                        }
                        catch
                        {
                            // Ignore malformed files; we prefer robustness over failure here.
                        }
                    }
                }
            }

            // If no patterns defined, there's nothing to import from OS env.
            if (patterns.Count == 0) return;

            // Convert wildcard patterns to compiled regex for case-insensitive matching.
            var regexes = patterns.Select(WildcardToRegex).ToArray();

            // Enumerate process env vars and include only allow-listed ones.
            var proc = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry de in proc)
            {
                if (de.Key is not string key) continue;
                var val = de.Value?.ToString() ?? string.Empty;

                if (regexes.Any(rx => rx.IsMatch(key)))
                {
                    envBag[key] = val; // last-wins
                }
            }
        }

        /// <summary>
        /// Converts a simple wildcard pattern to case-insensitive Regex.
        /// Supports '*' (zero or more chars) and '?' (single char).
        /// </summary>
        private static Regex WildcardToRegex(string pattern)
        {
            // Escape regex meta, then re-introduce wildcards.
            var escaped = Regex.Escape(pattern)
                               .Replace(@"\*", ".*")
                               .Replace(@"\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}