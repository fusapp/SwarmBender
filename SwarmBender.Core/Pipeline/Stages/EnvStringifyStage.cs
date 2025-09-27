using System.Globalization;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages;

/// <summary>
/// Environment'ı map yerine list (KEY=VALUE) formatına çevirir.
/// Böylece YAML'nın bool/number parse etmesi engellenir; tüm env değerleri string olur.
/// </summary>
public sealed class EnvStringifyStage : IRenderStage
{
    // TokenExpand (700)'den sonra, Serialize (800)'den önce
    public int Order => 710;

    public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
    {
        var model = ctx.Working;
        if (model?.Services is null || model.Services.Count == 0)
            return Task.CompletedTask;

        foreach (var (svcName, svc) in model.Services)
        {
            ct.ThrowIfCancellationRequested();
            var env = svc.Environment;
            if (env is null) continue;

            // 1) Map -> deterministik sırayla KEY=VALUE list'e çevir
            if (env.AsMap is not null)
            {
                var lines = new List<string>(env.AsMap.Count);

                // deterministik çıktı için alfabetik sırala
                foreach (var kv in env.AsMap.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var key = (kv.Key ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(key)) continue;

                    var sval = kv.Value is null
                        ? string.Empty
                        : Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty;

                    lines.Add($"{key}={sval}");
                }

                svc.Environment = ListOrDict.FromList(lines);
                continue;
            }

            // 2) Zaten list ise normalize et (KEY veya KEY=VAL formatına zorla)
            if (env.AsList is not null)
            {
                var outList = new List<string>(env.AsList.Count);
                foreach (var raw in env.AsList)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var idx = raw.IndexOf('=', StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        // çıplak KEY -> KEY=
                        var key = raw.Trim();
                        if (!string.IsNullOrEmpty(key)) outList.Add($"{key}=");
                    }
                    else
                    {
                        var key = raw[..idx].Trim();
                        var val = raw[(idx + 1)..]; // string zaten
                        if (!string.IsNullOrEmpty(key))
                            outList.Add($"{key}={val ?? string.Empty}");
                    }
                }

                svc.Environment = ListOrDict.FromList(outList);
            }
        }

        return Task.CompletedTask;
    }
}