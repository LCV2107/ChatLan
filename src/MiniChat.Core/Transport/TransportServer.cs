using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Network;
using MiniChat.Core.Protocol;

namespace MiniChat.Core.Transport;

/// <summary>Respuesta de un manejador: paquete y cuerpo binario opcional.</summary>
public readonly record struct Reply(PacketBase Packet, ReadOnlyMemory<byte> Body = default);

/// <summary>Lógica de aplicación para las conexiones entrantes (la implementa el motor de chat).</summary>
public interface IInboundHandler
{
    /// <summary>Se invoca tras el handshake. La huella es la demostrada por TLS.</summary>
    Task OnPeerHelloAsync(PeerInfo peer, CancellationToken ct);

    /// <summary>Procesa una petición ya validada sintácticamente y devuelve la respuesta.</summary>
    Task<Reply> HandleAsync(PeerInfo peer, Frame frame, CancellationToken ct);
}

/// <summary>
/// Servidor TCP+TLS. Escucha SOLO en las direcciones de las interfaces autorizadas (no en 0.0.0.0) y
/// vuelve a comprobar cada conexión entrante contra la política de red. Limita conexiones totales,
/// conexiones por dirección, tramas por segundo y tramas mal formadas.
/// </summary>
public sealed class TransportServer : IAsyncDisposable
{
    private readonly MiniChatOptions _options;
    private readonly NetworkPolicy _policy;
    private readonly X509Certificate2 _certificate;
    private readonly Func<LocalHello> _hello;
    private readonly IInboundHandler _handler;
    private readonly ILog _log;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new();
    private readonly Dictionary<IPAddress, (TcpListener Listener, CancellationTokenSource Cts)> _listeners = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SslStreamCertificateContext _certContext;

    public TransportServer(MiniChatOptions options, NetworkPolicy policy, X509Certificate2 certificate,
        Func<LocalHello> hello, IInboundHandler handler, ILog? log = null)
    {
        _options = options;
        _policy = policy;
        _certificate = certificate;
        _hello = hello;
        _handler = handler;
        _log = log ?? NullLog.Instance;
        _connectionSlots = new SemaphoreSlim(options.MaxInboundConnections);
        _certContext = SslStreamCertificateContext.Create(_certificate, null, offline: true);
    }

    /// <summary>Direcciones en las que se está escuchando realmente.</summary>
    public IReadOnlyList<IPAddress> ListeningAddresses { get { lock (_gate) return _listeners.Keys.ToList(); } }

    /// <summary>Errores al abrir puertos (p. ej. puerto ocupado), para mostrarlos al usuario.</summary>
    public string? LastBindError { get; private set; }

