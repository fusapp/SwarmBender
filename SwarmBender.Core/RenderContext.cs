using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core;

/// <summary>
/// Mutable context carried across pipeline stages.
/// </summary>
/// <summary>Mutable context carried across pipeline stages.</summary>
public sealed class RenderContext
{
    public required RenderRequest Request { get; init; }
    public required IFileSystem FileSystem { get; init; }
    public required IYamlEngine Yaml { get; init; }

    // Yol kısayolları
    public required string RootPath { get; init; }            // Request.RootPath
    public required string StacksDir { get; init; }           // {root}/stacks
    public required string OpsDir { get; init; }              // {root}/ops
    public required string OutputDir { get; init; }           // {root}/{OutDir}

    // Çalışma nesneleri (TİP’LENMİŞ COMPOSE!)
    /// <summary>Yalnızca template’in deserialize edilmiş hâli (değiştirme!)</summary>
    public ComposeFile? Template { get; set; }

    /// <summary>Stage’ler burada çalışır (Template’ten kopyalanır ve üzerine overlay/label/env/secrets uygulanır)</summary>
    public ComposeFile? Working { get; set; }

    /// <summary>Overlay’lerden ve x-sb uzantılarından toplanan env/label/secret bilgileri (stage’ler arası paylaşım)</summary>
    public Dictionary<string, string> AggregatedEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> AggregatedLabels { get; }      = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider’lardan gelen gizli veriler (ProvidersAggregate → SecretsAttach akışı)</summary>
    public Dictionary<string, string> SecretsBag { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Çıktı/sonuç
    public string? OutFilePath { get; set; }      // SerializeStage doldurur
    public string? HistoryFilePath { get; set; }  // SerializeStage doldurur

    // Yardımcı oluşturucu
    public static RenderContext Create(RenderRequest req, IFileSystem fs, IYamlEngine yaml)
    {
        var root      = req.RootPath;
        var stacks    = System.IO.Path.Combine(root, "stacks");
        var ops       = System.IO.Path.Combine(root, "ops");
        var outDirAbs = System.IO.Path.IsPathRooted(req.OutDir)
            ? req.OutDir
            : System.IO.Path.Combine(root, req.OutDir);

        return new RenderContext
        {
            Request   = req,
            FileSystem= fs,
            Yaml      = yaml,
            RootPath  = root,
            StacksDir = stacks,
            OpsDir    = ops,
            OutputDir = outDirAbs
        };
    }
}