using System.Net;
using System.Net.Sockets;

namespace MiniChat.Core.Network;

/// <summary>
/// Una subred IPv4 en notación CIDR (p. ej. 10.10.161.0/24), normalizada a su dirección de red.
/// Se usa para autorizar subredes de otras áreas de la oficina a las que se llega a través del router.
/// </summary>
public sealed record SubnetRange(IPAddress Network, int PrefixLength)
{
    /// <summary>Subred más grande admitida (/16 = 65 534 equipos).</summary>
    public const int MinPrefix = 16;
    public const int MaxPrefix = 30;

    public string Text => $"{Network}/{PrefixLength}";
    public override string ToString() => Text;

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return Subnet.Contains(Network, PrefixLength, address);
    }

    public bool Overlaps(SubnetRange other)
    {
        int p = Math.Min(PrefixLength, other.PrefixLength);
        return Subnet.Contains(Network, p, other.Network);
    }

    /// <summary>Número de direcciones de equipo (sin la de red ni la de difusión).</summary>
    public long HostCount => (1L << (32 - PrefixLength)) - 2;

    /// <summary>Direcciones de equipo de la subred, en orden (sin la de red ni la de difusión).</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        uint first = Subnet.ToUInt(Network) + 1;
        uint last = Subnet.ToUInt(Network) + (uint)((1L << (32 - PrefixLength)) - 2);
        for (uint v = first; v <= last && v >= first; v++)
            yield return Subnet.FromUInt(v);
    }

    /// <summary>
    /// Interpreta «10.10.161.0/24», «10.10.161.7/24» (se normaliza a la red) o «10.10.161.0/255.255.255.0».
    /// Solo se aceptan rangos privados (RFC 1918) de /16 a /30. <paramref name="allowLoopback"/> es solo para pruebas.
    /// </summary>
    public static bool TryParse(string? text, out SubnetRange? range, out string error, bool allowLoopback = false)
    {
        range = null;
        text = (text ?? "").Trim();
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0].Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || parts[0].Trim().Count(ch => ch == '.') != 3)
        {
            error = "Escriba la subred con su máscara, por ejemplo 10.10.161.0/24.";
            return false;
        }
        int prefix;
        var maskText = parts[1].Trim();
        if (maskText.Contains('.'))
        {
            if (!IPAddress.TryParse(maskText, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork || !TryMaskToPrefix(mask, out prefix))
            {
                error = $"«{maskText}» no es una máscara de subred válida.";
                return false;
            }
        }
        else if (!int.TryParse(maskText, out prefix) || prefix is < 0 or > 32)
        {
            error = $"«/{maskText}» no es un prefijo válido (de /{MinPrefix} a /{MaxPrefix}).";
            return false;
        }
        if (prefix is < MinPrefix or > MaxPrefix)
        {
            error = prefix < MinPrefix
                ? $"La subred /{prefix} es demasiado grande. Use como máximo /{MinPrefix} (65 534 equipos), o añada varias más pequeñas."
                : $"La subred /{prefix} es demasiado pequeña. Use de /{MinPrefix} a /{MaxPrefix}.";
            return false;
        }
        var network = Subnet.NetworkOf(ip, prefix);
        bool loopback = IPAddress.IsLoopback(network);
        if (!(IsPrivate(network) || (allowLoopback && loopback)) || !(IsPrivate(Subnet.FromUInt(Subnet.ToUInt(network) | ~Subnet.Mask(prefix))) || (allowLoopback && loopback)))
        {
            error = $"{network}/{prefix} no es un rango de red local privado (10.x.x.x, 172.16–31.x.x o 192.168.x.x). MiniChat nunca se comunica con direcciones de Internet.";
            return false;
        }
        range = new SubnetRange(network, prefix);
        error = "";
        return true;
    }

    public static SubnetRange? ParseOrNull(string? text, bool allowLoopback = false) =>
        TryParse(text, out var r, out _, allowLoopback) ? r : null;

    /// <summary>Direcciones IPv4 privadas (RFC 1918).</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static bool TryMaskToPrefix(IPAddress mask, out int prefix)
    {
        uint m = Subnet.ToUInt(mask);
        prefix = 0;
        while (prefix < 32 && (m & (0x80000000u >> prefix)) != 0) prefix++;
        return m == Subnet.Mask(prefix);
    }
}
