using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using MiniChat.Core.Discovery;
using MiniChat.Core.Network;
using MiniChat.Core.Protocol;
using MiniChat.Core.Transport;

namespace MiniChat.Core.Engine;

public enum FindingSeverity { Ok, Info, Warning, Error }

/// <summary>Resultado de una comprobación del diagnóstico de red, con la causa y cómo resolverla.</summary>
public sealed record NetworkFinding(FindingSeverity Severity, string Title, string Detail, string? Fix = null);

public sealed record ConnectionTestResult(bool Success, string Summary, double? HandshakeMs, double? RoundTripMs, string? Endpoint, string? Tls);

/// <summary>Estado para demostrar que MiniChat funciona solo con la red local.</summary>
public sealed record OfflineStatus(bool? InternetAvailable, IReadOnlyList<string> AuthorizedSubnets, int PeersOnline, int ContactsOnline, int BlockedOutsideLan)
{
    /// <summary>Están activas las reglas de tools\prueba-sin-internet.ps1 que cortan Internet solo a MiniChat.</summary>
    public bool InternetCutForMiniChat { get; init; }
}

// Diagnóstico de red: detecta los problemas más comunes y explica cómo resolverlos.
public sealed partial class ChatEngine
{
    private readonly ConcurrentDictionary<string, (string Name, int Theirs, int Ours, string Address)> _prefixMismatches = new();
    private DateTimeOffset _networkStartedAt = DateTimeOffset.MaxValue;

    /// <summary>Registra si un par anuncia una máscara distinta a la nuestra para la misma red.</summary>
    private void TrackPrefix(DiscoveredPeer peer)
    {
        var iface = Policy.InterfaceFor(peer.Endpoint.Address);
        if (iface is null || peer.PrefixLength is not int theirs || IPAddress.IsLoopback(iface.Address)) return;
        if (theirs != iface.PrefixLength)
            _prefixMismatches[peer.Fingerprint] = (peer.DisplayName, theirs, iface.PrefixLength, peer.Endpoint.Address.ToString());
        else
            _prefixMismatches.TryRemove(peer.Fingerprint, out _);
    }

    public OfflineStatus GetOfflineStatus()
    {
        var peers = _discovery?.Peers ?? Array.Empty<DiscoveredPeer>();
        var contacts = Db.Read(c => ContactsRepoFingerprints(c));
        return new OfflineStatus(
            Policy.Inventory.IsInternetAvailable(),
            Policy.AllowedInterfaces.Where(i => !IPAddress.IsLoopback(i.Address)).Select(i => $"{i.SubnetText} ({i.Name})").Distinct().ToList(),
            peers.Count,
            contacts.Count(IsOnline),
            Policy.BlockedOutsideLan)
        {
            InternetCutForMiniChat = Environment.ProcessPath is { } exe
                && FirewallInspector.Inspect(exe).RulesForProgram.Any(r => r.Enabled && !r.Allow && !r.Inbound && r.Name.StartsWith("MiniChat sin Internet", StringComparison.Ordinal)),
        };
    }

    private static List<string> ContactsRepoFingerprints(Microsoft.Data.Sqlite.SqliteConnection c) =>
        Persistence.Sql.Query(c, "SELECT fingerprint FROM contacts;", r => r.GetString(0));

