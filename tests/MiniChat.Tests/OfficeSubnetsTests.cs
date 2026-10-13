using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MiniChat.Core.Config;
using MiniChat.Core.Discovery;
using MiniChat.Core.Engine;
using MiniChat.Core.Identity;
using MiniChat.Core.Network;

namespace MiniChat.Tests;

/// <summary>Otras subredes de la oficina: validación, política, descubrimiento unicast y lista en el motor.</summary>
public class OfficeSubnetsTests
{
    /// <summary>Equipo en 10.10.160.0/24 cuyo router llega a todo 10.10.0.0/16 (como una oficina con VLAN por área).</summary>
    private sealed class OfficeInventory(IReadOnlyList<LocalInterface> items, Func<IPAddress, IPAddress?> route, params string[] admin) : INetworkInventory
    {
        public IReadOnlyList<LocalInterface> GetInterfaces() => items;
        public IPAddress? SourceAddressFor(IPAddress remote) => route(remote);
        public IReadOnlyList<string> GetAdminRemoteSubnets() => admin;
    }

    private static LocalInterface Lan(string ip = "10.10.160.112", int prefix = 24, NetworkCategory cat = NetworkCategory.Private, string id = "{E}") =>
        new(id, "Ethernet", "Intel", IPAddress.Parse(ip), prefix, NetworkInterfaceType.Ethernet, cat, "net-" + id, "Oficina", false);

    private static readonly SubnetRange Office16 = new(IPAddress.Parse("10.10.0.0"), 16);

    private static OfficeInventory Office(params string[] admin)
    {
        var lan = Lan();
        return new OfficeInventory(new[] { lan }, ip => Office16.Contains(ip) ? lan.Address : null, admin);
    }

    private static NetworkPolicy Policy(INetworkInventory inv, MiniChatOptions? o = null)
    {
        var p = new NetworkPolicy(inv, o ?? new MiniChatOptions());
        p.Refresh();
        return p;
    }

    private static SubnetRange R(string text, bool loopback = false) =>
        SubnetRange.ParseOrNull(text, loopback) ?? throw new ArgumentException(text);

    // ---------------- Validación de subredes ----------------

    [Theory]
    [InlineData("10.10.161.0/24", "10.10.161.0/24")]
    [InlineData(" 10.10.161.77/24 ", "10.10.161.0/24")]
    [InlineData("10.10.161.0/255.255.255.0", "10.10.161.0/24")]
    [InlineData("172.20.0.0/16", "172.20.0.0/16")]
    [InlineData("192.168.5.9/30", "192.168.5.8/30")]
    public void SubnetRange_AcceptsPrivateRanges_AndNormalizes(string input, string expected)
    {
        Assert.True(SubnetRange.TryParse(input, out var r, out var error), error);
        Assert.Equal(expected, r!.Text);
    }

