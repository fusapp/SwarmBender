using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Models;

/// <summary>In-memory model of ops/sb.yml (simplified for MVP; extend as needed).</summary>
public sealed class SbConfig
{
    [YamlMember(Alias = "version", ApplyNamingConventions = false)]
    public int Version { get; init; } = 1;

    [YamlMember(Alias = "render", ApplyNamingConventions = false)]
    public RenderSection Render { get; init; } = new();

    [YamlMember(Alias = "tokens", ApplyNamingConventions = false)]
    public TokensSection Tokens { get; init; } = new();

    [YamlMember(Alias = "secretize", ApplyNamingConventions = false)]
    public SecretizeSection Secretize { get; init; } = new();

    [YamlMember(Alias = "secrets", ApplyNamingConventions = false)]
    public SecretsSection Secrets { get; init; } = new();

    [YamlMember(Alias = "providers", ApplyNamingConventions = false)]
    public ProvidersSection Providers { get; init; } = new();

    [YamlMember(Alias = "metadata", ApplyNamingConventions = false)]
    public MetadataSection? Metadata { get; init; }

    [YamlMember(Alias = "schema", ApplyNamingConventions = false)]
    public SchemaSection? Schema { get; init; }
}

// --- render ---
public sealed class RenderSection
{
    [YamlMember(Alias = "appsettingsMode", ApplyNamingConventions = false)]
    public string AppsettingsMode { get; init; } = "env"; // env|config

    [YamlMember(Alias = "outDir", ApplyNamingConventions = false)]
    public string OutDir { get; init; } = "ops/state/last";

    [YamlMember(Alias = "writeHistory", ApplyNamingConventions = false)]
    public bool WriteHistory { get; init; } = true;

    [YamlMember(Alias = "overlayOrder", ApplyNamingConventions = false)]
    public List<string> OverlayOrder { get; init; } = new()
    {
        "stacks/all/{env}/stack/*.y?(a)ml",
        "stacks/{stackId}/{env}/stack/*.y?(a)ml"
    };
}

// --- tokens ---
public sealed class TokensSection
{
    // user tanımlı token’lar
    [YamlMember(Alias = "user", ApplyNamingConventions = false)]
    public Dictionary<string, string> User { get; init; } = new();
}

// --- secretize ---
public sealed class SecretizeSection
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; set; } = true;

    // flatten edilmiş anahtar desenleri (glob)
    [YamlMember(Alias = "paths", ApplyNamingConventions = false)]
    public List<string> Paths { get; set; } = new();
}

// --- secrets ---
public sealed class SecretsSection
{
    [YamlMember(Alias = "engine", ApplyNamingConventions = false)]
    public SecretsEngine Engine { get; init; } = new();

    /// <summary>
    /// Dış secret adı şablonu.
    /// Varsayılan: "{stackId}_{key}{_version}"
    /// Not: {_version} placeholder'ı varsa ve version boş değilse başına "_" koyarak eklenir.
    /// </summary>
    [YamlMember(Alias = "nameTemplate", ApplyNamingConventions = false)]
    public string NameTemplate { get; init; } = "{stackId}_{key}{_version}";

    /// <summary>
    /// Secret version üretimi: "content-sha" | "v1" | "" (kapalı).
    /// </summary>
    [YamlMember(Alias = "versionMode", ApplyNamingConventions = false)]
    public string VersionMode { get; init; } = "content-sha";

    /// <summary>
    /// Servis içi target adı için şablon. Varsayılan compose kanonu (ConnectionStrings__MSSQL__Master).
    /// Örn: "{key_compose}" veya "{key_flat}".
    /// </summary>
    [YamlMember(Alias = "targetTemplate", ApplyNamingConventions = false)]
    public string TargetTemplate { get; init; } = "{key_compose}";

    /// <summary>
    /// Anahtar kanonikleştirme modu:
    /// "compose" => '.' yerine "__" (env var/compose uyumlu)
    /// "flat"    => '.' ile hiyerarşi (örn. appsettings tarzı)
    /// </summary>
    [YamlMember(Alias = "canonicalization", ApplyNamingConventions = false)]
    public string Canonicalization { get; init; } = "compose";

    /// <summary>
    /// stackId gibi ön eklerin nasıl üretileceğini kontrol eder.
    /// Varsayılan: "{stackId}"
    /// Gerekirse "{company}", "{env}" vb token'larla zenginleştirilebilir.
    /// </summary>
    [YamlMember(Alias = "scopeTemplate", ApplyNamingConventions = false)]
    public string ScopeTemplate { get; init; } = "{stackId}";

    [YamlMember(Alias = "labels", ApplyNamingConventions = false)]
    public Dictionary<string, string> Labels { get; init; } = new()
    {
        ["owner"] = "swarmbender"
    };
}

public sealed class SecretsEngine
{
    // docker-cli | docker-dotnet
    [YamlMember(Alias = "type", ApplyNamingConventions = false)]
    public string Type { get; init; } = "docker-cli";

    [YamlMember(Alias = "args", ApplyNamingConventions = false)]
    public SecretsEngineArgs Args { get; init; } = new();
}

public sealed class SecretsEngineArgs
{
    [YamlMember(Alias = "dockerPath", ApplyNamingConventions = false)]
    public string DockerPath { get; init; } = "docker";

    [YamlMember(Alias = "dockerHost", ApplyNamingConventions = false)]
    public string DockerHost { get; init; } = "unix:///var/run/docker.sock";
}

// --- providers ---
public sealed class ProvidersSection
{
    // Kaynak sırası
    [YamlMember(Alias = "order", ApplyNamingConventions = false)]
    public List<ProviderOrderItem> Order { get; set; } = new()
    {
        new() { Type = "file" },
        new() { Type = "env" },
        new() { Type = "azure-kv" },
        new() { Type = "infisical" }
    };