    /// <summary>Sincroniza los listeners con las interfaces autorizadas actuales.</summary>
    public void SyncListeners()
    {
        var wanted = _policy.AllowedInterfaces.Select(i => i.Address).Distinct().ToHashSet();
        lock (_gate)
        {
            foreach (var addr in _listeners.Keys.Where(a => !wanted.Contains(a)).ToList())
            {
                var (listener, cts) = _listeners[addr];
                cts.Cancel();
                listener.Stop();
                _listeners.Remove(addr);
                _log.Info("tcp", $"Deja de escuchar en {addr}");
            }
            string? error = null;
            foreach (var addr in wanted.Where(a => !_listeners.ContainsKey(a)))
            {
                var listener = new TcpListener(addr, _options.TcpPort);
                listener.Server.ExclusiveAddressUse = true;
                try
                {
                    listener.Start(backlog: 16);
                }
                catch (SocketException ex)
                {
                    error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                        ? $"El puerto TCP {_options.TcpPort} ya está en uso en {addr}. ¿Hay otra instancia de MiniChat abierta?"
                        : $"No se pudo escuchar en {addr}:{_options.TcpPort} ({ex.SocketErrorCode}).";
                    _log.Warn("tcp", error);
                    continue;
                }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _listeners[addr] = (listener, cts);
                _log.Info("tcp", $"Escuchando en {addr}:{_options.TcpPort}");
                _ = AcceptLoopAsync(listener, addr, cts.Token);
            }
            LastBindError = error;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, IPAddress localAddress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                _log.Warn("tcp", "Error aceptando conexión", ex);
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var remoteAddress = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;

        if (!_policy.IsAllowedInbound(local.Address, remoteAddress))
        {
            _log.Warn("tcp", $"Conexión rechazada desde {remoteAddress}: fuera de la red autorizada");
            client.Dispose();
            return;
        }
        if (!_connectionSlots.Wait(0))
        {
            _log.Warn("tcp", "Conexión rechazada: límite de conexiones alcanzado");
            client.Dispose();
            return;
        }
        int perAddress = _perAddress.AddOrUpdate(remoteAddress, 1, (_, n) => n + 1);
        try
        {
            if (perAddress > _options.MaxInboundConnectionsPerAddress)
            {
                _log.Warn("tcp", $"Conexión rechazada desde {remoteAddress}: demasiadas conexiones");
                return;
            }
            client.NoDelay = true;
            await ServeAsync(client, remoteAddress, remote.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            _log.Debug("tcp", $"Conexión con {remoteAddress} terminada: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            _log.Error("tcp", $"Error inesperado en la conexión con {remoteAddress}", ex);
        }
        finally
        {
            _perAddress.AddOrUpdate(remoteAddress, 0, (_, n) => Math.Max(0, n - 1));
            _connectionSlots.Release();
            client.Dispose();
        }
    }

    private async Task ServeAsync(TcpClient client, IPAddress remoteAddress, int remotePort, CancellationToken ct)
    {
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        string? fingerprint = null;
        var sslOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = _certContext,
            ClientCertificateRequired = true,
            EnabledSslProtocols = TlsPolicy.Protocols,
            CertificateChainPolicy = TlsPolicy.OfflineChainPolicy(),
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                fingerprint = TlsPolicy.AcceptPeerCertificate(cert, errors);
                return fingerprint is not null;
            },
        };
        using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hsCts.CancelAfter(_options.HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(sslOptions, hsCts.Token).ConfigureAwait(false);
        }
        if (fingerprint is null) return;

        var codec = new FrameCodec(_options.MaxFrameBytes, _options.MaxHeaderBytes);

        // ---- Handshake de aplicación: la primera trama debe ser "hello". ----
        Frame? first;
        using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hsCts.CancelAfter(_options.HandshakeTimeout);
            try
            {
                first = await codec.ReadAsync(ssl, hsCts.Token).ConfigureAwait(false);
            }
            catch (FrameFormatException ex)
            {
                await TrySendAsync(codec, ssl, new ErrorPacket { Code = ErrorCodes.Malformed, Message = ex.Message }, ct).ConfigureAwait(false);
                return;
            }
        }
        if (first?.Packet is not HelloPacket hello)
        {
            await TrySendAsync(codec, ssl, new ErrorPacket { Code = ErrorCodes.HandshakeRequired, Message = "Se esperaba hello.", ReplyTo = first?.Packet.Id }, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            PacketValidator.Validate(hello, _options);
        }
        catch (FrameFormatException ex)
        {
            await TrySendAsync(codec, ssl, new ErrorPacket { Code = ErrorCodes.Malformed, Message = ex.Message, ReplyTo = hello.Id }, ct).ConfigureAwait(false);
            return;
        }
        var version = ProtocolInfo.Negotiate(hello.MinVersion, hello.MaxVersion);
        if (version is null)
        {
            _log.Warn("tcp", $"Versión incompatible de {LogFormat.Short(fingerprint)}: {hello.MinVersion}-{hello.MaxVersion}");
            await TrySendAsync(codec, ssl, new ErrorPacket
            {
                Code = ErrorCodes.IncompatibleVersion,
                Message = "Versión de protocolo incompatible.",
                MinVersion = ProtocolInfo.MinVersion,
                MaxVersion = ProtocolInfo.MaxVersion,
                ReplyTo = hello.Id,
            }, ct).ConfigureAwait(false);
            return;
        }

        var peer = new PeerInfo(fingerprint, new IPEndPoint(remoteAddress, remotePort), version.Value, hello.InstanceId,
            hello.DisplayName.Trim(), hello.MachineName.Trim(), hello.ListenPort, hello.AppVersion);
        var me = _hello();
        await codec.WriteAsync(ssl, new HelloOkPacket
        {
            ReplyTo = hello.Id,
            Version = version.Value,
            InstanceId = me.InstanceId,
            DisplayName = me.DisplayName,
            MachineName = me.MachineName,
            ListenPort = me.ListenPort,
            AppVersion = me.AppVersion,
        }, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        await _handler.OnPeerHelloAsync(peer, ct).ConfigureAwait(false);

        // ---- Bucle de peticiones ----
        int malformed = 0;
        var window = DateTime.UtcNow;
        int framesInWindow = 0;
        while (!ct.IsCancellationRequested)
        {
            Frame? frame;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(_options.IdleTimeout);
                try
                {
                    frame = await codec.ReadAsync(ssl, idle.Token).ConfigureAwait(false);
                }
                catch (FrameFormatException ex)
                {
                    _log.Warn("tcp", $"Trama mal formada de {LogFormat.Short(fingerprint)}: {ex.Message}");
                    if (++malformed >= _options.MaxMalformedFramesPerConnection) return;
                    await codec.WriteAsync(ssl, new ErrorPacket { Code = ErrorCodes.Malformed, Message = "Trama mal formada." }, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    continue;
                }
                catch (ProtocolException ex)
                {
                    _log.Warn("tcp", $"Error de protocolo de {LogFormat.Short(fingerprint)}: {ex.Message}");
                    await TrySendAsync(codec, ssl, new ErrorPacket { Code = ex.Code, Message = "Trama no válida." }, ct).ConfigureAwait(false);
                    return;
                }
            }
            if (frame is null) return; // cierre limpio

            if ((DateTime.UtcNow - window).TotalSeconds >= 1) { window = DateTime.UtcNow; framesInWindow = 0; }
            if (++framesInWindow > _options.MaxFramesPerSecondPerConnection)
            {
                await codec.WriteAsync(ssl, new ErrorPacket { Code = ErrorCodes.RateLimited, Message = "Demasiadas peticiones.", ReplyTo = frame.Packet.Id }, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                continue;
            }

            Reply reply;
            try
            {
                PacketValidator.Validate(frame.Packet, _options);
                reply = frame.Packet switch
                {
                    PingPacket => new Reply(new PongPacket()),
                    HelloPacket => new Reply(new ErrorPacket { Code = ErrorCodes.UnsupportedOp, Message = "Saludo repetido." }),
                    _ => await _handler.HandleAsync(peer, frame, ct).ConfigureAwait(false),
                };
            }
            catch (FrameFormatException ex)
            {
                _log.Warn("tcp", $"Paquete no válido de {LogFormat.Short(fingerprint)}: {ex.Message}");
                if (++malformed >= _options.MaxMalformedFramesPerConnection) return;
                reply = new Reply(new ErrorPacket { Code = ErrorCodes.Malformed, Message = "Paquete no válido." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error("tcp", "Error procesando una petición", ex);
                reply = new Reply(new ErrorPacket { Code = ErrorCodes.Internal, Message = "Error interno." });
            }
            var packet = reply.Packet with { ReplyTo = frame.Packet.Id };
            await codec.WriteAsync(ssl, packet, reply.Body, ct).ConfigureAwait(false);
        }
    }

    private static async Task TrySendAsync(FrameCodec codec, Stream stream, PacketBase packet, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await codec.WriteAsync(stream, packet, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_gate)
        {
            foreach (var (listener, cts) in _listeners.Values)
            {
                cts.Cancel();
                listener.Stop();
            }
            _listeners.Clear();
        }
        await Task.CompletedTask;
    }
}
