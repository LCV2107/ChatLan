using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Discovery;
using MiniChat.Core.Identity;
using MiniChat.Core.Model;
using MiniChat.Core.Network;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Security;
using MiniChat.Core.Stickers;
using MiniChat.Core.Transport;

namespace MiniChat.Core.Engine;

/// <summary>
/// Motor de MiniChat LAN, independiente de la interfaz: identidad, red, solicitudes, autorización,
/// mensajería, historial y stickers. Toda la comunicación y el almacenamiento se pueden probar sin WPF.
/// Los eventos se disparan en hilos del pool; la interfaz debe pasarlos a su hilo.
/// </summary>
public sealed partial class ChatEngine : IInboundHandler, IAsyncDisposable
{
    public const string SettingTrustedNetworks = "net.trusted";
    public const string SettingDeniedNetworks = "net.denied";
    public const string SettingRemoteSubnets = "net.remoteSubnets";
    public const string SettingRemoteSweep = "net.remoteSweep";

    private readonly ILog _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDirectContact = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _manualEndpoints = new();
    private readonly ConcurrentDictionary<long, Task> _pumps = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private TransportServer? _server;
    private DiscoveryService? _discovery;
    private ConnectionPool? _pool;
    private Timer? _maintenanceTimer;
    private Timer? _networkDebounce;
    private string _displayName;
    private bool _networkStarted;

    private ChatEngine(AppPaths paths, MiniChatOptions options, Database db, LocalIdentity identity, NetworkPolicy policy,
        ILog log, string machineName, string? displayName, bool identityRegenerated)
    {
        Paths = paths;
        Options = options;
        Db = db;
        Identity = identity;
        Policy = policy;
        _log = log;
        MachineName = machineName;
        _displayName = displayName ?? "";
        IdentityRegenerated = identityRegenerated;
        Vault = new Vault(db);
        Vault.StateChanged += OnVaultStateChanged;
        Stickers = new StickerService(db, paths, options, log, Vault);
    }

    public AppPaths Paths { get; }
    public MiniChatOptions Options { get; }
    public Database Db { get; }
    public LocalIdentity Identity { get; }
    public NetworkPolicy Policy { get; }
    public StickerService Stickers { get; }
    /// <summary>Claves del historial cifrado (PIN + cuenta de Windows).</summary>
    public Vault Vault { get; }
    public string MachineName { get; }
    public string DisplayName => _displayName;
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(_displayName);
    /// <summary>La clave de identidad anterior no se encontró y se generó una nueva: los contactos deben volver a emparejarse.</summary>
    public bool IdentityRegenerated { get; }
    public TransportServer? Server => _server;
    public DiscoveryService? Discovery => _discovery;

    public event EventHandler<long>? ContactChanged;
    public event EventHandler? PeopleChanged;
    public event EventHandler<MessageEvent>? MessageChanged;
    public event EventHandler<EngineNotice>? Notice;

    // ======================= Ciclo de vida =======================

    /// <summary>Abre datos e identidad. No toca la red. Lanza <see cref="DatabaseUnavailableException"/> si los datos no son legibles.</summary>
    public static ChatEngine Open(AppPaths paths, MiniChatOptions options, INetworkInventory inventory, ILog? log = null, string? machineName = null)
    {
        log ??= NullLog.Instance;
        paths.EnsureCreated();
        var db = Database.Open(paths.DatabaseFile, paths.BackupDirectory, log);

        var record = db.Read(c => ProfileRepo.GetIdentity(c));
        var keyName = record?.KeyName ?? "MiniChatLAN-" + Guid.NewGuid().ToString("N");
        var identity = LocalIdentity.OpenOrCreate(keyName);
        bool regenerated = record is not null && record.Value.Fingerprint != identity.Fingerprint;
        if (record is null || regenerated)
            db.Write((c, tx) => ProfileRepo.SaveIdentity(c, tx, keyName, identity.Fingerprint));
        if (regenerated) log.Warn("identity", "La clave de identidad anterior no estaba disponible; se generó una nueva");

        var displayName = db.Read(c => ProfileRepo.GetDisplayName(c));
        var policy = new NetworkPolicy(inventory, options);
        var engine = new ChatEngine(paths, options, db, identity, policy, log, machineName ?? Environment.MachineName, displayName, regenerated);
        engine.RecoverAfterRestart();
        engine.LoadNetworkLists();
        log.Info("engine", $"Perfil abierto. Identidad {LogFormat.Short(identity.Fingerprint)}");
        return engine;
    }