    /// <summary>Comprueba la red y devuelve los problemas encontrados, de más a menos grave.</summary>
    public Task<IReadOnlyList<NetworkFinding>> DiagnoseNetworkAsync() => Task.Run<IReadOnlyList<NetworkFinding>>(() =>
    {
        Policy.Refresh();
        var f = new List<NetworkFinding>();
        var decisions = Policy.Decisions.Where(d => !IPAddress.IsLoopback(d.Interface.Address)).ToList();
        var allowed = decisions.Where(d => d.Allowed).Select(d => d.Interface).ToList();

        // 1) Redes e interfaces
        if (decisions.Count == 0)
            f.Add(new(FindingSeverity.Error, "Sin conexión de red local",
                "Este equipo no tiene ninguna dirección IPv4 válida en una red local.",
                "Conecte el cable de red o el Wi-Fi y compruebe que el router/switch está encendido."));
        else if (allowed.Count == 0)
            f.Add(new(FindingSeverity.Error, "Ninguna red autorizada",
                "MiniChat no usa ninguna de las redes de este equipo, así que no puede ver ni recibir a nadie.",
                "En Ajustes › Redes, autorice su red de trabajo (o márquela como Privada en Windows)."));

        foreach (var d in decisions)
        {
            var i = d.Interface;
            if (i.IsDuplicate)
                f.Add(new(FindingSeverity.Error, $"IP duplicada en {i.Name}",
                    $"Windows detectó que otro equipo usa también la IP {i.Address}. Ninguno de los dos se comunicará bien.",
                    "Asigne una IP fija distinta a cada equipo (o use DHCP)."));
            if (!i.HasGateway && !i.LooksVirtualOrVpn)
                f.Add(new(FindingSeverity.Warning, $"{i.Name} no tiene puerta de enlace",
                    i.IsDhcp
                        ? "El router no entregó puerta de enlace. Windows puede considerar la red «no identificada» y tratarla como Pública."
                        : $"La IP fija {i.Address} no tiene puerta de enlace configurada. Windows suele marcar la red como «no identificada» (Pública) y el firewall bloquea MiniChat.",
                    "En la configuración IP del adaptador, indique como puerta de enlace la IP del router. Si la red no tiene router, puede pedir a su administrador que las redes no identificadas se traten como Privadas (secpol.msc › Directivas del administrador de listas de redes)."));
            if (!d.Allowed && i.Category == NetworkCategory.Public && !i.LooksVirtualOrVpn)
                f.Add(new(FindingSeverity.Warning, $"La red de {i.Name} es Pública",
                    $"Windows clasifica {i.SubnetText} como red Pública. MiniChat no la usa por defecto y el firewall de Windows bloquea las conexiones entrantes en redes Públicas.",
                    "Si es su red de trabajo: Configuración › Red e Internet › propiedades de la red › Privada. Alternativa: autorizarla en Ajustes › Redes (solo podrá enviar, no recibir, mientras Windows la considere Pública)."));
            else if (d.Allowed && i.Category == NetworkCategory.Public)
                f.Add(new(FindingSeverity.Warning, $"Red Pública autorizada manualmente ({i.Name})",
                    "MiniChat la usa porque usted la autorizó, pero el firewall de Windows aplica reglas de red Pública: otros equipos pueden no poder enviarle mensajes.",
                    "Marque la red como Privada en Windows si es una red de confianza."));
        }

        foreach (var adapter in Policy.Inventory.GetUnconfiguredAdapters())
            f.Add(new(FindingSeverity.Info, $"{adapter} no tiene dirección válida",
                "El adaptador está activo pero solo tiene una IP 169.254.x.x: no recibió dirección del router (DHCP).",
                "Si es el adaptador que usa para trabajar: compruebe el cable/Wi-Fi, reinicie el router o configure una IP fija."));

        // 2) Puertos de MiniChat
        if (_server?.LastBindError is { } bindError)
            f.Add(new(FindingSeverity.Error, "No se pudo abrir el puerto de conversaciones", bindError,
                "Cierre otras instancias de MiniChat con el mismo puerto, o reinicie el equipo."));
        if (_discovery?.LastError is { } discError)
            f.Add(new(FindingSeverity.Warning, "No se pudo abrir el descubrimiento", discError,
                "Otros equipos no le verán automáticamente; use «Conectar por IP»."));

        // 3) Firewall de Windows
        var exe = Environment.ProcessPath;
        FirewallState? fwState = null;
        if (exe is not null && Path.GetFileNameWithoutExtension(exe).Equals("MiniChat", StringComparison.OrdinalIgnoreCase))
            AddFirewallFindings(f, fwState = FirewallInspector.Inspect(exe));

        // 3b) Otras subredes de la oficina
        AddRemoteSubnetFindings(f, fwState);

        // 4) Compañeros y máscaras
        foreach (var (_, m) in _prefixMismatches)
            f.Add(new(FindingSeverity.Warning, $"Máscara de red distinta con {m.Name}",
                $"{m.Name} ({m.Address}) usa /{m.Theirs} y este equipo /{m.Ours}. Con máscaras distintas la comunicación puede funcionar solo en un sentido.",
                "Configure la misma máscara de subred en todos los equipos (por ejemplo 255.255.255.0)."));
        if (allowed.Count > 0 && _discovery is not null && _discovery.Peers.Count == 0 && Policy.RemoteSubnets.Count == 0 && DateTimeOffset.UtcNow - _networkStartedAt > TimeSpan.FromSeconds(30))
            f.Add(new(FindingSeverity.Info, "No se ve a ningún compañero",
                "Nadie con MiniChat abierto se ha anunciado en su red en los últimos segundos.",
                "Compruebe que su compañero tiene MiniChat abierto y está en la misma red. Si está en otra área de la oficina (otra subred), añádala en «Otras subredes de la oficina». Algunos Wi-Fi (invitados) aíslan los equipos o bloquean el multicast: pruebe «Conectar por IP» con la IP que aparece en sus Ajustes."));

        // 5) Internet (solo informativo)
        var internet = Policy.Inventory.IsInternetAvailable();
        f.Add(new(FindingSeverity.Info, internet switch { true => "Internet: disponible", false => "Internet: no disponible", _ => "Internet: estado desconocido" },
            "MiniChat no necesita Internet: solo se comunica con equipos de su red local. Este dato lo informa Windows."));

        var ordered = f.OrderByDescending(x => x.Severity).ToList();
        if (!f.Any(x => x.Severity >= FindingSeverity.Warning))
            ordered.Insert(0, new(FindingSeverity.Ok, "No se encontraron problemas",
                $"Redes en uso: {string.Join(", ", allowed.Select(a => a.SubnetText).Distinct())}."));
        return ordered;
    });

