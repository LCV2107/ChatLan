using System.Collections.Concurrent;
using System.Net;
using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Identity;
using MiniChat.Core.Network;
using MiniChat.Core.Transport;

namespace MiniChat.Core.Engine;

/// <summary>
/// Conexiones salientes reutilizables, como máximo una por par. Se cierran por inactividad antes de que
/// el servidor remoto las cierre. No hay reconexiones automáticas en bucle: solo se conecta cuando hay
/// algo que enviar.
/// </summary>
internal sealed class ConnectionPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PeerClient> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectGates = new();
    private readonly MiniChatOptions _options;
    private readonly NetworkPolicy _policy;
    private readonly LocalIdentity _identity;
    private readonly Func<LocalHello> _hello;
    private readonly ILog _log;
    private readonly Timer _idleTimer;

    public ConnectionPool(MiniChatOptions options, NetworkPolicy policy, LocalIdentity identity, Func<LocalHello> hello, ILog log)
    {
        _options = options;
        _policy = policy;
        _identity = identity;
        _hello = hello;
        _log = log;
        _idleTimer = new Timer(_ => _ = CloseIdleAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private TimeSpan ClientIdleLimit => TimeSpan.FromTicks(_options.IdleTimeout.Ticks * 2 / 3);

    /// <summary>Devuelve una conexión verificada con la huella esperada, probando las direcciones en orden.</summary>
    public async Task<(PeerClient Client, bool Reused)> GetAsync(string fingerprint, IReadOnlyList<IPEndPoint> endpoints, CancellationToken ct)
    {
        var gate = _connectGates.GetOrAdd(fingerprint, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(fingerprint, out var existing))
            {
                if (!existing.IsBroken && DateTime.UtcNow - existing.LastUsed < ClientIdleLimit) return (existing, true);
                _clients.TryRemove(fingerprint, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }
            if (endpoints.Count == 0)
                throw new PeerUnreachableException("El contacto no está disponible en la red ahora mismo y no se conoce ninguna dirección suya.");

            Exception? last = null;
            foreach (var ep in endpoints)
            {
                try
                {
                    var client = await PeerClient.ConnectAsync(ep, fingerprint, _identity.Certificate, _hello(), _policy, _options, ct).ConfigureAwait(false);
                    _clients[fingerprint] = client;
                    return (client, false);
                }
                catch (IdentityMismatchException ex)
                {
                    _log.Warn("pool", $"{ep.Address} presenta otra identidad ({LogFormat.Short(ex.Actual)} en lugar de {LogFormat.Short(ex.Expected)})");
                    last = ex;
                }
                catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IncompatibleVersionException)
                {
                    last = ex;
                }
            }
            throw last!;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Conexión puntual sin huella esperada (conexión manual por IP).</summary>
    public Task<PeerClient> ProbeAsync(IPEndPoint endpoint, CancellationToken ct) =>
        PeerClient.ConnectAsync(endpoint, null, _identity.Certificate, _hello(), _policy, _options, ct);

    public async Task DropAsync(string fingerprint)
    {
        if (_clients.TryRemove(fingerprint, out var c)) await c.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CloseIdleAsync()
    {
        foreach (var (fp, client) in _clients)
        {
            if (client.IsBroken || DateTime.UtcNow - client.LastUsed >= ClientIdleLimit)
            {
                var gate = _connectGates.GetOrAdd(fp, _ => new SemaphoreSlim(1, 1));
                if (!await gate.WaitAsync(0).ConfigureAwait(false)) continue;
                try
                {
                    if (_clients.TryGetValue(fp, out var current) && ReferenceEquals(current, client) && _clients.TryRemove(fp, out _))
                        await client.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        foreach (var c in _clients.Values) await c.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }
}
