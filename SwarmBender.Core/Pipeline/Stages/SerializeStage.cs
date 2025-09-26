using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Serializes the typed compose model (ctx.Working) to YAML and writes it under ctx.OutputDir.
    /// Also writes a timestamped history copy when config says so.
    /// Sets ctx.OutFilePath and ctx.HistoryFilePath accordingly.
    /// </summary>
    public sealed class SerializeStage : IRenderStage
    {
        public int Order => 800;

        private readonly IFileSystem _fs;
        private readonly IYamlEngine _yaml;

        public SerializeStage(IFileSystem fs, IYamlEngine yaml)
        {
            _fs = fs;
            _yaml = yaml;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null. Nothing to serialize.");

            // Ensure output directory exists
            _fs.EnsureDirectory(ctx.OutputDir);

            // Build output file names
            var stack = ctx.Request.StackId;
            var env   = ctx.Request.Env;

            // e.g. demo-dev.stack.yml
            var fileName = $"{Sanitize(stack)}-{Sanitize(env)}.stack.yml";
            var outPath  = Path.Combine(ctx.OutputDir, fileName);

            // Serialize model to YAML
            var yamlText = await _yaml.DumpYamlAsync(ctx.Working).ConfigureAwait(false);

            // Write "last" output
            await _fs.WriteAllTextAsync(outPath, yamlText, ct).ConfigureAwait(false);
            ctx.OutFilePath = outPath;

            // History: ops/state/{yyyyMMdd_HHmmss}/demo-dev.stack.yml (only if enabled)
            var writeHistory = ctx.Config?.Render?.WriteHistory ?? ctx.Request.WriteHistory;
            if (writeHistory)
            {
                // If request.OutDir already points to ".../ops/state/last", place history next to it:
                // replace trailing "last" with timestamp folder if present, else create under ops/state/
                var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

                // Default history root: <repo>/ops/state/<timestamp>/
                var defaultHistoryRoot = Path.Combine(ctx.OpsDir, "state", ts);

                // If OutputDir ends with ".../ops/state/last", map to sibling timestamp
                string historyRoot;
                var lastDir = Path.Combine(ctx.OpsDir, "state", "last").TrimEnd(Path.DirectorySeparatorChar);
                if (ctx.OutputDir.TrimEnd(Path.DirectorySeparatorChar)
                      .Equals(lastDir, StringComparison.OrdinalIgnoreCase))
                {
                    historyRoot = Path.Combine(ctx.OpsDir, "state", ts);
                }
                else
                {
                    // Otherwise just create a timestamped dir beside OutputDir's parent
                    historyRoot = defaultHistoryRoot;
                }

                _fs.EnsureDirectory(historyRoot);

                var historyPath = Path.Combine(historyRoot, fileName);
                await _fs.WriteAllTextAsync(historyPath, yamlText, ct).ConfigureAwait(false);

                ctx.HistoryFilePath = historyPath;
            }
        }

        private static string Sanitize(string s)
            => string.IsNullOrWhiteSpace(s) ? "unknown"
               : s.Replace(Path.DirectorySeparatorChar, '-')
                  .Replace(Path.AltDirectorySeparatorChar, '-');
    }
}