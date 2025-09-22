namespace SwarmBender.Services.Models;

// POCOs are minimal because we validate maps dynamically.
// Keeping models simple avoids over-constraining future keys.
public sealed class TenantSpec
{
    public string? DisplayName { get; init; }
    public IDictionary<string, string> Vars { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public TraefikSpec Traefik { get; init; } = new();
    public IList<string> Domains { get; init; } = new List<string>();
}

public sealed class TraefikSpec
{
    public string? CertResolver { get; init; }
}

public sealed class GroupSpec
{
    public IList<string> Services { get; init; } = new List<string>();
    public IDictionary<string, object?> Defaults { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}