    private void AddRemoteSubnetFindings(List<NetworkFinding> f, FirewallState? fw)
    {
        foreach (var bad in _invalidAdminSubnets)
            f.Add(new(FindingSeverity.Warning, "Subred del administrador no válida",
                $"La directiva del equipo incluye «{bad}», que no es una subred privada válida de /16 a /30; se ignora.",
                "Corríjala con tools\\firewall-subredes.ps1 (como administrador)."));
        var subnets = Policy.RemoteSubnets;
        if (subnets.Count == 0) return;
        var peers = _discovery?.Peers ?? Array.Empty<DiscoveredPeer>();
        var status = _discovery?.SweepStatus;
        foreach (var r in subnets)
        {
            var route = Policy.InterfaceFor(Subnet.FromUInt(Subnet.ToUInt(r.Network) + 1));
            if (route is null)
                f.Add(new(FindingSeverity.Warning, $"Sin ruta hacia {r}",
                    $"Windows no llega a {r} a través de una red autorizada: los compañeros de esa subred no se verán.",
                    "Compruebe con «Probar una IP» o pregunte a TI si esa subred está unida a la suya."));
            int n = peers.Count(p => r.Contains(p.Endpoint.Address));
            if (route is not null && n == 0 && status is { Running: false, LastFinished: not null })
                f.Add(new(FindingSeverity.Info, $"Nadie encontrado en {r}",
                    "Se buscó en toda la subred y ningún equipo con MiniChat respondió.",
                    "Ellos deben tener MiniChat abierto, haber añadido la subred de usted y permitirla en su firewall. Use «Probar una IP» con la IP de uno de ellos."));
        }
        if (fw is not { Readable: true, EnabledOnCurrentProfile: true }) return;
        var allowTcp = fw.RulesForProgram.Where(x => x.Enabled && x.Inbound && x.Allow && (x.Profiles & fw.CurrentProfiles) != 0
            && FirewallInspector.Covers(x, FirewallInspector.ProtocolTcp, Options.TcpPort)).ToList();
        var allowUdp = fw.RulesForProgram.Where(x => x.Enabled && x.Inbound && x.Allow && (x.Profiles & fw.CurrentProfiles) != 0
            && FirewallInspector.Covers(x, FirewallInspector.ProtocolUdp, Options.DiscoveryPort)).ToList();
        var uncovered = subnets.Where(r => !allowTcp.Any(x => FirewallInspector.CoversRemote(x, r)) || !allowUdp.Any(x => FirewallInspector.CoversRemote(x, r))).ToList();
        if (uncovered.Count > 0 && allowTcp.Count > 0)
            f.Add(new(FindingSeverity.Warning, "El firewall solo admite su propia subred",
                $"Las reglas de MiniChat no permiten entradas desde {string.Join(", ", uncovered)}. Usted podrá escribirles, pero ellos no podrán escribirle ni encontrarle.",
                $"Como administrador, en este equipo: .\\tools\\firewall-subredes.ps1 -Subredes {string.Join(",", subnets)}"));
    }

