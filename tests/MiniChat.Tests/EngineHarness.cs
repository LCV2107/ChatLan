using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Engine;
using MiniChat.Core.Identity;
using MiniChat.Core.Model;

namespace MiniChat.Tests;

/// <summary>
/// Un "equipo" de prueba: motor real con su propia carpeta de datos y su propia clave CNG.
/// Se comunica por loopback (TLS y multicast reales). Al terminar borra la carpeta y la clave.
/// </summary>
public sealed class TestPeer : IAsyncDisposable
{
    private readonly List<string> _keys = new();
    public const string Pin = "482915";
    public string Name { get; }
    public string Dir { get; }
    public MiniChatOptions Options { get; private set; }
    public ChatEngine Engine { get; private set; } = null!;
    public List<EngineNotice> Notices { get; } = new();

    private TestPeer(string name, string dir, MiniChatOptions options)
    {
        Name = name;
        Dir = dir;
        Options = options;
    }

    public static MiniChatOptions FastOptions(int discoveryPort, int tcpPort) => new()
    {
        AllowLoopback = true,
        TcpPort = tcpPort,
        DiscoveryPort = discoveryPort,
        AnnounceInterval = TimeSpan.FromMilliseconds(300),
        AnnounceJitter = TimeSpan.FromMilliseconds(50),
        PresenceTimeout = TimeSpan.FromSeconds(3),
        PresenceSweepInterval = TimeSpan.FromMilliseconds(250),
        RequestTimeout = TimeSpan.FromSeconds(4),
        HandshakeTimeout = TimeSpan.FromSeconds(4),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        DecisionRedeliveryMinInterval = TimeSpan.FromMilliseconds(200),
        PinIterations = 1000, // las pruebas no necesitan el coste real de PBKDF2
    };

    public static async Task<TestPeer> StartAsync(string name, int discoveryPort, string? dir = null, MiniChatOptions? options = null, bool network = true)
    {
        dir ??= Path.Combine(Path.GetTempPath(), "MiniChatTests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        var peer = new TestPeer(name, dir, options ?? FastOptions(discoveryPort, TestNet.NextPort()));
        await peer.OpenAsync(network);
        return peer;
    }

    public async Task OpenAsync(bool network = true)
    {
        Engine = ChatEngine.Open(new AppPaths(Dir), Options, new LoopbackInventory(), new RollingFileLog(Path.Combine(Dir, "logs"), minLevel: LogLevel.Debug), "PC-" + Name);
        if (!_keys.Contains(Engine.Identity.KeyName)) _keys.Add(Engine.Identity.KeyName);
        Engine.Notice += (_, n) => { lock (Notices) Notices.Add(n); };
        if (!Engine.HasDisplayName) await Engine.SetDisplayNameAsync(Name);
        var pin = await (Engine.NeedsPinSetup ? Engine.SetupPinAsync(Pin) : Engine.UnlockAsync(Pin));
        if (!pin.Success) throw new InvalidOperationException("PIN de prueba: " + pin.Message);
        if (network)
        {
            Engine.StartNetworking();
            if (Engine.Server!.LastBindError is { } e) throw new InvalidOperationException("Listener de prueba no disponible: " + e);
        }
    }

    /// <summary>Cierra y vuelve a abrir el mismo perfil (como cerrar y abrir la aplicación).</summary>
    public async Task RestartAsync(int? newTcpPort = null, bool network = true)
    {
        await Engine.DisposeAsync();
        if (newTcpPort is int p) Options = Options with { TcpPort = p };
        await OpenAsync(network);
    }

    public Task StopAsync() => Engine.DisposeAsync().AsTask();

    /// <summary>Simula una reinstalación que pierde la clave: borra la clave CNG del perfil.</summary>
    public void LoseIdentityKey() => LocalIdentity.Delete(Engine.Identity.KeyName);

    public string Fingerprint => Engine.Identity.Fingerprint;

    public async Task<PersonView?> PersonAsync(string fingerprint) =>
        (await Engine.GetPeopleAsync()).FirstOrDefault(p => p.Fingerprint == fingerprint);

    public async Task<long> ContactIdAsync(TestPeer other) =>
        (await PersonAsync(other.Fingerprint))?.ContactId ?? throw new InvalidOperationException($"{Name} no tiene a {other.Name} como contacto");

    public async Task<Relation> RelationWithAsync(TestPeer other) => (await PersonAsync(other.Fingerprint))?.Relation ?? Relation.None;

    public async Task<IReadOnlyList<ChatMessage>> MessagesWithAsync(TestPeer other, int limit = 500) =>
        await Engine.GetMessagesAsync(await ContactIdAsync(other), null, limit);

    public int NoticeCount(NoticeKind kind) { lock (Notices) return Notices.Count(n => n.Kind == kind); }

    public async ValueTask DisposeAsync()
    {
        try { await Engine.DisposeAsync(); } catch { }
        foreach (var k in _keys) try { LocalIdentity.Delete(k); } catch { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public static class Wait
{
    public static async Task Until(Func<Task<bool>> condition, string what, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("No se cumplió a tiempo: " + what);
            await Task.Delay(50);
        }
    }

    public static Task Until(Func<bool> condition, string what, int timeoutMs = 8000) => Until(() => Task.FromResult(condition()), what, timeoutMs);

    /// <summary>Espera a que A vea a B como disponible por descubrimiento.</summary>
    public static Task Discovered(TestPeer a, TestPeer b) =>
        Until(async () => (await a.PersonAsync(b.Fingerprint))?.Online == true, $"{a.Name} descubre a {b.Name}");

    /// <summary>Empareja A→B: solicitud de A, aceptación de B, ambos autorizados.</summary>
    public static async Task Pair(TestPeer a, TestPeer b)
    {
        await Discovered(a, b);
        var r = await a.Engine.SendChatRequestAsync(b.Fingerprint);
        Assert.True(r.Success, r.Message);
        var accept = await b.Engine.AcceptAsync(await b.ContactIdAsync(a));
        Assert.True(accept.Success, accept.Message);
        await Until(async () => await a.RelationWithAsync(b) == Relation.Authorized, $"{a.Name} recibe la aceptación");
        Assert.Equal(Relation.Authorized, await b.RelationWithAsync(a));
    }

    public static Task Status(TestPeer peer, long messageId, MessageStatus status, int timeoutMs = 8000) =>
        Until(async () => (await peer.Engine.GetMessageAsync(messageId))?.Status == status, $"mensaje {messageId} → {status}", timeoutMs);
}