    [YamlMember(Alias = "file", ApplyNamingConventions = false)]
    public ProvidersFile File { get; init; } = new();

    [YamlMember(Alias = "env", ApplyNamingConventions = false)]
    public ProvidersEnv Env { get; init; } = new();

    [YamlMember(Alias = "azure-kv", ApplyNamingConventions = false)]
    public ProvidersAzureKv AzureKv { get; set; } = new();

    [YamlMember(Alias = "infisical", ApplyNamingConventions = false)]
    public ProvidersInfisical Infisical { get; set; } = new();
}

public sealed class ProviderOrderItem
{
    [YamlMember(Alias = "type", ApplyNamingConventions = false)]
    public string Type { get; init; } = "";
}

public sealed class ProvidersFile
{
    [YamlMember(Alias = "extraJsonDirs", ApplyNamingConventions = false)]
    public List<string> ExtraJsonDirs { get; init; } = new();
}

public sealed class ProvidersEnv
{
    [YamlMember(Alias = "allowlistFileSearch", ApplyNamingConventions = false)]
    public List<string> AllowlistFileSearch { get; init; } = new();
}

public sealed class ProvidersAzureKv
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; init; }

    [YamlMember(Alias = "vaultUrl", ApplyNamingConventions = false)]
    public string? VaultUrl { get; init; }

    [YamlMember(Alias = "keyTemplate", ApplyNamingConventions = false)]
    public string KeyTemplate { get; init; } = "{key}";

    // flatten → secret name dönüşüm kuralları
    [YamlMember(Alias = "replace", ApplyNamingConventions = false)]
    public Dictionary<string, string> Replace { get; init; } = new()
    {
        ["__"] = "--"
    };

    [YamlMember(Alias = "routes", ApplyNamingConventions = false)]
    public List<SecretRouteRule> Routes { get; init; } = new();
}

public sealed class ProvidersInfisical
{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; init; }

    [YamlMember(Alias = "baseUrl", ApplyNamingConventions = false)]
    public string BaseUrl { get; init; } = "https://app.infisical.com";

    [YamlMember(Alias = "workspaceId", ApplyNamingConventions = false)]
    public string? WorkspaceId { get; init; }  // veya projectId/projectSlug/workspaceSlug

    [YamlMember(Alias = "envMap", ApplyNamingConventions = false)]
    public Dictionary<string, string> EnvMap { get; init; } = new()
    {
        ["dev"] = "dev",
        ["prod"] = "prod"
    };

    [YamlMember(Alias = "pathTemplate", ApplyNamingConventions = false)]
    public string PathTemplate { get; init; } = "/{scope}";

    [YamlMember(Alias = "keyTemplate", ApplyNamingConventions = false)]
    public string KeyTemplate { get; init; } = "{key}";

    [YamlMember(Alias = "replace", ApplyNamingConventions = false)]
    public Dictionary<string, string> Replace { get; init; } = new()
    {
        ["__"] = "_"
    };

    [YamlMember(Alias = "include", ApplyNamingConventions = false)]
    public List<string> Include { get; init; } = new()
    {
        "ConnectionStrings__*",
        "Redis__*",
        "Mongo__*"
    };

    [YamlMember(Alias = "routes", ApplyNamingConventions = false)]
    public List<SecretRouteRule> Routes { get; init; } = new();
}

// --- metadata ---
public sealed class MetadataSection
{
    [YamlMember(Alias = "groups", ApplyNamingConventions = false)]
    public List<MetadataGroup> Groups { get; init; } = new();

    [YamlMember(Alias = "tenants", ApplyNamingConventions = false)]
    public List<MetadataTenant> Tenants { get; init; } = new();
}

public sealed class MetadataGroup
{
    [YamlMember(Alias = "id", ApplyNamingConventions = false)]
    public string Id { get; init; } = "";

    [YamlMember(Alias = "description", ApplyNamingConventions = false)]
    public string? Description { get; init; }
}

public sealed class MetadataTenant
{
    [YamlMember(Alias = "id", ApplyNamingConventions = false)]
    public string Id { get; init; } = "";

    [YamlMember(Alias = "slug", ApplyNamingConventions = false)]
    public string Slug { get; init; } = "";

    [YamlMember(Alias = "groups", ApplyNamingConventions = false)]
    public List<string> Groups { get; init; } = new();
}

// --- schema ---
public sealed class SchemaSection
{
    [YamlMember(Alias = "required", ApplyNamingConventions = false)]
    public List<string> Required { get; init; } = new();

    [YamlMember(Alias = "enums", ApplyNamingConventions = false)]
    public Dictionary<string, List<string>> Enums { get; init; } = new();
}

public sealed class SecretRouteRule
{
    // Anahtar kalıbı(ları) (glob). İlk eşleşen kural kazanır.
    [YamlMember(Alias = "match", ApplyNamingConventions = false)]
    public List<string> Match { get; init; } = new();

    // Okuma yolları (sıralı). İlk bulunan mevcut değer kabul edilir.
    [YamlMember(Alias = "readPaths", ApplyNamingConventions = false)]
    public List<string> ReadPaths { get; init; } = new();

    // Yazım hedefi (upsert buraya). Zorunlu.
    [YamlMember(Alias = "writePath", ApplyNamingConventions = false)]
    public string WritePath { get; init; } = "/";

    // Legacy’den yeni path’e sessiz taşıma için işaret (ileride delete-legacy eklenebilir)
    [YamlMember(Alias = "migrate", ApplyNamingConventions = false)]
    public bool? Migrate { get; init; } = false;
}