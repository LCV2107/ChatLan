using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MiniChat.Core.Config;
using MiniChat.Core.Identity;
using MiniChat.Core.Network;
using MiniChat.Core.Protocol;
using MiniChat.Core.Transport;

namespace MiniChat.Tests;

/// <summary>Identidad CNG real con nombre único; se borra del almacén del usuario al terminar.</summary>
public sealed class TestIdentity : IDisposable
{
    public TestIdentity() => Identity = LocalIdentity.OpenOrCreate("MiniChatLAN-test-" + Guid.NewGuid().ToString("N"));
    public LocalIdentity Identity { get; }
    public void Dispose()
    {
        var name = Identity.KeyName;
        Identity.Dispose();
        LocalIdentity.Delete(name);
    }
}

public sealed class LoopbackInventory : INetworkInventory
{
    public IReadOnlyList<LocalInterface> GetInterfaces() => new[]
    {
        new LocalInterface("lo", "Loopback", "", IPAddress.Loopback, 8, NetworkInterfaceType.Loopback, NetworkCategory.Private, "lo", "", false),
    };
}

public static class TestNet
{
    private static readonly HashSet<int> Handed = new();

    /// <summary>Puerto libre elegido por el sistema (evita chocar con servicios que escuchan en el rango dinámico).</summary>
    public static int NextPort()
    {
        while (true)
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            using var u = new System.Net.Sockets.UdpClient();
            try { u.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)); } catch (System.Net.Sockets.SocketException) { continue; }
            lock (Handed) if (Handed.Add(port)) return port;
        }
    }

    public static MiniChatOptions Options(int? port = null) => new()
    {
        AllowLoopback = true,
        TcpPort = port ?? NextPort(),
        DiscoveryPort = NextPort(),
        RequestTimeout = TimeSpan.FromSeconds(5),
        HandshakeTimeout = TimeSpan.FromSeconds(5),
    };

    public static NetworkPolicy LoopbackPolicy(MiniChatOptions o)
    {
        var p = new NetworkPolicy(new LoopbackInventory(), o);
        p.Refresh();
        return p;
    }
}

public class TransportTests
{
    private sealed class EchoHandler : IInboundHandler
    {
        public PeerInfo? LastPeer;
        public Task OnPeerHelloAsync(PeerInfo peer, CancellationToken ct) { LastPeer = peer; return Task.CompletedTask; }
        public Task<Reply> HandleAsync(PeerInfo peer, Frame frame, CancellationToken ct) => frame.Packet switch
        {
            MessagePacket m => Task.FromResult(new Reply(new MessageAckPacket { MessageId = m.MessageId, Status = MessageAckStatuses.Stored })),
            _ => Task.FromResult(new Reply(new AckPacket { Status = AckStatuses.Ok })),
        };
    }

    private static LocalHello Hello(string name) => new(Guid.NewGuid().ToString("N"), name, "PC-" + name, 1, "test");

