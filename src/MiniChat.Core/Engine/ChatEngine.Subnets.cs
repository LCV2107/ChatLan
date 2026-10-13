using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Discovery;
using MiniChat.Core.Network;
using MiniChat.Core.Persistence;

namespace MiniChat.Core.Engine;

/// <summary>Una subred de otra área autorizada, con lo que se sabe de ella.</summary>
public sealed record RemoteSubnetView(string Subnet, bool FromAdministrator, int PeersOnline, string? Route);

/// <summary>Subred encontrada al explorar la oficina.</summary>
public sealed record SubnetCandidate(string Subnet, string? Gateway, long? RoundTripMs, bool IsOwn, bool IsAuthorized, int PeersOnline);

public sealed record OfficeScanResult(IReadOnlyList<SubnetCandidate> Subnets, string Summary);

/// <summary>Resultado de comprobar si se puede llegar a una IP concreta (ping, ruta y puerto de MiniChat).</summary>
public sealed record AddressProbeResult(IReadOnlyList<NetworkFinding> Steps, NetworkFinding Verdict, string? SuggestedSubnet);

// Otras subredes de la oficina: lista autorizada, exploración de la oficina y prueba de una IP.
public sealed partial class ChatEngine
{
    /// <summary>Subredes del administrador (HKLM) que no son válidas, para avisar en el diagnóstico.</summary>
    private IReadOnlyList<string> _invalidAdminSubnets = Array.Empty<string>();

    private void ApplyRemoteSubnets()
    {
        var invalid = new List<string>();
        var list = new List<SubnetRange>();
        foreach (var text in Policy.Inventory.GetAdminRemoteSubnets())
        {
            if (SubnetRange.TryParse(text, out var r, out _, Options.AllowLoopback)) list.Add(r!);
            else invalid.Add(text);
        }
        foreach (var text in ReadList(SettingRemoteSubnets))
            if (SubnetRange.TryParse(text, out var r, out _, Options.AllowLoopback)) list.Add(r!);
        _invalidAdminSubnets = invalid;
        Policy.SetRemoteSubnets(list.Take(Options.MaxRemoteSubnets));
    }

    private HashSet<string> AdminSubnetTexts() =>
        Policy.Inventory.GetAdminRemoteSubnets().Select(t => SubnetRange.ParseOrNull(t, Options.AllowLoopback)?.Text).OfType<string>().ToHashSet();

    /// <summary>Subredes de otras áreas autorizadas, con compañeros visibles y la interfaz por la que se llega.</summary>
    public IReadOnlyList<RemoteSubnetView> GetRemoteSubnets()
    {
        var admin = AdminSubnetTexts();
        var peers = _discovery?.Peers ?? Array.Empty<DiscoveredPeer>();
        return Policy.RemoteSubnets.Select(r =>
        {
            var iface = Policy.InterfaceFor(Subnet.FromUInt(Subnet.ToUInt(r.Network) + 1));
            return new RemoteSubnetView(r.Text, admin.Contains(r.Text), peers.Count(p => r.Contains(p.Endpoint.Address)),
                iface is null ? null : $"{iface.Name} ({iface.Address})");
        }).ToList();
    }

