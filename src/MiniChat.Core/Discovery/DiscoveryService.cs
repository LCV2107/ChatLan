using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Identity;
using MiniChat.Core.Network;
using MiniChat.Core.Protocol;

namespace MiniChat.Core.Discovery;

/// <summary>
/// Contenido de un anuncio. NO contiene conversaciones ni claves privadas, y NO es prueba de identidad:
/// la huella anunciada solo sirve para saber a quién intentar conectar; TLS verifica después.
/// </summary>
public sealed record Announcement
{
    [JsonPropertyName("p")] public string Protocol { get; init; } = ProtocolInfo.Name;
    [JsonPropertyName("k")] public string Kind { get; init; } = "ann";
    [JsonPropertyName("min")] public int MinVersion { get; init; } = ProtocolInfo.MinVersion;
    [JsonPropertyName("max")] public int MaxVersion { get; init; } = ProtocolInfo.MaxVersion;
    [JsonPropertyName("iid")] public string InstanceId { get; init; } = "";
    [JsonPropertyName("fp")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("dn")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("pc")] public string MachineName { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; }
    /// <summary>Longitud del prefijo de red de la interfaz que envía (opcional; detecta máscaras distintas).</summary>
    [JsonPropertyName("pl")] public int? PrefixLength { get; init; }
    /// <summary>1 = sondeo unicast desde otra subred: se pide respuesta directa (el multicast no cruza routers).</summary>
    [JsonPropertyName("u")] public int? WantsReply { get; init; }
}

/// <summary>Estado de la búsqueda de compañeros en otras subredes de la oficina.</summary>
public sealed record RemoteSweepStatus(bool Running, long Sent, long Total, DateTimeOffset? LastFinished);

[JsonSerializable(typeof(Announcement))]
internal sealed partial class DiscoveryJson : JsonSerializerContext;

public sealed record DiscoveredPeer(
    string Fingerprint,
    string DisplayName,
    string MachineName,
    IPEndPoint Endpoint,
    string InstanceId,
    DateTimeOffset LastSeen,
    bool CompatibleVersion)
{
    /// <summary>Prefijo de red que anunció el par (null en versiones que no lo envían).</summary>
    public int? PrefixLength { get; init; }
    /// <summary>Encontrado en otra subred de la oficina (por sondeo unicast, no por multicast).</summary>
    public bool FromOtherSubnet { get; init; }
}

