using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MiniChat.Core.Config;

namespace MiniChat.Core.Network;

public enum NetworkCategory { Unknown, Public, Private, Domain }

/// <summary>Una dirección IPv4 local junto con la red a la que pertenece.</summary>
public sealed record LocalInterface(
    string InterfaceId,
    string Name,
    string Description,
    IPAddress Address,
    int PrefixLength,
    NetworkInterfaceType Type,
    NetworkCategory Category,
    string NetworkId,
    string NetworkName,
    bool LooksVirtualOrVpn)
{
    public IPAddress NetworkAddress => Subnet.NetworkOf(Address, PrefixLength);
    public string SubnetText => $"{NetworkAddress}/{PrefixLength}";
    /// <summary>Clave estable con la que el usuario autoriza explícitamente una red.</summary>
    public string TrustKey => $"{NetworkId}|{SubnetText}";
    public bool Contains(IPAddress remote) => Subnet.Contains(Address, PrefixLength, remote);

    /// <summary>Tiene puerta de enlace configurada (sin ella, Windows suele marcar la red como «no identificada»).</summary>
    public bool HasGateway { get; init; } = true;
    /// <summary>IP obtenida por DHCP (false = IP fija).</summary>
    public bool IsDhcp { get; init; } = true;
    /// <summary>Windows detectó que otro equipo usa la misma IP.</summary>
    public bool IsDuplicate { get; init; }
}

public sealed record InterfaceDecision(LocalInterface Interface, bool Allowed, string Reason);

public interface INetworkInventory
{
    IReadOnlyList<LocalInterface> GetInterfaces();

    /// <summary>Adaptadores activos sin IP válida (solo 169.254.x.x).</summary>
    IReadOnlyList<string> GetUnconfiguredAdapters() => Array.Empty<string>();

    /// <summary>Estado de Internet según Windows, o null si no se sabe. Solo informativo.</summary>
    bool? IsInternetAvailable() => null;

    /// <summary>
    /// Dirección local que Windows usaría para llegar a <paramref name="remote"/> (según su tabla de rutas), o null.
    /// Conectar un socket UDP no envía nada: solo resuelve la ruta.
    /// </summary>
    IPAddress? SourceAddressFor(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(remote, 9));
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Subredes de otras áreas definidas por el administrador del equipo (no editables por el usuario).</summary>
    IReadOnlyList<string> GetAdminRemoteSubnets() => Array.Empty<string>();
}

/// <summary>
/// Decide por qué interfaces y con qué direcciones remotas se permite comunicar.
/// Reglas:
///  - Solo IPv4 unicast de interfaces activas; se excluyen loopback, túneles, PPP y APIPA (169.254/16).
///  - Una IP privada no basta: la dirección remota debe pertenecer a la subred de una interfaz autorizada.
///  - Por defecto se autorizan redes con perfil Privado o Dominio que no parezcan VPN/virtuales.
///  - Redes Públicas, VPN o virtuales solo si el usuario las autoriza explícitamente en Ajustes.
///  - Las redes denegadas explícitamente nunca se usan.
///  - Subredes de otras áreas (enrutadas por el router de la oficina) solo si se añaden explícitamente
///    (Ajustes o administrador); se alcanzan por una interfaz autorizada según la tabla de rutas de Windows.
/// </summary>
public sealed class NetworkPolicy
{
    private readonly INetworkInventory _inventory;
    private int _blockedOutside;
    private readonly MiniChatOptions _options;
    private readonly object _gate = new();
    private HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InterfaceDecision> _decisions = Array.Empty<InterfaceDecision>();
    private IReadOnlyList<SubnetRange> _remote = Array.Empty<SubnetRange>();

    public NetworkPolicy(INetworkInventory inventory, MiniChatOptions options)
    {
        _inventory = inventory;
        _options = options;
    }

    public event EventHandler? Changed;

    public INetworkInventory Inventory => _inventory;

    /// <summary>Intentos de comunicar con direcciones fuera de las redes locales autorizadas (todos rechazados).</summary>
    public int BlockedOutsideLan => Volatile.Read(ref _blockedOutside);

    public void SetUserLists(IEnumerable<string> trusted, IEnumerable<string> denied)
    {
        lock (_gate)
        {
            _trusted = new HashSet<string>(trusted, StringComparer.OrdinalIgnoreCase);
            _denied = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
        }
        Refresh();
    }

    public IReadOnlyList<InterfaceDecision> Decisions { get { lock (_gate) return _decisions; } }

    /// <summary>Subredes de otras áreas autorizadas (usuario + administrador).</summary>
    public IReadOnlyList<SubnetRange> RemoteSubnets { get { lock (_gate) return _remote; } }

