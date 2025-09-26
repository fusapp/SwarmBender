namespace SwarmBender.Core.Data.Compose;

/// <summary>
/// sysctls:
///   net.core.somaxconn: 1024
///   net.ipv4.tcp_syncookies: "1"
/// Değerler string olarak normalize edilir (scalar int/str fark etmez).
/// </summary>
public sealed class Sysctls
{
    public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
}