    public async Task<OperationResult> AddRemoteSubnetAsync(string text)
    {
        if (!SubnetRange.TryParse(text, out var range, out var error, Options.AllowLoopback)) return OperationResult.Fail(error);
        var own = Policy.Decisions.Select(d => d.Interface).FirstOrDefault(i => !IPAddress.IsLoopback(i.Address)
            && new SubnetRange(i.NetworkAddress, i.PrefixLength).Overlaps(range!));
        if (own is not null)
            return OperationResult.Fail($"{range} incluye la red de este equipo ({own.SubnetText}, {own.Name}). Esa red ya se usa directamente; añada solo las subredes de otras áreas.");
        var existing = Policy.RemoteSubnets.FirstOrDefault(r => r.Overlaps(range!));
        if (existing is not null)
            return OperationResult.Fail(existing.Text == range!.Text ? $"{range} ya está en la lista." : $"{range} se solapa con {existing}, que ya está en la lista.");
        if (Policy.RemoteSubnets.Count >= Options.MaxRemoteSubnets)
            return OperationResult.Fail($"Se admiten como máximo {Options.MaxRemoteSubnets} subredes.");

        await Task.Run(() =>
        {
            var list = ReadList(SettingRemoteSubnets);
            list.Add(range!.Text);
            SetSetting(SettingRemoteSubnets, JsonSerializer.Serialize(list));
        }).ConfigureAwait(false);
        ApplyRemoteSubnets();
        _discovery?.RestartRemoteSweep();
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        _log.Info("net", $"Subred de otra área autorizada: {range}");

        var probe = Subnet.FromUInt(Subnet.ToUInt(range!.Network) + 1);
        var route = Policy.InterfaceFor(probe);
        var source = route is null ? Policy.Inventory.SourceAddressFor(probe) : null;
        if (route is null)
            return OperationResult.Ok(source is null
                ? $"{range} añadida, pero Windows no tiene ruta hacia ella: no se podrá contactar hasta que TI una esa red con la suya."
                : $"{range} añadida, pero Windows llega a ella por {source}, una red que MiniChat no usa (por ejemplo, marcada como Pública). Revise la lista de redes de arriba.");
        return OperationResult.Ok($"{range} añadida. Buscando compañeros (tarda unos {Math.Max(1, range.HostCount / Math.Max(1, Options.RemoteProbesPerSecond))} s). Ellos deben añadir también la subred de usted ({string.Join(", ", Policy.AllowedInterfaces.Where(i => !IPAddress.IsLoopback(i.Address)).Select(i => i.SubnetText).Distinct())}).");
    }

    public async Task<OperationResult> RemoveRemoteSubnetAsync(string text)
    {
        if (AdminSubnetTexts().Contains(text))
            return OperationResult.Fail("Esta subred la definió el administrador del equipo y no se puede quitar desde aquí.");
        await Task.Run(() =>
        {
            var list = ReadList(SettingRemoteSubnets);
            list.RemoveAll(t => SubnetRange.ParseOrNull(t, Options.AllowLoopback)?.Text == text || t == text);
            SetSetting(SettingRemoteSubnets, JsonSerializer.Serialize(list));
        }).ConfigureAwait(false);
        ApplyRemoteSubnets();
        _discovery?.RestartRemoteSweep();
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok($"{text} quitada. Los compañeros de esa subred dejarán de aparecer en unos segundos.");
    }

    public bool RemoteSweepEnabled
    {
        get => GetSetting(SettingRemoteSweep) != "0";
        set
        {
            SetSetting(SettingRemoteSweep, value ? "1" : "0");
            if (_discovery is not null) _discovery.RemoteSweepEnabled = value;
        }
    }

    public RemoteSweepStatus? RemoteSweepStatus => _discovery?.SweepStatus;

    /// <summary>Últimas IP conocidas de los contactos: se sondean primero para reencontrarlos en seguida.</summary>
    private IEnumerable<IPAddress> KnownContactAddresses() =>
        Db.Read(c => Sql.Query(c, "SELECT c.last_address FROM contacts c LEFT JOIN relationships r ON r.contact_id = c.id WHERE c.last_address IS NOT NULL AND COALESCE(r.blocked, 0) = 0;", r => r.GetString(0)))
            .Select(t => IPAddress.TryParse(t, out var ip) ? ip : null).OfType<IPAddress>().ToList();

    // ======================= Explorar la oficina =======================

    /// <summary>
    /// Busca subredes vecinas haciendo ping a la puerta de enlace habitual (primera y última dirección) de cada
    /// bloque del mismo tamaño que la red propia, dentro de su /16. Solo cuando el usuario lo pide: es tráfico
    /// visible para el área de TI. Las puertas de enlace que no responden a ping no aparecen.
    /// </summary>
    public async Task<OfficeScanResult> ScanOfficeSubnetsAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Policy.Refresh();
        var own = Policy.AllowedInterfaces.Where(i => !IPAddress.IsLoopback(i.Address) && SubnetRange.IsPrivate(i.Address)).ToList();
        if (own.Count == 0)
            return new(Array.Empty<SubnetCandidate>(), "No hay ninguna red local privada autorizada desde la que explorar. Revise Ajustes › Redes.");

