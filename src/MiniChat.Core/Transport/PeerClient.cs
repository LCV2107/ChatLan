using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MiniChat.Core.Config;
using MiniChat.Core.Network;
using MiniChat.Core.Protocol;

namespace MiniChat.Core.Transport;

/// <summary>Datos del otro extremo tras un handshake correcto. La huella procede de TLS, no de lo que el par declare.</summary>
public sealed record PeerInfo(
    string Fingerprint,
    IPEndPoint RemoteEndPoint,
    int ProtocolVersion,
    string InstanceId,
    string DisplayName,
    string MachineName,
    int ListenPort,
    string AppVersion);

/// <summary>Datos locales que se envían en el handshake.</summary>
public sealed record LocalHello(string InstanceId, string DisplayName, string MachineName, int ListenPort, string AppVersion);

/// <summary>
/// Conexión saliente ya autenticada (TLS mutuo + hello). Modelo petición/respuesta: una petición en curso
/// a la vez por conexión; el servidor nunca inicia operaciones sobre una conexión entrante.
/// </summary>
public sealed class PeerClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly FrameCodec _codec;
    private readonly MiniChatOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _broken;

    private PeerClient(TcpClient tcp, SslStream ssl, FrameCodec codec, MiniChatOptions options, PeerInfo peer)
    {
        _tcp = tcp;
        _ssl = ssl;
        _codec = codec;
        _options = options;
        Peer = peer;
        LastUsed = DateTime.UtcNow;
    }

    public PeerInfo Peer { get; }

    /// <summary>Versión de TLS negociada con el par.</summary>
    public System.Security.Authentication.SslProtocols TlsProtocol => _ssl.SslProtocol;
    public DateTime LastUsed { get; private set; }
    public bool IsBroken => _broken || !_tcp.Connected;

    /// <summary>
    /// Conecta respetando la política de red: la IP remota debe estar en una subred autorizada y el socket
    /// se enlaza a la dirección local de esa interfaz.
    /// </summary>
    public static async Task<PeerClient> ConnectAsync(
        IPEndPoint remote, string? expectedFingerprint, X509Certificate2 localCertificate, LocalHello hello,
        NetworkPolicy policy, MiniChatOptions options, CancellationToken ct)
    {
        var iface = policy.InterfaceFor(remote.Address)
            ?? throw new NetworkNotAllowedException(policy.ExplainRejection(remote.Address));

        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            tcp.Client.Bind(new IPEndPoint(iface.Address, 0));
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(options.ConnectTimeout);
                try
                {
                    await tcp.ConnectAsync(remote, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new PeerUnreachableException($"Tiempo de espera agotado al conectar con {remote}. El equipo puede estar apagado, sin MiniChat abierto o bloqueado por un firewall.");
                }
                catch (SocketException ex)
                {
                    throw new PeerUnreachableException(DescribeSocketError(ex, remote), ex);
                }
            }

            string? actualFingerprint = null;
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = TlsPolicy.TargetHost,
                EnabledSslProtocols = TlsPolicy.Protocols,
                ClientCertificateContext = SslStreamCertificateContext.Create(localCertificate, null, offline: true),
                CertificateChainPolicy = TlsPolicy.OfflineChainPolicy(),
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    actualFingerprint = TlsPolicy.AcceptPeerCertificate(cert, errors);
                    return actualFingerprint is not null;
                },
            };
            using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hsCts.CancelAfter(options.HandshakeTimeout);
                try
                {
                    await ssl.AuthenticateAsClientAsync(sslOptions, hsCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    throw new PeerUnreachableException($"No se pudo establecer la conexión cifrada con {remote}. ¿Es un equipo con MiniChat LAN?", ex);
                }
            }

            if (actualFingerprint is null) throw new PeerUnreachableException("El otro equipo no presentó una identidad válida.");
            if (expectedFingerprint is not null && !string.Equals(expectedFingerprint, actualFingerprint, StringComparison.Ordinal))
                throw new IdentityMismatchException(expectedFingerprint, actualFingerprint);

            var codec = new FrameCodec(options.MaxFrameBytes, options.MaxHeaderBytes);
            var request = new HelloPacket
            {
                InstanceId = hello.InstanceId,
                DisplayName = hello.DisplayName,
                MachineName = hello.MachineName,
                ListenPort = hello.ListenPort,
                AppVersion = hello.AppVersion,
            };
            Frame? reply;
            using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hsCts.CancelAfter(options.HandshakeTimeout);
                await codec.WriteAsync(ssl, request, ReadOnlyMemory<byte>.Empty, hsCts.Token).ConfigureAwait(false);
                reply = await codec.ReadAsync(ssl, hsCts.Token).ConfigureAwait(false);
            }

            switch (reply?.Packet)
            {
                case HelloOkPacket ok:
                    PacketValidator.Validate(ok, options);
                    if (ok.Version < ProtocolInfo.MinVersion || ok.Version > ProtocolInfo.MaxVersion)
                        throw new IncompatibleVersionException(ok.Version, ok.Version);
                    var peer = new PeerInfo(actualFingerprint, remote, ok.Version, ok.InstanceId, ok.DisplayName.Trim(),
                        ok.MachineName.Trim(), ok.ListenPort, ok.AppVersion);
                    var client = new PeerClient(tcp, ssl, codec, options, peer);
                    tcp = null!;
                    ssl = null;
                    return client;
                case ErrorPacket { Code: ErrorCodes.IncompatibleVersion } err:
                    throw new IncompatibleVersionException(err.MinVersion, err.MaxVersion);
                case ErrorPacket err:
                    throw new PeerUnreachableException($"El otro equipo rechazó la conexión ({err.Code}).");
                default:
                    throw new PeerUnreachableException("Respuesta inesperada durante el saludo.");
            }
        }
        finally
        {
            if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
            tcp?.Dispose();
        }
    }

    /// <summary>
    /// Envía una petición y espera su respuesta. <paramref name="sentFully"/> indica si la trama llegó a
    /// escribirse por completo (necesario para distinguir "fallido" de "no confirmado").
    /// </summary>
    public async Task<Frame> RequestAsync(PacketBase packet, ReadOnlyMemory<byte> body, CancellationToken ct, StrongBox<bool>? sentFully = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsBroken) throw new PeerUnreachableException("La conexión se cerró.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.RequestTimeout);
            try
            {
                await _codec.WriteAsync(_ssl, packet, body, cts.Token).ConfigureAwait(false);
                if (sentFully is not null) sentFully.Value = true;
                while (true)
                {
                    var frame = await _codec.ReadAsync(_ssl, cts.Token).ConfigureAwait(false)
                        ?? throw new PeerUnreachableException("El otro equipo cerró la conexión antes de responder.");
                    PacketValidator.Validate(frame.Packet, _options);
                    if (frame.Packet.ReplyTo == packet.Id) { LastUsed = DateTime.UtcNow; return frame; }
                    // Respuesta a otra petición (no debería ocurrir): se ignora.
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or ProtocolException)
            {
                _broken = true;
                throw new PeerUnreachableException("Se perdió la conexión durante la transmisión.", ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _broken = true;
                throw new PeerTimeoutException("El otro equipo no respondió a tiempo.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string DescribeSocketError(SocketException ex, IPEndPoint remote) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => $"{remote.Address} rechazó la conexión: MiniChat no está abierto en ese equipo o usa otro puerto.",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => $"No hay ruta hacia {remote.Address}. Puede estar en otra red, VLAN o con aislamiento de clientes Wi-Fi.",
        SocketError.TimedOut => $"Tiempo de espera agotado con {remote}. Revise que el firewall del otro equipo permita MiniChat en redes privadas.",
        _ => $"Error de red al conectar con {remote}: {ex.SocketErrorCode}.",
    };

    public async ValueTask DisposeAsync()
    {
        _broken = true;
        try { await _ssl.DisposeAsync().ConfigureAwait(false); } catch { }
        _tcp.Dispose();
        _gate.Dispose();
    }
}

public sealed class StrongBox<T>
{
    public T? Value { get; set; }
}

public class PeerUnreachableException : Exception
{
    public PeerUnreachableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PeerTimeoutException : PeerUnreachableException
{
    public PeerTimeoutException(string message) : base(message) { }
}

public sealed class NetworkNotAllowedException : Exception
{
    public NetworkNotAllowedException(string message) : base(message) { }
}