    private void AddFirewallFindings(List<NetworkFinding> f, FirewallState fw)
    {
        if (!fw.Readable)
        {
            f.Add(new(FindingSeverity.Info, "No se pudo leer el firewall de Windows", fw.Error ?? ""));
            return;
        }
        if (!fw.EnabledOnCurrentProfile) return; // firewall desactivado en este perfil: no bloquea
        var profile = FirewallInspector.ProfileName(fw.CurrentProfiles);
        var current = fw.RulesForProgram.Where(r => r.Enabled && r.Inbound && (r.Profiles & fw.CurrentProfiles) != 0).ToList();
        var blocks = current.Where(r => !r.Allow && (FirewallInspector.Covers(r, FirewallInspector.ProtocolTcp, Options.TcpPort) || FirewallInspector.Covers(r, FirewallInspector.ProtocolUdp, Options.DiscoveryPort))).ToList();
        if (blocks.Count > 0)
        {
            f.Add(new(FindingSeverity.Error, "El firewall de Windows bloquea MiniChat",
                $"Hay {blocks.Count} regla(s) de bloqueo de entrada para MiniChat en el perfil {profile} (normalmente las crea Windows al cerrar su aviso «¿Permitir…?»). Las reglas de bloqueo prevalecen: otros equipos no pueden enviarle mensajes.",
                "Como administrador: «Firewall de Windows con seguridad avanzada» › Reglas de entrada › elimine las reglas «MiniChat» con acción Bloquear. O reinstale con el MSI."));
            return;
        }
        bool tcp = current.Any(r => r.Allow && FirewallInspector.Covers(r, FirewallInspector.ProtocolTcp, Options.TcpPort));
        bool udp = current.Any(r => r.Allow && FirewallInspector.Covers(r, FirewallInspector.ProtocolUdp, Options.DiscoveryPort));
        if (!tcp)
            f.Add(new(FindingSeverity.Warning, "Sin permiso de entrada en el firewall",
                $"Ninguna regla permite conexiones entrantes a MiniChat (TCP {Options.TcpPort}) en el perfil {profile}. Podrá enviar mensajes pero no recibirlos.",
                fw.CurrentProfiles.HasFlag(FirewallProfiles.Public)
                    ? "La red actual es Pública. Márquela como Privada en Windows; el instalador ya incluye permisos para redes Privadas y de Dominio."
                    : "Instale MiniChat con el MSI (crea las reglas limitadas) o ejecute tools\\firewall-dev.ps1 como administrador."));
        else if (!udp)
            f.Add(new(FindingSeverity.Info, "Descubrimiento sin permiso en el firewall",
                $"No hay regla de entrada UDP {Options.DiscoveryPort}: quizá otros no le vean en la lista, aunque sí puedan conectarle por IP."));
        if (fw.RulesForProgram.Any(r => r.Enabled && r.Inbound && r.Allow && r.Profiles.HasFlag(FirewallProfiles.Public) && r.LocalPorts is "*" or ""))
            f.Add(new(FindingSeverity.Info, "Permiso amplio en redes Públicas",
                "Hay una regla que permite a MiniChat recibir en cualquier puerto en redes Públicas (creada al aceptar el aviso de Windows). MiniChat igualmente rechaza equipos fuera de su red autorizada.",
                "Si no la necesita, elimínela en «Firewall de Windows con seguridad avanzada»."));
    }

    /// <summary>Prueba una conexión completa con un contacto: TLS, identidad y tiempo de respuesta.</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(long contactId, CancellationToken ct = default)
    {
        var contact = await Task.Run(() => Db.Read(c => Persistence.ContactsRepo.Get(c, contactId)), ct).ConfigureAwait(false);
        if (contact is null) return new(false, "El contacto no existe.", null, null, null, null);
        var endpoints = EndpointsFor(contact.Fingerprint, contact);
        if (endpoints.Count == 0) return new(false, "No se conoce ninguna dirección de este contacto y no se está anunciando en la red.", null, null, null, null);
        Exception? last = null;
        foreach (var ep in endpoints)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await using var client = await PeerClient.ConnectAsync(ep, contact.Fingerprint, Identity.Certificate, LocalHelloInfo(), Policy, Options, ct).ConfigureAwait(false);
                double handshake = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var reply = await client.RequestAsync(new PingPacket(), default, ct).ConfigureAwait(false);
                double rtt = sw.Elapsed.TotalMilliseconds;
                MarkDirectContact(contact.Fingerprint);
                var tls = client.TlsProtocol.ToString().Replace("Tls13", "TLS 1.3").Replace("Tls12", "TLS 1.2");
                bool pong = reply.Packet is PongPacket;
                return new(pong, pong
                    ? $"Conexión correcta con {ep.Address} por la red local: identidad verificada, cifrado {tls}, respuesta en {rtt:0.0} ms (conexión inicial {handshake:0} ms)."
                    : "El equipo respondió de forma inesperada.", handshake, rtt, ep.ToString(), tls);
            }
            catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IncompatibleVersionException or IdentityMismatchException)
            {
                last = ex;
            }
        }
        return new(false, Describe(last!, contact), null, null, null, null);
    }
}