    [Fact]
    public async Task MutualTls_IdentifiesBothSides_ByKeyFingerprint()
    {
        using var a = new TestIdentity();
        using var b = new TestIdentity();
        var o = TestNet.Options();
        var policy = TestNet.LoopbackPolicy(o);
        var handler = new EchoHandler();
        await using var server = new TransportServer(o, policy, b.Identity.Certificate, () => Hello("Bea"), handler);
        server.SyncListeners();

        await using var client = await PeerClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, o.TcpPort), b.Identity.Fingerprint,
            a.Identity.Certificate, Hello("Ana"), policy, o, TestContext.Current.CancellationToken);

        Assert.Equal(b.Identity.Fingerprint, client.Peer.Fingerprint);
        Assert.Equal("Bea", client.Peer.DisplayName);
        var reply = await client.RequestAsync(new MessagePacket { MessageId = Guid.NewGuid().ToString("N"), Text = "hola ñ 😀" }, default, TestContext.Current.CancellationToken);
        Assert.IsType<MessageAckPacket>(reply.Packet);
        // El servidor identificó al cliente por su certificado, no por lo que declaró.
        Assert.Equal(a.Identity.Fingerprint, handler.LastPeer!.Fingerprint);
    }

    [Fact]
    public async Task ConnectingToKnownContact_WithDifferentKey_IsRejected()
    {
        using var a = new TestIdentity();
        using var b = new TestIdentity();
        using var impostor = new TestIdentity();
        var o = TestNet.Options();
        var policy = TestNet.LoopbackPolicy(o);
        await using var server = new TransportServer(o, policy, impostor.Identity.Certificate, () => Hello("Bea"), new EchoHandler());
        server.SyncListeners();

        var ex = await Assert.ThrowsAsync<IdentityMismatchException>(() => PeerClient.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, o.TcpPort), b.Identity.Fingerprint, a.Identity.Certificate, Hello("Ana"), policy, o,
            TestContext.Current.CancellationToken));
        Assert.Equal(impostor.Identity.Fingerprint, ex.Actual);
    }

    [Fact]
    public async Task Connecting_OutsideAuthorizedNetwork_IsRefusedBeforeAnyTraffic()
    {
        using var a = new TestIdentity();
        var o = TestNet.Options() with { AllowLoopback = false };
        var policy = TestNet.LoopbackPolicy(o);
        await Assert.ThrowsAsync<NetworkNotAllowedException>(() => PeerClient.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, o.TcpPort), null, a.Identity.Certificate, Hello("Ana"), policy, o,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IncompatibleProtocolVersion_IsReportedClearly()
    {
        using var a = new TestIdentity();
        using var b = new TestIdentity();
        var o = TestNet.Options();
        var policy = TestNet.LoopbackPolicy(o);
        await using var server = new TransportServer(o, policy, b.Identity.Certificate, () => Hello("Bea"), new EchoHandler());
        server.SyncListeners();

        await using var raw = await RawTls.ConnectAsync(o.TcpPort, a.Identity);
        await raw.SendAsync(new HelloPacket { MinVersion = 7, MaxVersion = 9, InstanceId = "x1", DisplayName = "Futuro", MachineName = "PC", ListenPort = 1 });
        var reply = await raw.ReadAsync();
        var err = Assert.IsType<ErrorPacket>(reply!.Packet);
        Assert.Equal(ErrorCodes.IncompatibleVersion, err.Code);
        Assert.Equal(ProtocolInfo.MaxVersion, err.MaxVersion);
    }

    [Fact]
    public async Task MalformedFrames_GetErrors_ThenConnectionCloses_ServerKeepsServing()
    {
        using var a = new TestIdentity();
        using var b = new TestIdentity();
        var o = TestNet.Options();
        var policy = TestNet.LoopbackPolicy(o);
        await using var server = new TransportServer(o, policy, b.Identity.Certificate, () => Hello("Bea"), new EchoHandler());
        server.SyncListeners();

        await using (var raw = await RawTls.ConnectAsync(o.TcpPort, a.Identity))
        {
            await raw.SendAsync(new HelloPacket { InstanceId = "x1", DisplayName = "Ana", MachineName = "PC", ListenPort = 1 });
            Assert.IsType<HelloOkPacket>((await raw.ReadAsync())!.Packet);
            // JSON roto
            await raw.SendRawAsync(Encoding.UTF8.GetBytes("{\"op\":\"msg\",\"mid\":"));
            Assert.Equal(ErrorCodes.Malformed, Assert.IsType<ErrorPacket>((await raw.ReadAsync())!.Packet).Code);
            // Operación desconocida
            await raw.SendRawAsync(Encoding.UTF8.GetBytes("{\"op\":\"format_c\",\"id\":\"a\"}"));
            Assert.Equal(ErrorCodes.Malformed, Assert.IsType<ErrorPacket>((await raw.ReadAsync())!.Packet).Code);
            // Campos inválidos (texto vacío)
            await raw.SendAsync(new MessagePacket { MessageId = Guid.NewGuid().ToString("N"), Text = "" });
            // tercer fallo: el servidor cierra la conexión
            var after = await raw.TryReadAsync();
            Assert.Null(after);
        }

        // El servidor sigue aceptando conexiones nuevas
        await using var client = await PeerClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, o.TcpPort), b.Identity.Fingerprint,
            a.Identity.Certificate, Hello("Ana"), policy, o, TestContext.Current.CancellationToken);
        Assert.Equal("Bea", client.Peer.DisplayName);
    }

    [Fact]
    public async Task OversizedFrameLength_ClosesConnection()
    {
        using var a = new TestIdentity();
        using var b = new TestIdentity();
        var o = TestNet.Options();
        await using var server = new TransportServer(o, TestNet.LoopbackPolicy(o), b.Identity.Certificate, () => Hello("Bea"), new EchoHandler());
        server.SyncListeners();
        await using var raw = await RawTls.ConnectAsync(o.TcpPort, a.Identity);
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x7FFFFFFF);
        await raw.Stream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        var reply = await raw.TryReadAsync();
        Assert.True(reply is null || reply.Packet is ErrorPacket);
        Assert.Null(await raw.TryReadAsync());
    }
}

/// <summary>Cliente TLS "en crudo" para enviar tramas arbitrarias en las pruebas.</summary>
public sealed class RawTls : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly FrameCodec _codec = new(1536 * 1024, 16 * 1024);
    public SslStream Stream { get; }

    private RawTls(TcpClient tcp, SslStream ssl) { _tcp = tcp; Stream = ssl; }

    public static async Task<RawTls> ConnectAsync(int port, LocalIdentity identity)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var ssl = new SslStream(tcp.GetStream());
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = TlsPolicy.TargetHost,
            ClientCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, null, offline: true),
            CertificateChainPolicy = TlsPolicy.OfflineChainPolicy(),
            RemoteCertificateValidationCallback = (_, c, _, e) => TlsPolicy.AcceptPeerCertificate(c, e) is not null,
        }, cts.Token);
        return new RawTls(tcp, ssl);
    }

    public Task SendAsync(PacketBase p) => _codec.WriteAsync(Stream, p, default, CancellationToken.None);

    public async Task SendRawAsync(byte[] header)
    {
        var buf = new byte[6 + header.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)(2 + header.Length));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)header.Length);
        header.CopyTo(buf, 6);
        await Stream.WriteAsync(buf);
    }

    public async Task<Frame?> ReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _codec.ReadAsync(Stream, cts.Token);
    }

    public async Task<Frame?> TryReadAsync()
    {
        try { return await ReadAsync(); }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _tcp.Dispose();
    }
}
