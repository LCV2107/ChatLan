using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MiniChat.Core.Diagnostics;

namespace MiniChat.Core.Network;

/// <summary>
/// Inventario real de interfaces de Windows. El perfil de red (Público/Privado/Dominio) se obtiene del
/// Network List Manager (API COM documentada de Windows, netlistmgr.h). Si falla, la categoría queda
/// como Desconocida y la red no se autoriza automáticamente (falla de forma segura).
/// </summary>
public sealed class WindowsNetworkInventory : INetworkInventory
{
    private static readonly string[] VirtualHints =
    {
        "vpn", "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "tap-", "tap ", "tun", "wireguard",
        "openvpn", "fortinet", "forticlient", "anyconnect", "cisco", "globalprotect", "pangp", "zerotier",
        "tailscale", "hamachi", "npcap", "loopback", "wsl", "docker", "nordlynx", "proton",
    };

    private readonly ILog _log;

    public WindowsNetworkInventory(ILog? log = null) => _log = log ?? NullLog.Instance;

    private List<string> _unconfigured = new();

    /// <summary>Adaptadores físicos activos que solo tienen una IP 169.254.x.x (no obtuvieron dirección).</summary>
    public IReadOnlyList<string> GetUnconfiguredAdapters() => _unconfigured;

    public IReadOnlyList<LocalInterface> GetInterfaces()
    {
        var categories = ReadNetworkCategories();
        var result = new List<LocalInterface>();
        var unconfigured = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
            bool isLoopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }

            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            bool isDhcp = true;
            try { isDhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? true; } catch (NetworkInformationException) { }
            var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool looksVirtual = !isLoopback && VirtualHints.Any(h => text.Contains(h));
            bool onlyApipa = true;

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA: sin DHCP, no es una red de trabajo
                onlyApipa = false;
                categories.TryGetValue(ni.Id, out var cat);
                result.Add(new LocalInterface(
                    ni.Id, ni.Name, ni.Description, ua.Address, ua.PrefixLength, ni.NetworkInterfaceType,
                    isLoopback ? NetworkCategory.Private : cat.Category,
                    cat.NetworkId ?? "sin-id", cat.Name ?? "", looksVirtual)
                {
                    HasGateway = hasGateway || isLoopback,
                    IsDhcp = isDhcp,
                    IsDuplicate = ua.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate,
                });
            }
            bool physical = ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet;
            if (onlyApipa && physical && !looksVirtual && !text.Contains("bluetooth")
                && props.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                unconfigured.Add(ni.Name);
        }
        _unconfigured = unconfigured;
        return result;
    }

    /// <summary>Clave y valor (HKLM) donde el administrador define las subredes de otras áreas para todos los usuarios.</summary>
    public const string AdminPolicyKey = @"SOFTWARE\MiniChatLAN";
    public const string AdminRemoteSubnetsValue = "SubredesRemotas";

    /// <summary>Subredes definidas por el administrador (tools\firewall-subredes.ps1), separadas por comas o punto y coma.</summary>
    public IReadOnlyList<string> GetAdminRemoteSubnets()
    {
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(AdminPolicyKey);
            if (key?.GetValue(AdminRemoteSubnetsValue) is not string text) return Array.Empty<string>();
            return text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("net", "No se pudo leer la directiva de subredes del administrador", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>Lo que Windows sabe sobre la salida a Internet (comprobación propia de Windows; MiniChat no la hace).</summary>
    public bool? IsInternetAvailable()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return null;
            manager = Activator.CreateInstance(type);
            return ((INetworkListManager)manager!).get_IsConnectedToInternet();
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager);
        }
    }

    private Dictionary<string, (NetworkCategory Category, string? NetworkId, string? Name)> ReadNetworkCategories()
    {
        var map = new Dictionary<string, (NetworkCategory, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return map;
            manager = Activator.CreateInstance(type);
            var nlm = (INetworkListManager)manager!;
            var connections = nlm.GetNetworkConnections();
            while (true)
            {
                var buffer = new INetworkConnection[1];
                connections.Next(1, buffer, out uint fetched);
                if (fetched == 0) break;
                var conn = buffer[0];
                var network = conn.GetNetwork();
                var category = network.GetCategory() switch
                {
                    0 => NetworkCategory.Public,
                    1 => NetworkCategory.Private,
                    2 => NetworkCategory.Domain,
                    _ => NetworkCategory.Unknown,
                };
                var adapter = conn.GetAdapterId().ToString("B").ToUpperInvariant();
                map[adapter] = (category, network.GetNetworkId().ToString("N"), network.GetName());
                Marshal.ReleaseComObject(network);
                Marshal.ReleaseComObject(conn);
            }
            Marshal.ReleaseComObject(connections);
        }
        catch (Exception ex)
        {
            _log.Warn("net", "No se pudo leer el perfil de red de Windows; las redes quedan sin autorizar por defecto", ex);
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager);
        }
        return map;
    }

    // ---- Declaraciones COM mínimas del Network List Manager (netlistmgr.h). Orden de vtable exacto. ----

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnection(Guid connectionId);
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object get__NewEnum();
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] elements, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool get_IsConnectedToInternet();
        bool get_IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        bool get_IsConnectedToInternet();
        bool get_IsConnected();
        int GetConnectivity();
        int GetCategory();
    }
}