    private void RecoverAfterRestart()
    {
        Db.Write((c, tx) =>
        {
            int interrupted = MessagesRepo.RecoverInterruptedSends(c, tx);
            int queued = c.Exec("UPDATE messages SET status = 4, error = 'No se envió antes de cerrar la aplicación.', status_changed_at = $t WHERE direction = 0 AND status = 0;",
                tx, ("$t", Time.Now()));
            if (interrupted + queued > 0) _log.Info("engine", $"Recuperación: {interrupted} sin confirmar, {queued} sin enviar");
        });
        ExpireOldRequests();
    }

    /// <summary>Inicia escucha TCP y descubrimiento. Requiere nombre visible.</summary>
    public void StartNetworking()
    {
        if (_networkStarted) return;
        if (!HasDisplayName) throw new InvalidOperationException("Configure primero el nombre visible.");
        // Sin PIN no se podría cifrar lo que llegue: la red no se abre hasta que exista.
        if (NeedsPinSetup) throw new InvalidOperationException("Configure primero el PIN.");
        _networkStarted = true;
        _networkStartedAt = DateTimeOffset.UtcNow;
        _pool = new ConnectionPool(Options, Policy, Identity, LocalHelloInfo, _log);
        _server = new TransportServer(Options, Policy, Identity.Certificate, LocalHelloInfo, this, _log);
        _discovery = new DiscoveryService(Options, Policy, SelfAnnouncement, _log);
        _discovery.PeerSeen += OnPeerSeen;
        _discovery.PeerLost += OnPeerLost;
        _discovery.PriorityTargets = KnownContactAddresses;
        _discovery.RemoteSweepEnabled = GetSetting(SettingRemoteSweep) != "0";
        Policy.Refresh();
        _server.SyncListeners();
        _discovery.Start();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _maintenanceTimer = new Timer(_ => Maintenance(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
        _ = Task.Run(() => RedeliverDecisionsAsync(null));
        _ = Stickers.CollectGarbageAsync();
        CleanupOpenedFiles();
        _ = Task.Run(() => ResumeFileWork(null));
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Varios eventos seguidos: se agrupan y se aplica una sola vez.
        _networkDebounce?.Dispose();
        _networkDebounce = new Timer(_ => RefreshNetwork(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    public void RefreshNetwork()
    {
        Policy.Refresh();
        _server?.SyncListeners();
        _discovery?.SyncSockets();
        _discovery?.AnnounceSoon();
        PeopleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Maintenance()
    {
        try
        {
            ExpireOldRequests();
            Db.Write((c, tx) => RequestsRepo.PruneLog(c, tx, DateTimeOffset.UtcNow.AddDays(-1)));
        }
        catch (Exception ex)
        {
            _log.Warn("engine", "Fallo en mantenimiento", ex);
        }
    }

    private void ExpireOldRequests()
    {
        var cutoff = DateTimeOffset.UtcNow - Options.RequestExpiry;
        var changed = Db.Write((c, tx) =>
        {
            var expired = c.Query("SELECT id, contact_id, direction FROM requests WHERE status = 0 AND created_at < $c;",
                r => (Id: r.GetString(0), Contact: r.GetInt64(1), Dir: (RequestDirection)r.GetInt32(2)), tx, ("$c", cutoff.ToUnixTimeMilliseconds()));
            foreach (var e in expired)
            {
                RequestsRepo.Resolve(c, tx, e.Id, RequestStatus.Expired);
                var contact = ContactsRepo.Get(c, e.Contact, tx);
                if (contact?.Relation is Relation.OutgoingPending or Relation.IncomingPending)
                    ContactsRepo.SetRelation(c, tx, e.Contact, Relation.None);
            }
            return expired.Select(e => e.Contact).Distinct().ToList();
        });
        foreach (var id in changed) ContactChanged?.Invoke(this, id);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _networkDebounce?.Dispose();
        if (_maintenanceTimer is not null) await _maintenanceTimer.DisposeAsync().ConfigureAwait(false);
        Vault.Lock();
        try { await Task.WhenAll(_pumps.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        if (_discovery is not null) await _discovery.DisposeAsync().ConfigureAwait(false);
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        if (_pool is not null) await _pool.DisposeAsync().ConfigureAwait(false);
        Identity.Dispose();
        Vault.Dispose();
    }

    // ======================= Perfil y ajustes =======================

    private LocalHello LocalHelloInfo() => new(_instanceId, Truncate(_displayName), Truncate(MachineName), Options.TcpPort, AppVersion);

    private Announcement SelfAnnouncement() => new()
    {
        InstanceId = _instanceId,
        Fingerprint = Identity.Fingerprint,
        DisplayName = Truncate(_displayName),
        MachineName = Truncate(MachineName),
        Port = Options.TcpPort,
    };

    public static string AppVersion => typeof(ChatEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Versión completa, incluida una posible etiqueta de pre-release (p. ej. «1.1.0-beta.1»), para el updater.</summary>
    public static string AppVersionFull
    {
        get
        {
            var info = typeof(ChatEngine).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return AppVersion;
            return info.Split('+', 2)[0];   // descarta el metadato de compilación («+<sha>»)
        }
    }

    private string Truncate(string s) => s.Length <= Options.MaxNameChars ? s : s[..Options.MaxNameChars];

    public async Task<OperationResult> SetDisplayNameAsync(string name)
    {
        name = (name ?? "").Trim();
        if (!PacketValidator.IsName(name, Options.MaxNameChars))
            return OperationResult.Fail($"Escriba un nombre de 1 a {Options.MaxNameChars} caracteres, sin caracteres de control.");
        await Task.Run(() => Db.Write((c, tx) => ProfileRepo.SetDisplayName(c, tx, name))).ConfigureAwait(false);
        _displayName = name;
        _discovery?.AnnounceSoon();
        return OperationResult.Ok();
    }

    public string? GetSetting(string key) => Db.Read(c => SettingsRepo.Get(c, key));

    public void SetSetting(string key, string value) => Db.Write((c, tx) => SettingsRepo.Set(c, tx, key, value));

    private void LoadNetworkLists()
    {
        Policy.SetUserLists(ReadList(SettingTrustedNetworks), ReadList(SettingDeniedNetworks));
        ApplyRemoteSubnets();
    }

    private List<string> ReadList(string key)
    {
        try { return JsonSerializer.Deserialize<List<string>>(GetSetting(key) ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Autoriza o deniega explícitamente una red (por su clave estable) y reconfigura la red.</summary>
    public void SetNetworkTrust(string trustKey, bool? trusted)
    {
        var t = ReadList(SettingTrustedNetworks);
        var d = ReadList(SettingDeniedNetworks);
        t.Remove(trustKey);
        d.Remove(trustKey);
        if (trusted == true) t.Add(trustKey);
        if (trusted == false) d.Add(trustKey);
        SetSetting(SettingTrustedNetworks, JsonSerializer.Serialize(t));
        SetSetting(SettingDeniedNetworks, JsonSerializer.Serialize(d));
        Policy.SetUserLists(t, d);
        RefreshNetwork();
    }

    // ======================= Consultas =======================

    public string PairingCodeWith(string fingerprint) => IdentityCodes.PairingCode(Identity.Fingerprint, fingerprint);

    public bool IsOnline(string fingerprint) =>
        (_discovery?.IsAvailable(fingerprint) ?? false) ||
        (_lastDirectContact.TryGetValue(fingerprint, out var t) && DateTimeOffset.UtcNow - t < Options.PresenceTimeout);

    private void MarkDirectContact(string fingerprint) => _lastDirectContact[fingerprint] = DateTimeOffset.UtcNow;

    public Task<Contact?> GetContactAsync(long id) => Task.Run(() => Reveal(Db.Read(c => ContactsRepo.Get(c, id))));

    public Task<IReadOnlyList<PersonView>> GetPeopleAsync() => Task.Run<IReadOnlyList<PersonView>>(() =>
    {
        var (contacts, conversations, historyCounts) = Db.Read(c => (
            ContactsRepo.List(c).Select(x => Reveal(x)!).ToList(),
            MessagesRepo.ListConversations(c).ToDictionary(v => v.ContactId),
            c.Query("SELECT v.contact_id, COUNT(m.id) FROM conversations v JOIN messages m ON m.conversation_id = v.id GROUP BY v.contact_id;",
                r => (r.GetInt64(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2)));
        var discovered = (_discovery?.Peers ?? Array.Empty<DiscoveredPeer>()).ToDictionary(p => p.Fingerprint);
        var byFp = contacts.ToDictionary(c => c.Fingerprint);
        var list = new List<PersonView>();

        foreach (var c in contacts)
        {
            discovered.TryGetValue(c.Fingerprint, out var d);
            conversations.TryGetValue(c.Id, out var conv);
            list.Add(new PersonView(c.Fingerprint, c.Id, d?.DisplayName ?? c.DisplayName, d?.MachineName ?? c.MachineName,
                IsOnline(c.Fingerprint), d?.Endpoint, c.Relation, c.Blocked, conv?.UnreadCount ?? 0, d?.CompatibleVersion ?? true,
                null, null, historyCounts.GetValueOrDefault(c.Id) > 0));
        }

        var unknownPeers = discovered.Values.Where(d => !byFp.ContainsKey(d.Fingerprint)).ToList();
        foreach (var (fp, ep) in _manualEndpoints)
            if (!byFp.ContainsKey(fp) && !discovered.ContainsKey(fp) && IsOnline(fp))
                unknownPeers.Add(new DiscoveredPeer(fp, "(conexión manual)", ep.Address.ToString(), ep, "", DateTimeOffset.UtcNow, true));
        foreach (var d in unknownPeers)
        {
            // Mismo nombre de PC que un contacto conocido pero otra clave: posible reinstalación o suplantación.
            var twin = contacts.FirstOrDefault(c => string.Equals(c.MachineName, d.MachineName, StringComparison.OrdinalIgnoreCase)
                                                 && (c.Relation == Relation.Authorized || historyCounts.GetValueOrDefault(c.Id) > 0));
            list.Add(new PersonView(d.Fingerprint, null, d.DisplayName, d.MachineName, true, d.Endpoint, Relation.None, false, 0,
                d.CompatibleVersion, twin?.Id, twin?.DisplayName, false));
        }
        return list;
    });

    /// <summary>Página de historial (orden local estable), anterior a <paramref name="beforeId"/>.</summary>
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(long contactId, long? beforeId, int limit = 50) => Task.Run<IReadOnlyList<ChatMessage>>(() =>
        Db.Read(c =>
        {
            var conv = MessagesRepo.GetConversation(c, contactId);
            return conv is null ? new List<ChatMessage>() : MessagesRepo.Page(c, conv.Id, beforeId, limit);
        }).Select(m => Reveal(m)!).ToList());

    public Task<ChatMessage?> GetMessageAsync(long id) => Task.Run(() => Reveal(Db.Read(c => MessagesRepo.Get(c, id))));

    public Task<string?> GetDraftAsync(long contactId) => Task.Run(() => TryOpen(Db.Read(c => MessagesRepo.GetConversation(c, contactId)?.Draft)));

    public Task SaveDraftAsync(long contactId, string? draft) => Task.Run(() => Db.Write((c, tx) =>
    {
        var conv = MessagesRepo.GetOrCreateConversation(c, tx, contactId);
        MessagesRepo.SetDraft(c, tx, conv.Id, string.IsNullOrEmpty(draft) ? null : Vault.Seal(draft));
    }));

    public async Task MarkReadAsync(long contactId)
    {
        bool changed = await Task.Run(() => Db.Write((c, tx) =>
        {
            var conv = MessagesRepo.GetConversation(c, contactId, tx);
            if (conv is null || conv.UnreadCount == 0) return false;
            MessagesRepo.ResetUnread(c, tx, conv.Id);
            return true;
        })).ConfigureAwait(false);
        if (changed) UnreadChanged?.Invoke(this, contactId);
    }

    /// <summary>Cambió el número de no leídos de una conversación (sin otros cambios del contacto).</summary>
    public event EventHandler<long>? UnreadChanged;

    /// <summary>Direcciones candidatas para un par: descubrimiento, conexión manual y última dirección guardada.</summary>
    private List<IPEndPoint> EndpointsFor(string fingerprint, Contact? contact)
    {
        var list = new List<IPEndPoint>();
        if (_discovery is not null) list.AddRange(_discovery.EndpointsFor(fingerprint));
        if (_manualEndpoints.TryGetValue(fingerprint, out var manual)) list.Add(manual);
        if (contact?.LastAddress is { } addr && IPAddress.TryParse(addr, out var ip) && contact.LastPort is int port)
            list.Add(new IPEndPoint(ip, port));
        return list.DistinctBy(e => e.ToString()).ToList();
    }

    private void OnPeerSeen(object? sender, DiscoveredPeer peer)
    {
        // Los anuncios se repiten cada pocos segundos: solo se avisa a la interfaz si algo cambió.
        if (!peer.FromOtherSubnet) TrackPrefix(peer);
        var signature = $"{peer.DisplayName}|{peer.MachineName}|{peer.Endpoint}|{peer.CompatibleVersion}";
        if (!_peerSignatures.TryGetValue(peer.Fingerprint, out var old) || old != signature)
        {
            _peerSignatures[peer.Fingerprint] = signature;
            PeopleChanged?.Invoke(this, EventArgs.Empty);
        }
        // Si había una aceptación/rechazo pendiente de entregar, se reintenta (con espera mínima).
        if (Volatile.Read(ref _undeliveredDecisions) > 0)
            _ = Task.Run(() => RedeliverDecisionsAsync(peer.Fingerprint));
        if (Vault.IsUnlocked) _ = Task.Run(() => ResumeFileWork(peer.Fingerprint));
    }

    private void OnPeerLost(object? sender, string fingerprint)
    {
        _peerSignatures.TryRemove(fingerprint, out _);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly ConcurrentDictionary<string, string> _peerSignatures = new();
    private int _undeliveredDecisions = 1; // se recalcula tras el primer intento de reentrega

    private void RaiseContact(long id) => ContactChanged?.Invoke(this, id);
    private void RaiseNotice(EngineNotice n) => Notice?.Invoke(this, n);
    private void RaiseMessage(long contactId, ChatMessage m, bool isNew) => MessageChanged?.Invoke(this, new MessageEvent(contactId, Reveal(m)!, isNew));
}
