using System.Net;
using System.Text;
using MiniChat.Core.Config;
using MiniChat.Core.Discovery;
using MiniChat.Core.Network;

namespace MiniChat.Tests;

public class DiscoveryTests
{
    private static string Fp(char c) => new(c, 64);

    private static DiscoveryService Create(MiniChatOptions o, NetworkPolicy p, string name, string fp, int port) =>
        new(o, p, () => new Announcement { InstanceId = "i" + name, Fingerprint = fp, DisplayName = name, MachineName = "PC-" + name, Port = port });

    [Fact]
    public async Task TwoInstances_FindEachOther_OverMulticastLoopback()
    {
        var o = TestNet.Options() with { AnnounceInterval = TimeSpan.FromMilliseconds(300), AnnounceJitter = TimeSpan.Zero };
        var policy = TestNet.LoopbackPolicy(o);
        await using var a = Create(o, policy, "Ana", Fp('A'), 1111);
        await using var b = Create(o, policy, "Bea", Fp('B'), 2222);
        var seen = new TaskCompletionSource<DiscoveredPeer>();
        a.PeerSeen += (_, p) => { if (p.Fingerprint == Fp('B')) seen.TrySetResult(p); };
        a.Start();
        b.Start();
        var peer = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("Bea", peer.DisplayName);
        Assert.Equal(2222, peer.Endpoint.Port);
        Assert.True(peer.CompatibleVersion);
        Assert.DoesNotContain(a.Peers, x => x.Fingerprint == Fp('A')); // no se descubre a sí mismo
    }

    [Fact]
    public async Task Presence_ExpiresWhenAnnouncementsStop_AndByeRemovesImmediately()
    {
        var o = TestNet.Options() with
        {
            AnnounceInterval = TimeSpan.FromMilliseconds(200), AnnounceJitter = TimeSpan.Zero,
            PresenceTimeout = TimeSpan.FromMilliseconds(800), PresenceSweepInterval = TimeSpan.FromMilliseconds(100),
        };
        var policy = TestNet.LoopbackPolicy(o);
        await using var a = Create(o, policy, "Ana", Fp('A'), 1111);
        var b = Create(o, policy, "Bea", Fp('B'), 2222);
        var lost = new TaskCompletionSource();
        a.PeerLost += (_, fp) => { if (fp == Fp('B')) lost.TrySetResult(); };
        a.Start();
        b.Start();
        await WaitUntil(() => a.IsAvailable(Fp('B')));
        await b.DisposeAsync(); // envía "bye"
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(a.IsAvailable(Fp('B')));
    }

    [Fact]
    public void MalformedOrForeignDatagrams_AreIgnored_WithoutThrowing()
    {
        var o = TestNet.Options();
        var policy = TestNet.LoopbackPolicy(o);
        var a = Create(o, policy, "Ana", Fp('A'), 1111);
        var iface = policy.AllowedInterfaces[0];
        var remote = new IPEndPoint(IPAddress.Loopback, 5000);
        string[] junk =
        {
            "", "{", "null", "[1,2,3]", "{\"p\":\"otro\",\"k\":\"ann\"}",
            "{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x\",\"fp\":\"no-hex\",\"dn\":\"a\",\"pc\":\"b\",\"port\":1}",
            "{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x\",\"fp\":\"" + Fp('C') + "\",\"dn\":\"\",\"pc\":\"b\",\"port\":1}",
            "{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x\",\"fp\":\"" + Fp('C') + "\",\"dn\":\"a\",\"pc\":\"b\",\"port\":70000}",
            new string('x', 5000),
        };
        foreach (var j in junk) a.Process(iface, remote, Encoding.UTF8.GetBytes(j));
        Assert.Empty(a.Peers);
        // Un anuncio válido sí se procesa
        a.Process(iface, remote, Encoding.UTF8.GetBytes("{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x\",\"fp\":\"" + Fp('C') + "\",\"dn\":\"Caro\",\"pc\":\"PC-1\",\"port\":4000,\"min\":1,\"max\":1}"));
        Assert.Single(a.Peers);
    }

    [Fact]
    public void Datagram_FromOutsideInterfaceSubnet_IsIgnored()
    {
        var o = TestNet.Options();
        var lan = new LocalInterface("e", "Ethernet", "", IPAddress.Parse("192.168.1.10"), 24, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, NetworkCategory.Private, "n", "", false);
        var policy = new NetworkPolicy(new Fixed(lan), o);
        policy.Refresh();
        var a = Create(o, policy, "Ana", Fp('A'), 1111);
        var ann = Encoding.UTF8.GetBytes("{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x\",\"fp\":\"" + Fp('C') + "\",\"dn\":\"Caro\",\"pc\":\"PC-1\",\"port\":4000}");
        a.Process(lan, new IPEndPoint(IPAddress.Parse("10.9.9.9"), 47820), ann);
        Assert.Empty(a.Peers);
        a.Process(lan, new IPEndPoint(IPAddress.Parse("192.168.1.50"), 47820), ann);
        Assert.Single(a.Peers);
    }

    [Fact]
    public void RateLimit_DropsFloods()
    {
        var o = TestNet.Options() with { MaxDatagramsPerSourcePerSecond = 5 };
        var policy = TestNet.LoopbackPolicy(o);
        var a = Create(o, policy, "Ana", Fp('A'), 1111);
        var iface = policy.AllowedInterfaces[0];
        for (int i = 0; i < 200; i++)
        {
            var fp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));
            a.Process(iface, new IPEndPoint(IPAddress.Loopback, 5000), Encoding.UTF8.GetBytes(
                "{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x\",\"fp\":\"" + fp + "\",\"dn\":\"n\",\"pc\":\"p\",\"port\":4000}"));
        }
        Assert.True(a.Peers.Count <= 5 + 1, $"procesados {a.Peers.Count}");
    }

    private sealed class Fixed(params LocalInterface[] items) : INetworkInventory
    {
        public IReadOnlyList<LocalInterface> GetInterfaces() => items;
    }

    internal static async Task WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(50);
        }
    }
}