/// <summary>
/// Descubrimiento de instancias de MiniChat por UDP multicast (239.255.77.77:47820 por defecto, TTL 1) en la
/// subred propia. Para otras subredes de la oficina (que el multicast no alcanza), y SOLO si se autorizaron
/// explícitamente, envía un sondeo unicast pausado a cada dirección de esas subredes; responden únicamente los
/// equipos con MiniChat. El sondeo no lleva nombres: solo pide a quien lo reciba que se anuncie.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private sealed class Endpoint
    {
        public required IPEndPoint Address;
        public required DateTimeOffset LastSeen;
        public required Announcement Info;
        /// <summary>Llegó por unicast desde otra subred: hay que mantenerle informado por unicast.</summary>
        public bool Remote;
        public IPAddress? Local;
    }

    private sealed class Entry
    {
        public readonly Dictionary<string, Endpoint> Endpoints = new();
    }

    private sealed class InterfaceSocket
    {
        public required LocalInterface Interface;
        public required Socket Socket;
        public required CancellationTokenSource Cts;
    }

    private readonly MiniChatOptions _options;
    private readonly NetworkPolicy _policy;
    private readonly Func<Announcement> _self;
    private readonly ILog _log;
    private readonly ConcurrentDictionary<string, Entry> _peers = new();
    private readonly Dictionary<IPAddress, InterfaceSocket> _sockets = new();
    private readonly object _socketGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<IPAddress, (long Second, int Count)> _perSource = new();
    private long _globalSecond;
    private int _globalCount;
    private DateTimeOffset _lastQueryReply = DateTimeOffset.MinValue;
    private Task? _announceLoop;
    private Task? _sweepLoop;
    private readonly SemaphoreSlim _announceNow = new(0, 1);
    private readonly SemaphoreSlim _sweepNow = new(0, 1);
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _lastProbeReply = new();
    private Task? _remoteSweepLoop;
    private int _sweepGeneration;
    private volatile bool _remoteSweepEnabled = true;
    private RemoteSweepStatus _sweepStatus = new(false, 0, 0, null);

    public DiscoveryService(MiniChatOptions options, NetworkPolicy policy, Func<Announcement> self, ILog? log = null)
    {
        _options = options;
        _policy = policy;
        _self = self;
        _log = log ?? NullLog.Instance;
    }

    public event EventHandler<DiscoveredPeer>? PeerSeen;
    public event EventHandler<string>? PeerLost;

    /// <summary>Errores al abrir el puerto UDP (para mostrarlos en Ajustes).</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        SyncSockets();
        _announceLoop = AnnounceLoopAsync(_shutdown.Token);
        _sweepLoop = SweepLoopAsync(_shutdown.Token);
        _remoteSweepLoop = RemoteSweepLoopAsync(_shutdown.Token);
        Send("qry");
    }

    /// <summary>Direcciones a sondear primero en otras subredes (p. ej. la última IP conocida de cada contacto).</summary>
    public Func<IEnumerable<IPAddress>>? PriorityTargets { get; set; }

    /// <summary>Buscar automáticamente en las subredes autorizadas (si no, solo se mantiene a los ya encontrados).</summary>
    public bool RemoteSweepEnabled
    {
        get => _remoteSweepEnabled;
        set { _remoteSweepEnabled = value; RestartRemoteSweep(); }
    }

    public RemoteSweepStatus SweepStatus => Volatile.Read(ref _sweepStatus);

    /// <summary>Empieza de nuevo la búsqueda en otras subredes (p. ej. tras cambiar la lista).</summary>
    public void RestartRemoteSweep()
    {
        Interlocked.Increment(ref _sweepGeneration);
        try { _sweepNow.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Equipos disponibles ahora (anuncio reciente), uno por huella.</summary>
    public IReadOnlyList<DiscoveredPeer> Peers
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var list = new List<DiscoveredPeer>();
            foreach (var (fp, entry) in _peers)
            {
                lock (entry)
                {
                    var best = entry.Endpoints.Values.Where(e => now - e.LastSeen < _options.PresenceTimeout).MaxBy(e => e.LastSeen);
                    if (best is not null) list.Add(ToPeer(fp, best));
                }
            }
            return list;
        }
    }

    /// <summary>Direcciones candidatas (más reciente primero) para una huella. Son pistas sin verificar.</summary>
    public IReadOnlyList<IPEndPoint> EndpointsFor(string fingerprint)
    {
        if (!_peers.TryGetValue(fingerprint, out var entry)) return Array.Empty<IPEndPoint>();
        var now = DateTimeOffset.UtcNow;
        lock (entry)
            return entry.Endpoints.Values.Where(e => now - e.LastSeen < _options.PresenceTimeout)
                .OrderByDescending(e => e.LastSeen).Select(e => e.Address).ToList();
    }

    public bool IsAvailable(string fingerprint) => EndpointsFor(fingerprint).Count > 0;

    /// <summary>Reanuncia de inmediato (p. ej. tras cambiar el nombre visible).</summary>
    public void AnnounceSoon()
    {
        try { _announceNow.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Vuelve a abrir sockets según las interfaces autorizadas actuales.</summary>
    public void SyncSockets()
    {
        var wanted = _policy.AllowedInterfaces.GroupBy(i => i.Address).Select(g => g.First()).ToDictionary(i => i.Address);
        lock (_socketGate)
        {
            foreach (var addr in _sockets.Keys.Where(a => !wanted.ContainsKey(a)).ToList())
            {
                _sockets[addr].Cts.Cancel();
                _sockets[addr].Socket.Dispose();
                _sockets.Remove(addr);
            }
            string? error = null;
            foreach (var (addr, iface) in wanted)
            {
                if (_sockets.ContainsKey(addr)) continue;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.ExclusiveAddressUse = false;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(addr, _options.DiscoveryPort));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_options.DiscoveryGroup, addr));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                }
                catch (SocketException ex)
                {
                    error = $"No se pudo abrir el descubrimiento en {addr}:{_options.DiscoveryPort} ({ex.SocketErrorCode}).";
                    _log.Warn("discovery", error);
                    socket.Dispose();
                    continue;
                }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var s = new InterfaceSocket { Interface = iface, Socket = socket, Cts = cts };
                _sockets[addr] = s;
                _log.Info("discovery", $"Descubrimiento activo en {iface.Name} {iface.SubnetText}");
                _ = ReceiveLoopAsync(s, cts.Token);
            }
            LastError = error;
        }
    }

    private async Task ReceiveLoopAsync(InterfaceSocket s, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(2048, _options.MaxDatagramBytes + 1)];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await s.Socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException ex)
            {
                _log.Warn("discovery", "Error de recepción UDP", ex);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            try
            {
                Process(s.Interface, (IPEndPoint)result.RemoteEndPoint, buffer.AsSpan(0, result.ReceivedBytes));
            }
            catch (Exception ex)
            {
                // Un datagrama malicioso o roto nunca debe detener el descubrimiento.
                _log.Debug("discovery", $"Datagrama descartado: {ex.GetType().Name}");
            }
        }
    }

    internal void Process(LocalInterface iface, IPEndPoint remote, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > _options.MaxDatagramBytes) return;
        if (!AllowRate(remote.Address)) return;
        bool loopbackOk = IPAddress.IsLoopback(iface.Address) && IPAddress.IsLoopback(remote.Address);
        bool otherSubnet = false;
        if (!loopbackOk && !iface.Contains(remote.Address))
        {
            // Fuera de la subred de la interfaz: solo si es una subred de otra área autorizada explícitamente.
            if (!_policy.IsInRemoteSubnet(remote.Address) || _policy.IsOnAttachedSubnet(remote.Address)) return;
            otherSubnet = true;
        }
        if (!_policy.AllowedInterfaces.Any(i => i.Address.Equals(iface.Address))) return;

        Announcement? a;
        try { a = JsonSerializer.Deserialize(data, DiscoveryJson.Default.Announcement); }
        catch (JsonException) { return; }
        if (a is null || a.Protocol != ProtocolInfo.Name) return;
        if (!PacketValidator.IsName(a.InstanceId, 64) || a.InstanceId == _self().InstanceId) return;

        if (a.Kind == "qry" && a.WantsReply == 1)
        {
            // Sondeo unicast (otra subred): se responde directamente a quien preguntó, con límite por origen.
            ReplyToProbe(iface, remote);
            return;
        }
        if (a.Kind == "qry")
        {
            if (otherSubnet) return;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastQueryReply >= _options.QueryReplyMinInterval)
            {
                _lastQueryReply = now;
                Send("ann");
            }
            return;
        }
        if (!IdentityCodes.IsValidFingerprint(a.Fingerprint)) return;
        if (a.Port is <= 0 or > 65535) return;
        if (!PacketValidator.IsName(a.DisplayName, _options.MaxNameChars) || !PacketValidator.IsName(a.MachineName, _options.MaxNameChars)) return;

        if (a.Kind == "bye")
        {
            if (_peers.TryGetValue(a.Fingerprint, out var e))
            {
                bool empty;
                lock (e)
                {
                    e.Endpoints.Remove($"{remote.Address}:{a.Port}");
                    empty = e.Endpoints.Count == 0;
                }
                if (empty && _peers.TryRemove(a.Fingerprint, out _)) PeerLost?.Invoke(this, a.Fingerprint);
            }
            return;
        }
        if (a.Kind != "ann") return;
        if (!_peers.ContainsKey(a.Fingerprint) && _peers.Count >= _options.MaxDiscoveredPeers) return;

        var entry = _peers.GetOrAdd(a.Fingerprint, _ => new Entry());
        Endpoint ep;
        bool isNew = false;
        lock (entry)
        {
            var key = $"{remote.Address}:{a.Port}";
            if (!entry.Endpoints.TryGetValue(key, out ep!))
            {
                if (entry.Endpoints.Count >= 4)
                    entry.Endpoints.Remove(entry.Endpoints.MinBy(kv => kv.Value.LastSeen).Key);
                ep = new Endpoint { Address = new IPEndPoint(remote.Address, a.Port), LastSeen = DateTimeOffset.UtcNow, Info = a };
                entry.Endpoints[key] = ep;
                isNew = true;
            }
            ep.LastSeen = DateTimeOffset.UtcNow;
            ep.Info = a;
            ep.Remote = otherSubnet;
            ep.Local = iface.Address;
        }
        // Recién encontrado en otra subred: se le anuncia en seguida para que también nos vea.
        if (isNew && otherSubnet) SendUnicast(iface.Address, remote.Address, _self() with { Kind = "ann" });
        PeerSeen?.Invoke(this, ToPeer(a.Fingerprint, ep));
    }

    private bool AllowRate(IPAddress source)
    {
        long second = Environment.TickCount64 / 1000;
        lock (_perSource)
        {
            if (second != _globalSecond) { _globalSecond = second; _globalCount = 0; }
            if (++_globalCount > _options.MaxDatagramsPerSecond) return false;
        }
        var state = _perSource.AddOrUpdate(source, (second, 1), (_, s) => s.Second == second ? (second, s.Count + 1) : (second, 1));
        if (_perSource.Count > 1024) _perSource.Clear();
        return state.Count <= _options.MaxDatagramsPerSourcePerSecond;
    }

    private DiscoveredPeer ToPeer(string fp, Endpoint e) => new(
        fp, e.Info.DisplayName.Trim(), e.Info.MachineName.Trim(), e.Address, e.Info.InstanceId, e.LastSeen,
        ProtocolInfo.Negotiate(e.Info.MinVersion, e.Info.MaxVersion) is not null)
    {
        PrefixLength = e.Info.PrefixLength is int pl && pl is >= 0 and <= 32 ? pl : null,
        FromOtherSubnet = e.Remote,
    };

    private void ReplyToProbe(LocalInterface iface, IPEndPoint remote)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastProbeReply.TryGetValue(remote.Address, out var last) && now - last < _options.ProbeReplyMinInterval) return;
        if (_lastProbeReply.Count > 1024) _lastProbeReply.Clear();
        _lastProbeReply[remote.Address] = now;
        SendUnicast(iface.Address, remote, _self() with { Kind = "ann" });
    }

    private void SendUnicast(IPAddress local, IPAddress remote, Announcement a) =>
        SendUnicast(local, new IPEndPoint(remote, _options.DiscoveryPort), a);

    /// <summary>Envía un datagrama unicast por el socket de la interfaz indicada.</summary>
    private void SendUnicast(IPAddress local, IPEndPoint target, Announcement a)
    {
        InterfaceSocket? s;
        lock (_socketGate) _sockets.TryGetValue(local, out s);
        if (s is null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(a with { PrefixLength = s.Interface.PrefixLength }, DiscoveryJson.Default.Announcement);
        if (bytes.Length > _options.MaxDatagramBytes) return;
        try { s.Socket.SendTo(bytes, target); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _log.Debug("discovery", $"No se pudo enviar a {target}: {ex.GetType().Name}");
        }
    }

    private void Send(string kind)
    {
        var a = _self() with { Kind = kind };
        var target = new IPEndPoint(_options.DiscoveryGroup, _options.DiscoveryPort);
        List<InterfaceSocket> sockets;
        lock (_socketGate) sockets = _sockets.Values.ToList();
        foreach (var s in sockets)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(a with { PrefixLength = s.Interface.PrefixLength }, DiscoveryJson.Default.Announcement);
            if (bytes.Length > _options.MaxDatagramBytes) { _log.Warn("discovery", "Anuncio demasiado grande"); return; }
            try { s.Socket.SendTo(bytes, target); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _log.Debug("discovery", $"No se pudo enviar el anuncio por {s.Interface.Address}: {ex.GetType().Name}");
            }
        }
        // Los compañeros de otras subredes no reciben el multicast: se les mantiene informados por unicast.
        if (kind is "ann" or "bye")
            foreach (var (local, address) in RemoteEndpoints())
                SendUnicast(local, address, a);
    }

    private List<(IPAddress Local, IPAddress Address)> RemoteEndpoints()
    {
        var list = new List<(IPAddress, IPAddress)>();
        var now = DateTimeOffset.UtcNow;
        foreach (var (_, entry) in _peers)
            lock (entry)
                foreach (var e in entry.Endpoints.Values)
                    if (e.Remote && e.Local is not null && now - e.LastSeen < _options.PresenceTimeout)
                        list.Add((e.Local, e.Address.Address));
        return list.Distinct().ToList();
    }

    // ---------------- Búsqueda en otras subredes ----------------

    /// <summary>
    /// Direcciones a sondear: primero las prioritarias, luego cada equipo de las subredes autorizadas. Se omiten
    /// las propias, las de subredes conectadas directamente (ahí basta el multicast) y las repetidas.
    /// </summary>
    internal static IEnumerable<IPAddress> SweepTargets(IEnumerable<IPAddress> priority, NetworkPolicy policy)
    {
        var subnets = policy.RemoteSubnets;
        var own = policy.Decisions.Select(d => d.Interface.Address).ToHashSet();
        var seen = new HashSet<IPAddress>();
        bool Wanted(IPAddress ip) => !own.Contains(ip) && !policy.IsOnAttachedSubnet(ip) && seen.Add(ip);
        foreach (var ip in priority)
            if (subnets.Any(r => r.Contains(ip)) && Wanted(ip)) yield return ip;
        foreach (var subnet in subnets)
            foreach (var ip in subnet.Hosts())
                if (Wanted(ip)) yield return ip;
    }

    private async Task RemoteSweepLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                int generation = Volatile.Read(ref _sweepGeneration);
                if (_remoteSweepEnabled && _policy.RemoteSubnets.Count > 0)
                    await SweepOnceAsync(generation, ct).ConfigureAwait(false);
                await _sweepNow.WaitAsync(_options.RemoteSweepInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warn("discovery", "La búsqueda en otras subredes se detuvo", ex);
        }
    }

    private async Task SweepOnceAsync(int generation, CancellationToken ct)
    {
        List<IPAddress> priority;
        try { priority = PriorityTargets?.Invoke().ToList() ?? new List<IPAddress>(); }
        catch (Exception) { priority = new List<IPAddress>(); }
        long total = _policy.RemoteSubnets.Sum(r => r.HostCount);
        long sent = 0;
        var lastFinished = SweepStatus.LastFinished;
        Volatile.Write(ref _sweepStatus, new RemoteSweepStatus(true, 0, total, lastFinished));
        _log.Info("discovery", $"Buscando compañeros en otras subredes: {string.Join(", ", _policy.RemoteSubnets.Select(r => r.Text))}");

        var probe = new Announcement { Kind = "qry", InstanceId = _self().InstanceId, WantsReply = 1 };
        int perTick = Math.Max(1, _options.RemoteProbesPerSecond / 10);
        var known = RemoteEndpoints().Select(e => e.Address).ToHashSet();
        int inTick = 0;
        bool completed = true;
        foreach (var ip in SweepTargets(priority, _policy))
        {
            if (ct.IsCancellationRequested || generation != Volatile.Read(ref _sweepGeneration) || !_remoteSweepEnabled) { completed = false; break; }
            sent++;
            if (known.Contains(ip)) continue;             // ya se le mantiene informado por unicast
            var iface = _policy.InterfaceFor(ip);
            if (iface is null) continue;                  // sin ruta por una red autorizada
            SendUnicast(iface.Address, ip, probe);
            if (++inTick >= perTick)
            {
                inTick = 0;
                Volatile.Write(ref _sweepStatus, new RemoteSweepStatus(true, sent, total, lastFinished));
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        Volatile.Write(ref _sweepStatus, new RemoteSweepStatus(false, sent, total, completed ? DateTimeOffset.UtcNow : lastFinished));
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        Send("ann");
        while (!ct.IsCancellationRequested)
        {
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * _options.AnnounceJitter.TotalMilliseconds);
            try { await _announceNow.WaitAsync(_options.AnnounceInterval + jitter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Send("ann");
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PresenceSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var (fp, entry) in _peers)
                {
                    bool empty;
                    lock (entry)
                    {
                        foreach (var key in entry.Endpoints.Where(kv => now - kv.Value.LastSeen >= _options.PresenceTimeout).Select(kv => kv.Key).ToList())
                            entry.Endpoints.Remove(key);
                        empty = entry.Endpoints.Count == 0;
                    }
                    if (empty && _peers.TryRemove(fp, out _)) PeerLost?.Invoke(this, fp);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { Send("bye"); } catch { }
        _shutdown.Cancel();
        lock (_socketGate)
        {
            foreach (var s in _sockets.Values) s.Socket.Dispose();
            _sockets.Clear();
        }
        try { if (_announceLoop is not null) await _announceLoop.ConfigureAwait(false); } catch { }
        try { if (_sweepLoop is not null) await _sweepLoop.ConfigureAwait(false); } catch { }
        try { if (_remoteSweepLoop is not null) await _remoteSweepLoop.ConfigureAwait(false); } catch { }
    }
}