    [Theory]
    [InlineData("8.8.8.0/24")]          // Internet
    [InlineData("172.32.0.0/16")]       // fuera de 172.16/12
    [InlineData("10.0.0.0/8")]          // demasiado grande
    [InlineData("10.10.161.0/31")]      // demasiado pequeña
    [InlineData("10.10.161.0")]         // sin máscara
    [InlineData("10.10.161/24")]
    [InlineData("10.10.161.0/255.0.255.0")]
    [InlineData("127.0.0.0/24")]
    [InlineData("")]
    public void SubnetRange_RejectsInvalidOrPublic(string input)
    {
        Assert.False(SubnetRange.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void SubnetRange_Hosts_ExcludeNetworkAndBroadcast()
    {
        var hosts = R("192.168.5.8/29").Hosts().Select(h => h.ToString()).ToList();
        Assert.Equal(new[] { "192.168.5.9", "192.168.5.10", "192.168.5.11", "192.168.5.12", "192.168.5.13", "192.168.5.14" }, hosts);
        Assert.Equal(254, R("10.10.161.0/24").HostCount);
        Assert.True(R("10.10.160.0/22").Overlaps(R("10.10.161.0/24")));
        Assert.False(R("10.10.160.0/24").Overlaps(R("10.10.161.0/24")));
    }

    // ---------------- Política ----------------

    [Fact]
    public void OtherSubnet_IsRejected_UntilAuthorized_ThenRoutedThroughTheLan()
    {
        var p = Policy(Office());
        var lan = p.AllowedInterfaces.Single();
        var peer = IPAddress.Parse("10.10.161.25");

        Assert.Null(p.InterfaceFor(peer));
        Assert.False(p.IsAllowedInbound(lan.Address, peer));
        Assert.Contains("Otras subredes", p.ExplainRejection(peer));

        p.SetRemoteSubnets(new[] { R("10.10.161.0/24") });
        Assert.Equal(lan, p.InterfaceFor(peer));
        Assert.True(p.IsAllowedInbound(lan.Address, peer));
        // Solo la subred autorizada, no el resto de la oficina ni Internet.
        Assert.Null(p.InterfaceFor(IPAddress.Parse("10.10.162.25")));
        Assert.False(p.IsAllowedInbound(lan.Address, IPAddress.Parse("10.10.162.25")));
        Assert.Contains("Internet", p.ExplainRejection(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void OtherSubnet_ReachedThroughUnauthorizedInterface_IsRejected()
    {
        // La ruta hacia 10.10.161.0/24 sale por un adaptador de red Pública (no autorizado).
        var lan = Lan();
        var pub = Lan("192.168.50.4", 24, NetworkCategory.Public, "{W}");
        var p = Policy(new OfficeInventory(new[] { lan, pub }, _ => pub.Address));
        p.SetRemoteSubnets(new[] { R("10.10.161.0/24") });
        Assert.Null(p.InterfaceFor(IPAddress.Parse("10.10.161.25")));
        Assert.Contains("no es de una red autorizada", p.ExplainRejection(IPAddress.Parse("10.10.161.25")));
    }

    [Fact]
    public void RemoteSubnet_CannotOverrideADeniedAttachedNetwork()
    {
        var pub = Lan("192.168.50.4", 24, NetworkCategory.Public, "{W}");
        var p = Policy(new OfficeInventory(new[] { Lan(), pub }, ip => ip.ToString().StartsWith("192.168.50.") ? pub.Address : null));
        p.SetRemoteSubnets(new[] { R("192.168.50.0/24") });
        Assert.Null(p.InterfaceFor(IPAddress.Parse("192.168.50.9")));
        Assert.False(p.IsAllowedInbound(Lan().Address, IPAddress.Parse("192.168.50.9")));
    }

    // ---------------- Descubrimiento ----------------

    private static string Fp(char c) => new(c, 64);

    private static byte[] Ann(char fp, string name, int? u = null) => Encoding.UTF8.GetBytes(
        "{\"p\":\"mclan\",\"k\":\"ann\",\"iid\":\"x" + fp + "\",\"fp\":\"" + Fp(fp) + "\",\"dn\":\"" + name + "\",\"pc\":\"PC\",\"port\":47821,\"min\":1,\"max\":2"
        + (u is null ? "" : ",\"u\":" + u) + "}");

    [Fact]
    public void Announcement_FromAuthorizedOtherSubnet_IsAccepted_AndMarked()
    {
        var o = new MiniChatOptions();
        var p = Policy(Office(), o);
        var lan = p.AllowedInterfaces.Single();
        var d = new DiscoveryService(o, p, () => new Announcement { InstanceId = "me", Fingerprint = Fp('A'), DisplayName = "Ana", MachineName = "PC-A", Port = 47821 });

        d.Process(lan, new IPEndPoint(IPAddress.Parse("10.10.161.25"), 47820), Ann('C', "Caro"));
        Assert.Empty(d.Peers);                                            // aún no autorizada

        p.SetRemoteSubnets(new[] { R("10.10.161.0/24") });
        d.Process(lan, new IPEndPoint(IPAddress.Parse("10.10.161.25"), 47820), Ann('C', "Caro"));
        d.Process(lan, new IPEndPoint(IPAddress.Parse("10.10.160.30"), 47820), Ann('D', "Dani"));
        d.Process(lan, new IPEndPoint(IPAddress.Parse("10.10.162.9"), 47820), Ann('E', "Eva")); // no autorizada

        var peers = d.Peers.ToDictionary(x => x.DisplayName);
        Assert.Equal(2, peers.Count);
        Assert.True(peers["Caro"].FromOtherSubnet);
        Assert.False(peers["Dani"].FromOtherSubnet);
    }

    [Fact]
    public void SweepTargets_PriorityFirst_SkipOwnSubnetAndDuplicates()
    {
        var p = Policy(Office());
        p.SetRemoteSubnets(new[] { R("10.10.161.0/29"), R("10.10.170.0/30") });
        var priority = new[] { IPAddress.Parse("10.10.170.2"), IPAddress.Parse("10.10.160.40"), IPAddress.Parse("10.10.99.1") };
        var targets = DiscoveryService.SweepTargets(priority, p).Select(x => x.ToString()).ToList();

        Assert.Equal("10.10.170.2", targets[0]);                          // último contacto conocido, primero
        Assert.DoesNotContain("10.10.160.40", targets);                   // su propia subred: basta el multicast
        Assert.DoesNotContain("10.10.99.1", targets);                     // no autorizada
        Assert.DoesNotContain("10.10.161.0", targets);                    // dirección de red
        Assert.DoesNotContain("10.10.161.7", targets);                    // difusión
        Assert.Equal(targets.Count, targets.Distinct().Count());
        Assert.Equal(6 + 2, targets.Count);
    }

    /// <summary>Equipo de loopback con prefijo /32, para poder usar 127.0.0.x como «otras» direcciones.</summary>
    private sealed class SingleLoopback(string ip) : INetworkInventory
    {
        public IReadOnlyList<LocalInterface> GetInterfaces() => new[]
        {
            new LocalInterface("lo", "Loopback", "", IPAddress.Parse(ip), 32, NetworkInterfaceType.Loopback, NetworkCategory.Private, "lo", "", false),
        };
    }

    [Fact]
    public async Task UnicastProbe_IsAnsweredDirectly_WithRateLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var o = TestNet.Options();
        var p = Policy(new SingleLoopback("127.0.0.1"), o);
        await using var d = new DiscoveryService(o, p, () => new Announcement { InstanceId = "me", Fingerprint = Fp('A'), DisplayName = "Ana", MachineName = "PC-A", Port = 47821 });
        d.Start();

        using var asker = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 0));
        var probe = Encoding.UTF8.GetBytes("{\"p\":\"mclan\",\"k\":\"qry\",\"iid\":\"otro\",\"u\":1}");
        await asker.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, o.DiscoveryPort), ct);
        var reply = await asker.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
        var text = Encoding.UTF8.GetString(reply.Buffer);
        Assert.Contains("\"k\":\"ann\"", text);
        Assert.Contains(Fp('A'), text);
        Assert.Equal(o.DiscoveryPort, reply.RemoteEndPoint.Port);

        // Un segundo sondeo inmediato del mismo origen no recibe respuesta (límite por origen).
        await asker.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, o.DiscoveryPort), ct);
        var second = asker.ReceiveAsync(ct).AsTask();
        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(800, ct)));
    }

    [Fact]
    public async Task Sweep_SendsNamelessProbes_ToEveryAddressOfAuthorizedSubnet()
    {
        var ct = TestContext.Current.CancellationToken;
        var o = TestNet.Options() with { RemoteProbesPerSecond = 200 };
        var p = Policy(new SingleLoopback("127.0.0.1"), o);
        p.SetRemoteSubnets(new[] { R("127.0.0.0/29", loopback: true) });
        await using var d = new DiscoveryService(o, p, () => new Announcement { InstanceId = "me", Fingerprint = Fp('A'), DisplayName = "Ana Secreta", MachineName = "PC-A", Port = 47821 });

        using var listener = new UdpClient();
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Client.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), o.DiscoveryPort));
        d.Start();

        var got = await listener.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        var text = Encoding.UTF8.GetString(got.Buffer);
        Assert.Contains("\"k\":\"qry\"", text);
        Assert.Contains("\"u\":1", text);
        Assert.DoesNotContain("Ana Secreta", text);   // el sondeo no lleva nombres
        Assert.DoesNotContain(Fp('A'), text);
        await DiscoveryTests.WaitUntil(() => d.SweepStatus.LastFinished is not null);
        Assert.Equal(5, d.SweepStatus.Sent);                // .2 a .6: la propia (.1), la de red y la de difusión no
    }

    // ---------------- Firewall ----------------

    [Theory]
    [InlineData("*", true)]
    [InlineData("LocalSubnet", false)]
    [InlineData("10.10.161.0/255.255.255.0", true)]
    [InlineData("10.10.161.0/24", true)]
    [InlineData("10.10.160.0/255.255.252.0", true)]
    [InlineData("10.10.160.0-10.10.161.255", true)]
    [InlineData("10.10.161.0/255.255.255.128", false)]
    [InlineData("LocalSubnet,10.10.162.0/24", false)]
    [InlineData("LocalSubnet,10.10.162.0/24,10.10.161.0/24", true)]
    public void Firewall_RemoteAddressCoverage(string remote, bool covers)
    {
        var rule = new FirewallRuleInfo("r", true, true, FirewallProfiles.Private, FirewallInspector.ProtocolTcp, "47821", true) { RemoteAddresses = remote };
        Assert.Equal(covers, FirewallInspector.CoversRemote(rule, R("10.10.161.0/24")));
    }

    // ---------------- Motor: lista persistente ----------------

    [Fact]
    public async Task Engine_AddRemoveSubnets_ValidatesPersists_AndRespectsAdminPolicy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "subnets-" + Guid.NewGuid().ToString("N")[..8]);
        var inv = Office("10.10.170.0/24", "no-valida");
        string? key = null;
        try
        {
            var e = ChatEngine.Open(new AppPaths(dir), new MiniChatOptions(), inv);
            key = e.Identity.KeyName;
            Assert.False((await e.AddRemoteSubnetAsync("10.10.160.0/23")).Success);   // incluye su propia red
            Assert.False((await e.AddRemoteSubnetAsync("8.8.8.0/24")).Success);
            Assert.True((await e.AddRemoteSubnetAsync("10.10.161.0/24")).Success);
            Assert.False((await e.AddRemoteSubnetAsync("10.10.161.128/25")).Success); // solapa con la anterior
            Assert.False((await e.AddRemoteSubnetAsync("10.10.170.0/24")).Success);   // ya la define el administrador

            var list = e.GetRemoteSubnets().ToDictionary(v => v.Subnet);
            Assert.Equal(2, list.Count);
            Assert.True(list["10.10.170.0/24"].FromAdministrator);
            Assert.NotNull(list["10.10.161.0/24"].Route);
            Assert.False((await e.RemoveRemoteSubnetAsync("10.10.170.0/24")).Success);
            Assert.Contains((await e.DiagnoseNetworkAsync()), f => f.Title.Contains("administrador no válida"));
            await e.DisposeAsync();

            var again = ChatEngine.Open(new AppPaths(dir), new MiniChatOptions(), inv);
            Assert.True(again.Policy.IsInRemoteSubnet(IPAddress.Parse("10.10.161.9")));
            Assert.True((await again.RemoveRemoteSubnetAsync("10.10.161.0/24")).Success);
            Assert.False(again.Policy.IsInRemoteSubnet(IPAddress.Parse("10.10.161.9")));
            Assert.True(again.Policy.IsInRemoteSubnet(IPAddress.Parse("10.10.170.9")));
            await again.DisposeAsync();
        }
        finally
        {
            if (key is not null) LocalIdentity.Delete(key);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ProbeAddress_RejectsInternetAndInvalid_WithoutTouchingTheNetwork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "probe-" + Guid.NewGuid().ToString("N")[..8]);
        string? key = null;
        try
        {
            var e = ChatEngine.Open(new AppPaths(dir), new MiniChatOptions(), Office());
            key = e.Identity.KeyName;
            var r1 = await e.ProbeAddressAsync("8.8.8.8", TestContext.Current.CancellationToken);
            Assert.Empty(r1.Steps);
            Assert.Equal(FindingSeverity.Error, r1.Verdict.Severity);
            var r2 = await e.ProbeAddressAsync("10.10.161", TestContext.Current.CancellationToken);
            Assert.Empty(r2.Steps);
            var r3 = await e.ProbeAddressAsync("10.10.160.112", TestContext.Current.CancellationToken);
            Assert.Equal("Es este equipo", r3.Verdict.Title);
            await e.DisposeAsync();
        }
        finally
        {
            if (key is not null) LocalIdentity.Delete(key);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
