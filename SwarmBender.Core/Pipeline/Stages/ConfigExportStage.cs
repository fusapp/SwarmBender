using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;
using SwarmBender.Core.Util;

namespace SwarmBender.Core.Pipeline.Stages;

[StageUsage(PipelineMode.ConfigExport)]
public sealed class ConfigExportStage : IRenderStage
{
    public int Order => 800;

    private readonly IFileSystem _fs;

    public ConfigExportStage(IFileSystem fs) => _fs = fs;

    public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
    {
        if (ctx.Working is null)
            throw new InvalidOperationException("Working model is null. Nothing to export.");

        // 1) Final env’leri topla (TokenExpand + EnvStringify sonrası)
        var flatAll = CollectFinalEnv(ctx.Working);

        // 2) Secretize kalıplarını derle
        var secretMatchers = BuildSecretMatchers(ctx.Config);

        // 3) Secretize edilenleri çıkar + anahtarları kanonikle ('.' -> '__')
        var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, val) in flatAll)
        {
            var keyCanon = SecretUtil.ToComposeCanon(rawKey);

            // secretize match'i varsa SKIP
            if (secretMatchers.Any(rx => rx.IsMatch(rawKey) || rx.IsMatch(keyCanon)))
                continue;

            flat[keyCanon] = val ?? string.Empty; // last-wins
        }

        // 4) Unflatten → JSON
        var json = ConfigJsonUtil.UnflattenToJson(flat, indented: true);

        // 5) Yaz
        var outName = $"{San(ctx.Request.StackId)}-{San(ctx.Request.Env)}.appsettings.json";
        var outPath = Path.Combine(ctx.OutputDir, outName);
        _fs.EnsureDirectory(ctx.OutputDir);
        await _fs.WriteAllTextAsync(outPath, json, ct);

        ctx.OutFilePath = outPath;
    }

    // ---- helpers ----

    private static string San(string s)
        => string.IsNullOrWhiteSpace(s)
            ? "unknown"
            : s.Replace(Path.DirectorySeparatorChar, '-')
               .Replace(Path.AltDirectorySeparatorChar, '-');

    private static Dictionary<string, string> CollectFinalEnv(ComposeFile root)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.Services is null) return bag;

        foreach (var (_, svc) in root.Services)
        {
            var map = ToMap(svc.Environment);
            foreach (var (k, v) in map)
                bag[k] = v ?? string.Empty; // last-wins across services
        }
        return bag;
    }

    private static Dictionary<string, string> ToMap(ListOrDict? lod)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lod is null) return dict;

        if (lod.AsMap is not null)
        {
            foreach (var (k, v) in lod.AsMap)
                dict[k] = v ?? string.Empty;
            return dict;
        }

        if (lod.AsList is not null)
        {
            foreach (var item in lod.AsList)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                var idx = item.IndexOf('=', StringComparison.Ordinal);
                if (idx < 0) dict[item.Trim()] = string.Empty;
                else
                {
                    var k = item[..idx].Trim();
                    var v = item[(idx + 1)..];
                    if (!string.IsNullOrEmpty(k)) dict[k] = v;
                }
            }
        }
        return dict;
    }

    private static IEnumerable<Regex> BuildSecretMatchers(SwarmBender.Core.Data.Models.SbConfig cfg)
    {
        var list = new List<Regex>();
        if (cfg?.Secretize?.Enabled ?? false && cfg.Secretize.Paths is { Count: > 0 })
        {
            foreach (var pat in cfg.Secretize.Paths)
            {
                var esc = Regex.Escape(pat).Replace(@"\*", ".*").Replace(@"\?", ".");
                list.Add(new Regex("^" + esc + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
        }
        return list;
    }
}