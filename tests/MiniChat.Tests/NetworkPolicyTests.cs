using System.Net;
using System.Net.NetworkInformation;
using MiniChat.Core.Config;
using MiniChat.Core.Network;

namespace MiniChat.Tests;

public class NetworkPolicyTests
{
    private sealed class FakeInventory(params LocalInterface[] items) : INetworkInventory
    {
        public IReadOnlyList<LocalInterface> GetInterfaces() => items;
    }

    private static LocalInterface Iface(string ip, int prefix, NetworkCategory cat, bool virt = false, string id = "{A}", string net = "net1") =>
        new(id, "Ethernet", "Intel", IPAddress.Parse(ip), prefix, NetworkInterfaceType.Ethernet, cat, net, "Red", virt);

    [Fact]
    public void PrivateNetwork_IsAllowed_AndOnlyItsSubnet()
    {
        var p = new NetworkPolicy(new FakeInventory(Iface("192.168.1.10", 24, NetworkCategory.Private)), new MiniChatOptions());
        p.Refresh();
        Assert.NotNull(p.InterfaceFor(IPAddress.Parse("192.168.1.77")));
        // Otra IP privada que NO es de la subred autorizada
        Assert.Null(p.InterfaceFor(IPAddress.Parse("192.168.2.77")));
        Assert.Null(p.InterfaceFor(IPAddress.Parse("10.0.0.5")));
        Assert.Contains("no pertenece", p.ExplainRejection(IPAddress.Parse("10.0.0.5")));
    }

    [Fact]
    public void PublicNetwork_IsDeniedByDefault_ButCanBeTrustedExplicitly()
    {
        var i = Iface("10.10.160.112", 24, NetworkCategory.Public);
        var p = new NetworkPolicy(new FakeInventory(i), new MiniChatOptions());
        p.Refresh();
        Assert.Null(p.InterfaceFor(IPAddress.Parse("10.10.160.5")));
        Assert.False(p.IsAllowedInbound(i.Address, IPAddress.Parse("10.10.160.5")));
        p.SetUserLists(new[] { i.TrustKey }, Array.Empty<string>());
        Assert.NotNull(p.InterfaceFor(IPAddress.Parse("10.10.160.5")));
        Assert.True(p.IsAllowedInbound(i.Address, IPAddress.Parse("10.10.160.5")));
        Assert.False(p.IsAllowedInbound(i.Address, IPAddress.Parse("10.10.161.5")));
    }

    [Fact]
    public void VpnLookingAdapter_IsDeniedEvenIfPrivate_AndDenyListWins()
    {
        var vpn = Iface("172.16.0.2", 16, NetworkCategory.Private, virt: true, id: "{V}", net: "vpn");
        var lan = Iface("192.168.1.10", 24, NetworkCategory.Private);
        var p = new NetworkPolicy(new FakeInventory(vpn, lan), new MiniChatOptions());
        p.Refresh();
        Assert.Null(p.InterfaceFor(IPAddress.Parse("172.16.5.5")));
        p.SetUserLists(Array.Empty<string>(), new[] { lan.TrustKey });
        Assert.Null(p.InterfaceFor(IPAddress.Parse("192.168.1.20")));
    }

    [Fact]
    public void Loopback_OnlyWithTestOption()
    {
        var lo = new LocalInterface("lo", "Loopback", "", IPAddress.Loopback, 8, NetworkInterfaceType.Loopback, NetworkCategory.Private, "lo", "", false);
        var p = new NetworkPolicy(new FakeInventory(lo), new MiniChatOptions());
        p.Refresh();
        Assert.Null(p.InterfaceFor(IPAddress.Loopback));
        var t = new NetworkPolicy(new FakeInventory(lo), new MiniChatOptions { AllowLoopback = true });
        t.Refresh();
        Assert.NotNull(t.InterfaceFor(IPAddress.Loopback));
    }

    [Fact]
    public void RealWindowsInventory_ReadsInterfacesWithoutThrowing()
    {
        var list = new WindowsNetworkInventory().GetInterfaces();
        foreach (var i in list)
            TestContext.Current.TestOutputHelper?.WriteLine($"{i.Name} {i.SubnetText} {i.Category} virt={i.LooksVirtualOrVpn} net={i.NetworkName}");
        Assert.All(list, i => Assert.False(i.Address.ToString().StartsWith("169.254.")));
    }
}