        var peers = _discovery?.Peers ?? Array.Empty<DiscoveredPeer>();
        var authorized = Policy.RemoteSubnets;
        var blocks = new List<(SubnetRange Range, bool IsOwn)>();
        var notes = new List<string>();
        foreach (var iface in own)
        {
            if (iface.PrefixLength <= SubnetRange.MinPrefix)
            {
                notes.Add($"La red {iface.SubnetText} ya abarca todo su rango: no hay subredes vecinas que buscar.");
                continue;
            }
            int blockPrefix = Math.Clamp(iface.PrefixLength, 20, 24);
            uint parent = Subnet.ToUInt(Subnet.NetworkOf(iface.Address, SubnetRange.MinPrefix));
            uint size = 1u << (32 - blockPrefix);
            for (uint v = parent; v < parent + 65536u; v += size)
            {
                var range = new SubnetRange(Subnet.FromUInt(v), blockPrefix);
                if (blocks.Any(b => b.Range.Overlaps(range))) continue;
                blocks.Add((range, own.Any(i => range.Contains(i.Address))));
            }
        }

        var found = new System.Collections.Concurrent.ConcurrentBag<SubnetCandidate>();
        int done = 0;
        using var gate = new SemaphoreSlim(32);
        var tasks = blocks.Select(async b =>
        {
            int Peers() => peers.Count(p => b.Range.Contains(p.Endpoint.Address));
            bool isAuthorized = authorized.Any(r => r.Overlaps(b.Range));
            if (b.IsOwn)
            {
                found.Add(new SubnetCandidate(b.Range.Text, null, null, true, true, Peers()));
                progress?.Report(Interlocked.Increment(ref done) * 100 / blocks.Count);
                return;
            }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                uint n = Subnet.ToUInt(b.Range.Network);
                foreach (var gw in new[] { Subnet.FromUInt(n + 1), Subnet.FromUInt(n + (1u << (32 - b.Range.PrefixLength)) - 2) })
                {
                    var rtt = await PingAsync(gw, 600, ct).ConfigureAwait(false);
                    if (rtt is not null)
                    {
                        found.Add(new SubnetCandidate(b.Range.Text, gw.ToString(), rtt, false, isAuthorized, Peers()));
                        return;
                    }
                }
                if (isAuthorized || Peers() > 0)
                    found.Add(new SubnetCandidate(b.Range.Text, null, null, false, isAuthorized, Peers()));
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done) * 100 / Math.Max(1, blocks.Count));
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var list = found.OrderBy(c => Subnet.ToUInt(IPAddress.Parse(c.Subnet.Split('/')[0]))).ToList();
        int others = list.Count(c => !c.IsOwn && c.Gateway is not null);
        var summary = others == 0
            ? "No respondió ninguna otra subred. Puede que no existan, que estén en otro rango o que sus routers no respondan a ping: pregunte a TI qué subredes hay y añádalas a mano."
            : $"Se encontraron {others} subred(es) además de la suya. Que respondan no garantiza que estén unidas a la suya para MiniChat: use «Probar una IP» con la IP de un compañero.";
        if (notes.Count > 0) summary = string.Join(" ", notes) + " " + summary;
        _log.Info("net", $"Exploración de la oficina: {blocks.Count} bloques, {others} subredes con respuesta");
        return new(list, summary);
    }

    private static async Task<long?> PingAsync(IPAddress ip, int timeoutMs, CancellationToken ct, int ttl = 64)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], new PingOptions(ttl, true), ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException)
        {
            return null;
        }
    }

    // ======================= Probar una IP =======================

    /// <summary>
    /// Comprueba paso a paso si se puede llegar a una IP de la red: a qué red pertenece, por dónde sale,
    /// si responde a ping, por qué routers pasa y si el puerto de MiniChat está abierto. Solo IP privadas.
    /// </summary>
    public async Task<AddressProbeResult> ProbeAddressAsync(string ipText, CancellationToken ct = default)
    {
        var steps = new List<NetworkFinding>();
        if (!IPAddress.TryParse((ipText ?? "").Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || (ipText ?? "").Trim().Count(ch => ch == '.') != 3)
            return new(steps, new(FindingSeverity.Error, "Dirección no válida", "Escriba una IPv4 completa, por ejemplo 10.10.161.25."), null);
        if (!SubnetRange.IsPrivate(ip) && !(Options.AllowLoopback && IPAddress.IsLoopback(ip)))
            return new(steps, new(FindingSeverity.Error, "No es una dirección de red local",
                $"{ip} no es una IP privada (10.x, 172.16–31.x, 192.168.x). MiniChat no se comunica a través de Internet."), null);

        Policy.Refresh();
        if (Policy.Decisions.Any(d => d.Interface.Address.Equals(ip)))
            return new(steps, new(FindingSeverity.Info, "Es este equipo", $"{ip} es una dirección de este mismo equipo."), null);

        // 1) ¿A qué red pertenece?
        string? suggested = null;
        var attached = Policy.Decisions.FirstOrDefault(d => !IPAddress.IsLoopback(d.Interface.Address) && d.Interface.Contains(ip));
        bool sameSubnet = attached is not null;
        if (attached is not null)
            steps.Add(attached.Allowed
                ? new(FindingSeverity.Ok, "Misma red que este equipo", $"{ip} está en {attached.Interface.SubnetText} ({attached.Interface.Name}). Se descubren solos, sin configurar nada.")
                : new(FindingSeverity.Warning, "Misma red, pero no autorizada", $"{ip} está en {attached.Interface.SubnetText} ({attached.Interface.Name}), que MiniChat no usa: {attached.Reason}."));
        else if (Policy.RemoteSubnets.FirstOrDefault(r => r.Contains(ip)) is { } remote)
            steps.Add(new(FindingSeverity.Ok, "Subred de otra área autorizada", $"{ip} está en {remote}, que está en su lista de subredes."));
        else
        {
            int prefix = Math.Clamp(Policy.AllowedInterfaces.FirstOrDefault(i => !IPAddress.IsLoopback(i.Address))?.PrefixLength ?? 24, 20, 24);
            suggested = $"{Subnet.NetworkOf(ip, prefix)}/{prefix}";
            steps.Add(new(FindingSeverity.Warning, "Otra subred, todavía no autorizada",
                $"{ip} no está en la red de este equipo ni en su lista. Probablemente pertenece a {suggested} (supuesto: misma máscara que la suya; confírmelo con TI)."));
        }

        // 2) ¿Por dónde sale?
        var source = Policy.Inventory.SourceAddressFor(ip);
        var sourceIface = source is null ? null : Policy.Decisions.FirstOrDefault(d => d.Interface.Address.Equals(source));
        if (source is null)
            steps.Add(new(FindingSeverity.Error, "Sin ruta", $"Windows no tiene ninguna ruta hacia {ip}. Este equipo no puede llegar a esa red."));
        else if (sourceIface is null || !sourceIface.Allowed)
            steps.Add(new(FindingSeverity.Warning, "Sale por una red no autorizada",
                $"Para llegar a {ip} Windows usa {source}{(sourceIface is null ? "" : $" ({sourceIface.Interface.Name})")}, que MiniChat no usa."));
        else
            steps.Add(new(FindingSeverity.Ok, "Ruta",
                sameSubnet ? $"Directa por {sourceIface.Interface.Name} ({source})." : $"Por {sourceIface.Interface.Name} ({source}) a través del router de la red."));

        // 3) Ping
        var rtt = await PingAsync(ip, 1000, ct).ConfigureAwait(false) ?? await PingAsync(ip, 1000, ct).ConfigureAwait(false);
        steps.Add(rtt is not null
            ? new(FindingSeverity.Ok, "Responde a ping", $"{ip} respondió en {rtt} ms.")
            : new(FindingSeverity.Info, "No responde a ping", "Puede estar apagado, o su firewall o el de la red bloquean el ping (es habitual). Se sigue comprobando."));

        // 4) Recorrido (solo si está en otra subred)
        bool reachedTarget = rtt is not null;
        string? lastHop = null;
        if (!sameSubnet && source is not null)
        {
            var hops = new List<string>();
            int misses = 0;
            for (int ttl = 1; ttl <= 8 && misses < 3; ttl++)
            {
                var (hop, ms, reached) = await TraceHopAsync(ip, ttl, ct).ConfigureAwait(false);
                if (hop is null) { hops.Add($"{ttl}: *"); misses++; continue; }
                misses = 0;
                lastHop = hop.ToString();
                hops.Add($"{ttl}: {hop} ({ms} ms)");
                if (reached) { reachedTarget = true; break; }
            }
            steps.Add(new(reachedTarget ? FindingSeverity.Ok : FindingSeverity.Info, "Recorrido entre redes", string.Join(" · ", hops)
                + (reachedTarget ? "" : lastHop is null ? " — ningún router respondió." : $" — no se pasa de {lastHop}.")));
        }

        // 5) Puerto de MiniChat
        var (state, tcpMs) = await TestTcpAsync(ip, Options.TcpPort, ct).ConfigureAwait(false);
        steps.Add(state switch
        {
            TcpState.Open => new(FindingSeverity.Ok, $"Puerto {Options.TcpPort} abierto", $"MiniChat (u otro programa) acepta conexiones en ese equipo ({tcpMs} ms)."),
            TcpState.Refused => new(FindingSeverity.Warning, $"Puerto {Options.TcpPort} cerrado", "El equipo existe y responde, pero no hay MiniChat abierto en él."),
            _ => new(FindingSeverity.Warning, $"Puerto {Options.TcpPort} sin respuesta", "No contesta: equipo apagado, MiniChat cerrado con el firewall en modo silencioso, o un firewall lo bloquea."),
        });

        // Veredicto
        bool authorized = sameSubnet ? attached!.Allowed : Policy.IsInRemoteSubnet(ip);
        NetworkFinding verdict;
        if (source is null)
            verdict = new(FindingSeverity.Error, "No se puede: las redes no están unidas",
                "Este equipo no tiene ruta hacia esa red. Hace falta que TI una las redes (router o cable entre ellas); MiniChat no puede hacerlo por sí solo.");
        else if (state == TcpState.Open)
            verdict = authorized
                ? new(FindingSeverity.Ok, "Se puede conectar", "Hay ruta y MiniChat responde. Si su compañero también autorizó la subred de usted, ya pueden verse y chatear.")
                : new(FindingSeverity.Ok, "Se puede conectar, falta autorizarla",
                    $"Hay ruta y MiniChat responde. Añada {suggested} en «Otras subredes de la oficina» (y su compañero debe añadir la de usted).");
        else if (state == TcpState.Refused)
            verdict = new(FindingSeverity.Warning, "Hay conexión, pero MiniChat no está abierto allí",
                "La red está unida y el equipo responde. Pida a su compañero que abra MiniChat y vuelva a probar.");
        else if (reachedTarget)
            verdict = new(FindingSeverity.Warning, "Hay ruta, pero algo bloquea MiniChat",
                $"El equipo responde, pero no el puerto {Options.TcpPort}. Causas habituales: MiniChat cerrado, la red de su compañero es Pública en Windows, su firewall solo admite su propia subred (ejecute allí tools\\firewall-subredes.ps1) o un firewall entre áreas (pida a TI que permita TCP {Options.TcpPort} y UDP {Options.DiscoveryPort} entre estas subredes).");
        else
            verdict = new(FindingSeverity.Warning, "No se obtuvo respuesta",
                (lastHop is null ? "Nadie respondió más allá de este equipo. " : $"El tráfico llega hasta {lastHop} y no pasa de ahí. ")
                + "O el equipo está apagado, o las redes no están unidas, o un firewall entre áreas bloquea el tráfico. Confírmelo con TI.");
        _log.Info("net", $"Prueba de IP {ip}: {verdict.Title}");
        return new(steps, verdict, authorized ? null : suggested);
    }

    private static async Task<(IPAddress? Hop, long Ms, bool Reached)> TraceHopAsync(IPAddress target, int ttl, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var sw = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(900), new byte[32], new PingOptions(ttl, true), ct).ConfigureAwait(false);
            long ms = sw.ElapsedMilliseconds;
            return reply.Status switch
            {
                IPStatus.Success => (reply.Address, reply.RoundtripTime, true),
                IPStatus.TtlExpired or IPStatus.TimeExceeded => (reply.Address, ms, false),
                _ => (null, 0, false),
            };
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException)
        {
            return (null, 0, false);
        }
    }

    private enum TcpState { Open, Refused, Timeout }

    private static async Task<(TcpState, long)> TestTcpAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return (TcpState.Open, sw.ElapsedMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (TcpState.Refused, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (TcpState.Timeout, sw.ElapsedMilliseconds);
        }
    }
}
