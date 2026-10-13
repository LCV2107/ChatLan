namespace MiniChat.Core.Network;

/// <summary>Perfiles del Firewall de Windows (NET_FW_PROFILE_TYPE2).</summary>
[Flags]
public enum FirewallProfiles { None = 0, Domain = 1, Private = 2, Public = 4 }

public sealed record FirewallRuleInfo(string Name, bool Inbound, bool Allow, FirewallProfiles Profiles, int Protocol, string LocalPorts, bool Enabled)
{
    /// <summary>Direcciones remotas admitidas por la regla («*», «LocalSubnet», «10.10.161.0/255.255.255.0», rangos…).</summary>
    public string RemoteAddresses { get; init; } = "*";
}

public sealed record FirewallState(
    bool Readable,
    FirewallProfiles CurrentProfiles,
    bool EnabledOnCurrentProfile,
    IReadOnlyList<FirewallRuleInfo> RulesForProgram,
    string? Error);

/// <summary>
/// Lectura (nunca escritura) de la configuración del Firewall de Windows mediante su API COM documentada
/// (HNetCfg.FwPolicy2 / INetFwPolicy2). Funciona como usuario estándar.
/// </summary>
public static class FirewallInspector
{
    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;
    public const int ProtocolAny = 256;

    public static FirewallState Inspect(string programPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return new(false, FirewallProfiles.None, false, Array.Empty<FirewallRuleInfo>(), "API del firewall no disponible.");
            dynamic policy = Activator.CreateInstance(type)!;
            var current = (FirewallProfiles)(int)policy.CurrentProfileTypes;
            bool enabled = false;
            foreach (var p in new[] { FirewallProfiles.Domain, FirewallProfiles.Private, FirewallProfiles.Public })
                if (current.HasFlag(p) && (bool)policy.FirewallEnabled[(int)p]) enabled = true;

            var full = Path.GetFullPath(programPath);
            var rules = new List<FirewallRuleInfo>();
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app is null || !string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), full, StringComparison.OrdinalIgnoreCase)) continue;
                rules.Add(new FirewallRuleInfo(
                    (string)(rule.Name ?? ""),
                    (int)rule.Direction == 1,
                    (int)rule.Action == 1,
                    (FirewallProfiles)((int)rule.Profiles & 7),
                    (int)rule.Protocol,
                    (string)(rule.LocalPorts ?? "*"),
                    (bool)rule.Enabled)
                {
                    RemoteAddresses = (string)(rule.RemoteAddresses ?? "*"),
                });
            }
            return new(true, current, enabled, rules, null);
        }
        catch (Exception ex)
        {
            return new(false, FirewallProfiles.None, false, Array.Empty<FirewallRuleInfo>(), ex.Message);
        }
    }

    /// <summary>¿La regla cubre este protocolo y puerto locales?</summary>
    public static bool Covers(FirewallRuleInfo r, int protocol, int port)
    {
        if (r.Protocol != ProtocolAny && r.Protocol != protocol) return false;
        if (r.LocalPorts is "*" or "" ) return true;
        foreach (var part in r.LocalPorts.Split(','))
        {
            var range = part.Trim().Split('-');
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port) return true;
            if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi) && port >= lo && port <= hi) return true;
        }
        return false;
    }

    /// <summary>¿La regla admite conexiones desde toda la subred indicada?</summary>
    public static bool CoversRemote(FirewallRuleInfo r, SubnetRange subnet)
    {
        uint first = Subnet.ToUInt(subnet.Network);
        uint last = first + (uint)((1L << (32 - subnet.PrefixLength)) - 1);
        foreach (var raw in r.RemoteAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw is "*" || raw.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
            if (TryParseRange(raw, out var lo, out var hi) && lo <= first && last <= hi) return true;
        }
        return false;
    }

    private static bool TryParseRange(string text, out uint lo, out uint hi)
    {
        lo = hi = 0;
        var dash = text.Split('-');
        if (dash.Length == 2)
        {
            if (!System.Net.IPAddress.TryParse(dash[0], out var a) || !System.Net.IPAddress.TryParse(dash[1], out var b)
                || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || b.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            lo = Subnet.ToUInt(a);
            hi = Subnet.ToUInt(b);
            return lo <= hi;
        }
        var slash = text.Split('/');
        if (!System.Net.IPAddress.TryParse(slash[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        uint mask = uint.MaxValue;
        if (slash.Length == 2)
        {
            if (int.TryParse(slash[1], out var prefix) && prefix is >= 0 and <= 32) mask = Subnet.Mask(prefix);
            else if (System.Net.IPAddress.TryParse(slash[1], out var m) && m.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) mask = Subnet.ToUInt(m);
            else return false;
        }
        lo = Subnet.ToUInt(ip) & mask;
        hi = lo | ~mask;
        return true;
    }

    public static string ProfileName(FirewallProfiles p) => p switch
    {
        FirewallProfiles.Domain => "Dominio",
        FirewallProfiles.Private => "Privado",
        FirewallProfiles.Public => "Público",
        _ => string.Join(" + ", new[] { FirewallProfiles.Domain, FirewallProfiles.Private, FirewallProfiles.Public }.Where(x => p.HasFlag(x)).Select(ProfileName)),
    };
}
