using YamlDotNet.Serialization;

namespace SwarmBender.Core.Data.Compose;

public sealed class PortMapping
{
    [YamlMember(Alias = "target", ApplyNamingConventions = false)]
    public int? Target { get; set; }       // container port

    [YamlMember(Alias = "published", ApplyNamingConventions = false)]
    public int? Published { get; set; }    // host port

    [YamlMember(Alias = "protocol", ApplyNamingConventions = false)]
    public string? Protocol { get; set; }  // "tcp"|"udp"|"sctp"

    [YamlMember(Alias = "mode", ApplyNamingConventions = false)]
    public string? Mode { get; set; }      // "ingress"|"host"

    public static bool TryParse(string s, out PortMapping pm)
    {
        // desteklenen string format örnekleri: "80:80", "80:80/tcp", "8080:80/udp"
        pm = new PortMapping();
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split('/', 2);
        var left  = parts[0];
        pm.Protocol = parts.Length == 2 ? parts[1] : "tcp";

        var colon = left.Split(':', 2);
        if (colon.Length == 2 && int.TryParse(colon[0], out var pub) && int.TryParse(colon[1], out var tar))
        {
            pm.Published = pub;
            pm.Target = tar;
            return true;
        }
        if (int.TryParse(left, out var single))
        {
            pm.Target = single;
            return true;
        }
        return false;
    }

    public override string ToString()
    {
        if (Published.HasValue && Target.HasValue)
            return $"{Published}:{Target}/{(Protocol ?? "tcp")}";
        if (Target.HasValue)
            return $"{Target}/{(Protocol ?? "tcp")}";
        return "0/unknown";
    }
}