    public void SetRemoteSubnets(IEnumerable<SubnetRange> subnets)
    {
        lock (_gate) _remote = subnets.DistinctBy(r => r.Text).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>La dirección pertenece a una subred de otra área autorizada.</summary>
    public bool IsInRemoteSubnet(IPAddress remote)
    {
        remote = Normalize(remote);
        return remote.AddressFamily == AddressFamily.InterNetwork && RemoteSubnets.Any(r => r.Contains(remote));
    }

    /// <summary>La dirección está en la subred de alguna interfaz de este equipo (autorizada o no).</summary>
    public bool IsOnAttachedSubnet(IPAddress remote)
    {
        remote = Normalize(remote);
        return Decisions.Any(d => !IPAddress.IsLoopback(d.Interface.Address) && d.Interface.Contains(remote));
    }

    public IReadOnlyList<LocalInterface> AllowedInterfaces =>
        Decisions.Where(d => d.Allowed).Select(d => d.Interface).ToList();

    public void Refresh()
    {
        IReadOnlyList<LocalInterface> interfaces;
        try { interfaces = _inventory.GetInterfaces(); }
        catch { interfaces = Array.Empty<LocalInterface>(); }

        var decisions = new List<InterfaceDecision>();
        lock (_gate)
        {
            foreach (var i in interfaces)
                decisions.Add(Decide(i));
            _decisions = decisions;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private InterfaceDecision Decide(LocalInterface i)
    {
        if (IPAddress.IsLoopback(i.Address))
            return new(i, _options.AllowLoopback, _options.AllowLoopback ? "Loopback (solo pruebas)" : "Loopback");
        if (_denied.Contains(i.TrustKey)) return new(i, false, "Denegada por el usuario");
        if (_trusted.Contains(i.TrustKey)) return new(i, true, "Autorizada por el usuario");
        if (i.LooksVirtualOrVpn) return new(i, false, "Parece una VPN o adaptador virtual; autorícela en Ajustes si corresponde");
        return i.Category switch
        {
            NetworkCategory.Private => new(i, true, "Red privada"),
            NetworkCategory.Domain => new(i, true, "Red de dominio"),
            NetworkCategory.Public => new(i, false, "Red pública: autorícela en Ajustes si es su red de trabajo"),
            _ => new(i, false, "Perfil de red desconocido; autorícela en Ajustes si corresponde"),
        };
    }

    /// <summary>Interfaz autorizada cuya subred contiene <paramref name="remote"/>, o null.</summary>
    public LocalInterface? InterfaceFor(IPAddress remote)
    {
        remote = Normalize(remote);
        if (remote.AddressFamily != AddressFamily.InterNetwork) return null;
        foreach (var d in Decisions)
        {
            if (!d.Allowed) continue;
            if (IPAddress.IsLoopback(d.Interface.Address) && IPAddress.IsLoopback(remote)) return d.Interface;
            if (d.Interface.Contains(remote)) return d.Interface;
        }
        // Otra área de la oficina: se sale por la interfaz que indique la tabla de rutas, si está autorizada.
        if (!IsInRemoteSubnet(remote) || IsOnAttachedSubnet(remote)) return null;
        var source = _inventory.SourceAddressFor(remote);
        if (source is null || IPAddress.IsLoopback(source)) return null;
        return Decisions.FirstOrDefault(d => d.Allowed && d.Interface.Address.Equals(source))?.Interface;
    }

    /// <summary>Comprueba una conexión entrante: la IP local debe ser autorizada y la remota de su subred.</summary>
    public bool IsAllowedInbound(IPAddress local, IPAddress remote)
    {
        local = Normalize(local);
        remote = Normalize(remote);
        var iface = Decisions.FirstOrDefault(d => d.Allowed && d.Interface.Address.Equals(local))?.Interface;
        if (iface is null) return false;
        if (IPAddress.IsLoopback(local)) return IPAddress.IsLoopback(remote);
        return iface.Contains(remote) || (IsInRemoteSubnet(remote) && !IsOnAttachedSubnet(remote));
    }

    /// <summary>Explica por qué no se puede contactar una IP (para mensajes útiles al usuario).</summary>
    public string ExplainRejection(IPAddress remote)
    {
        Interlocked.Increment(ref _blockedOutside);
        remote = Normalize(remote);
        if (remote.AddressFamily != AddressFamily.InterNetwork) return "Solo se admiten direcciones IPv4 locales.";
        var denied = Decisions.FirstOrDefault(d => !d.Allowed && d.Interface.Contains(remote));
        if (denied is not null)
            return $"La IP {remote} pertenece a la red {denied.Interface.SubnetText} ({denied.Interface.Name}), que no está autorizada: {denied.Reason}.";
        var allowed = AllowedInterfaces;
        if (allowed.Count == 0)
            return "No hay ninguna red local autorizada. Revise Ajustes > Redes.";
        if (IsInRemoteSubnet(remote))
        {
            var source = _inventory.SourceAddressFor(remote);
            return source is null
                ? $"Windows no tiene ruta hacia {remote}: este equipo no puede llegar a esa subred."
                : $"Para llegar a {remote} Windows usa la dirección {source}, que no es de una red autorizada. Revise Ajustes › Redes.";
        }
        if (!SubnetRange.IsPrivate(remote))
            return $"{remote} no es una dirección de red local. MiniChat nunca se comunica a través de Internet.";
        return $"La IP {remote} no pertenece a su red ({string.Join(", ", allowed.Select(a => a.SubnetText))}). " +
               "Si es de otra área de la oficina, añada su subred en Ajustes › Redes › Otras subredes de la oficina.";
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

public static class Subnet
{
    public static IPAddress NetworkOf(IPAddress address, int prefix)
    {
        uint mask = Mask(prefix);
        uint value = ToUInt(address) & mask;
        return FromUInt(value);
    }

    public static bool Contains(IPAddress local, int prefix, IPAddress remote)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork) return false;
        uint mask = Mask(prefix);
        return (ToUInt(local) & mask) == (ToUInt(remote) & mask);
    }

    internal static uint Mask(int prefix) => prefix <= 0 ? 0u : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);

    internal static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    internal static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
