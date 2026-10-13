using System.Net;
using System.Net.NetworkInformation;
using MiniChat.Core.Config;
using MiniChat.Core.Engine;
using MiniChat.Core.Network;

namespace MiniChat.Tests;

[Collection("engine")]
public class NetworkDiagnosticsTests
{
    private sealed class FakeInventory(bool? internet, string[] unconfigured, params LocalInterface[] items) : INetworkInventory
    {
        public IReadOnlyList<LocalInterface> GetInterfaces() => items;
        public IReadOnlyList<string> GetUnconfiguredAdapters() => unconfigured;
        public bool? IsInternetAvailable() => internet;
    }

    private static LocalInterface Lan(string ip, NetworkCategory cat, string name = "Ethernet") =>
        new("{" + name + "}", name, "Intel", IPAddress.Parse(ip), 24, NetworkInterfaceType.Ethernet, cat, "net-" + name, "Oficina", false);

    private static async Task<IReadOnlyList<NetworkFinding>> DiagnoseAsync(INetworkInventory inventory)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "diag-" + Guid.NewGuid().ToString("N")[..8]);
        var engine = ChatEngine.Open(new AppPaths(dir), new MiniChatOptions(), inventory);
        try { return await engine.DiagnoseNetworkAsync(); }
        finally
        {
            var key = engine.Identity.KeyName;
            await engine.DisposeAsync();
            Core.Identity.LocalIdentity.Delete(key);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task CommonProblems_AreDetected_WithAFix()
    {
        var findings = await DiagnoseAsync(new FakeInventory(false, new[] { "Wi-Fi" },
            Lan("192.168.1.10", NetworkCategory.Public) with { HasGateway = false, IsDhcp = false },
            Lan("10.0.0.5", NetworkCategory.Private, "Ethernet 2") with { IsDuplicate = true }));

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Title.Contains("IP duplicada"));
        Assert.Contains(findings, f => f.Severity == FindingSeverity.Warning && f.Title.Contains("puerta de enlace") && f.Detail.Contains("IP fija"));
        Assert.Contains(findings, f => f.Severity == FindingSeverity.Warning && f.Title.Contains("Pública"));
        Assert.Contains(findings, f => f.Title.Contains("Wi-Fi") && f.Detail.Contains("169.254"));
        Assert.Contains(findings, f => f.Title == "Internet: no disponible" && f.Detail.Contains("no necesita Internet"));
        Assert.All(findings.Where(f => f.Severity >= FindingSeverity.Warning), f => Assert.False(string.IsNullOrWhiteSpace(f.Fix)));
        Assert.Equal(FindingSeverity.Error, findings[0].Severity); // ordenados por gravedad
    }

    [Fact]
    public async Task NoAuthorizedNetwork_IsAnError()
    {
        var findings = await DiagnoseAsync(new FakeInventory(null, Array.Empty<string>(), Lan("192.168.1.10", NetworkCategory.Public)));
        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Title == "Ninguna red autorizada");
    }

    [Fact]
    public async Task HealthyNetwork_ReportsOk()
    {
        var findings = await DiagnoseAsync(new FakeInventory(true, Array.Empty<string>(), Lan("192.168.1.10", NetworkCategory.Private)));
        Assert.Equal(FindingSeverity.Ok, findings[0].Severity);
        Assert.Contains("192.168.1.0/24", findings[0].Detail);
        Assert.DoesNotContain(findings, f => f.Severity >= FindingSeverity.Warning);
    }

    [Fact]
    public async Task ConnectionTest_MeasuresRealTlsRoundTrip_AndOfflineStatusCountsPeers()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var r = await ana.Engine.TestConnectionAsync(await ana.ContactIdAsync(bea));
        Assert.True(r.Success, r.Summary);
        Assert.NotNull(r.RoundTripMs);
        Assert.Contains("identidad verificada", r.Summary);
        Assert.StartsWith("TLS 1.", r.Tls);

        var status = ana.Engine.GetOfflineStatus();
        Assert.Equal(1, status.PeersOnline);
        Assert.Equal(1, status.ContactsOnline);

        // Intentos hacia Internet: rechazados y contados.
        await ana.Engine.ConnectManualAsync("8.8.8.8");
        Assert.True(ana.Engine.GetOfflineStatus().BlockedOutsideLan >= 1);

        await bea.StopAsync();
        var down = await ana.Engine.TestConnectionAsync(await ana.ContactIdAsync(bea));
        Assert.False(down.Success);
        Assert.False(string.IsNullOrWhiteSpace(down.Summary));
    }
}
