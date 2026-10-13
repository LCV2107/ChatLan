using System.Net;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Transport;

namespace MiniChat.Tests;

[Collection("engine")]
public class MessagingTests
{
    [Fact]
    public async Task Unicode_RoundTrips_Exactly()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        string[] samples =
        {
            "ñandú, pingüino, acción — «comillas»",
            "Emoji: 👩🏽‍💻🇪🇸❤️‍🔥 y ZWJ",
            "עברית ومرحبا بالعالم (RTL)",
            "Combinantes: é ä Z̵",
            "中文 日本語 한국어",
            "Línea 1\nLínea 2\n\n  sangría",
            new string('x', 3999) + "é",
        };
        var id = await ana.ContactIdAsync(bea);
        long last = 0;
        foreach (var s in samples) last = (await ana.Engine.SendTextAsync(id, s)).Value!.Id;
        await Wait.Status(ana, last, MessageStatus.Delivered);
        var received = await bea.MessagesWithAsync(ana);
        Assert.Equal(samples, received.Select(m => m.Text));
        Assert.False((await ana.Engine.SendTextAsync(id, new string('y', 4001))).Success);
        Assert.False((await ana.Engine.SendTextAsync(id, "   \n ")).Success);
    }

    [Fact]
    public async Task OfflineRecipient_MessagesFail_ManualRetryDeliversInOrder()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        await bea.StopAsync();

        var m1 = await ana.Engine.SendTextAsync(id, "uno");
        await Wait.Status(ana, m1.Value!.Id, MessageStatus.Failed);
        var m2 = await ana.Engine.SendTextAsync(id, "dos");
        var m3 = await ana.Engine.SendTextAsync(id, "tres");
        await Task.Delay(500);
        // Los posteriores esperan detrás del fallido (no se adelantan)
        Assert.Equal(MessageStatus.Pending, (await ana.Engine.GetMessageAsync(m2.Value!.Id))!.Status);
        Assert.Equal(MessageStatus.Pending, (await ana.Engine.GetMessageAsync(m3.Value!.Id))!.Status);
        Assert.NotNull((await ana.Engine.GetMessageAsync(m1.Value.Id))!.Error);

        await bea.OpenAsync();
        await Wait.Discovered(ana, bea);
        Assert.True((await ana.Engine.RetryAsync(id)).Success);
        await Wait.Status(ana, m3.Value!.Id, MessageStatus.Delivered);
        Assert.Equal(new[] { "uno", "dos", "tres" }, (await bea.MessagesWithAsync(ana)).Select(m => m.Text));
    }

    [Fact]
    public async Task LostAck_Retry_DoesNotDuplicate()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var uid = Guid.NewGuid().ToString("N");
        Assert.Equal(MessageAckStatuses.Stored, (await RelationshipTests.RawSendAsync(ana, bea, "una vez", uid)).Status);
        Assert.Equal(MessageAckStatuses.Duplicate, (await RelationshipTests.RawSendAsync(ana, bea, "una vez", uid)).Status);
        Assert.Single(await bea.MessagesWithAsync(ana));
        Assert.Equal(1, bea.NoticeCount(NoticeKind.NewMessage));
    }

    [Fact]
    public async Task NoAckFromReceiver_IsShownAsUnconfirmed_ThenRetryBecomesDelivered()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        int beaPort = bea.Options.TcpPort;
        await bea.StopAsync();

        // Un "agujero negro" con la identidad de Bea: acepta la conexión TLS, pero nunca confirma.
        var bh = new BlackHoleHandler();
        await using (var server = new TransportServer(bea.Options, bea.Engine.Policy, IdentityFor(bea).Certificate,
                         () => new LocalHello("bh", "Bea", "PC-Bea", beaPort, "t"), bh))
        {
            server.SyncListeners();
            var m = await ana.Engine.SendTextAsync(id, "¿llegó?");
            await Wait.Status(ana, m.Value!.Id, MessageStatus.Unconfirmed, 15000);
            Assert.Contains("confirmación", (await ana.Engine.GetMessageAsync(m.Value.Id))!.Error);
        }

        await bea.OpenAsync();
        await Wait.Discovered(ana, bea);
        await ana.Engine.RetryAsync(id);
        var msgs = await ana.MessagesWithAsync(bea);
        await Wait.Status(ana, msgs[^1].Id, MessageStatus.Delivered);
        Assert.Single(await bea.MessagesWithAsync(ana));
    }

    private static Core.Identity.LocalIdentity IdentityFor(TestPeer p) => Core.Identity.LocalIdentity.OpenOrCreate(p.Engine.Identity.KeyName);

    private sealed class BlackHoleHandler : IInboundHandler
    {
        public Task OnPeerHelloAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        public async Task<Reply> HandleAsync(PeerInfo peer, Frame frame, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task SenderClosedBeforeAck_IsRecoveredAsUnconfirmed_OnRestart()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        // Simulamos el estado que deja un cierre abrupto: "enviando" en la base de datos.
        var m = await ana.Engine.SendTextAsync(id, "en vuelo");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered);
        ana.Engine.Db.Write((c, tx) => MessagesRepo.SetStatus(c, tx, m.Value.Id, MessageStatus.Sending, null));
        var queued = ana.Engine.Db.Write((c, tx) => MessagesRepo.InsertOutgoing(c, tx, MessagesRepo.GetConversation(c, id, tx)!.Id, MessageKind.Text, "en cola", null));

        await ana.RestartAsync();
        Assert.Equal(MessageStatus.Unconfirmed, (await ana.Engine.GetMessageAsync(m.Value.Id))!.Status);
        Assert.Equal(MessageStatus.Failed, (await ana.Engine.GetMessageAsync(queued.Id))!.Status);
        await Wait.Discovered(ana, bea);
        await ana.Engine.RetryAsync(id);
        await Wait.Status(ana, queued.Id, MessageStatus.Delivered);
        await Wait.Status(ana, m.Value.Id, MessageStatus.Delivered); // duplicado en Bea → entregado
        Assert.Equal(new[] { "en vuelo", "en cola" }, (await bea.MessagesWithAsync(ana)).Select(x => x.Text));
    }

    [Fact]
    public async Task StorageErrorOnReceiver_IsReportedAsFailed_NotDelivered()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        bea.Engine.Db.Write((c, tx) => c.Exec("CREATE TRIGGER fail_insert BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT, 'disco lleno simulado'); END;", tx));
        var m = await ana.Engine.SendTextAsync(await ana.ContactIdAsync(bea), "no cabe");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Failed);
        Assert.Contains("almacenamiento", (await ana.Engine.GetMessageAsync(m.Value.Id))!.Error);
        bea.Engine.Db.Write((c, tx) => c.Exec("DROP TRIGGER fail_insert;", tx));
        await ana.Engine.RetryAsync(await ana.ContactIdAsync(bea));
        await Wait.Status(ana, m.Value.Id, MessageStatus.Delivered);
    }

    [Fact]
    public async Task History_IsPaged_InStableOrder()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        long last = 0;
        for (int i = 1; i <= 120; i++) last = (await ana.Engine.SendTextAsync(id, $"m{i}")).Value!.Id;
        await Wait.Status(ana, last, MessageStatus.Delivered, 30000);
        var beaId = await bea.ContactIdAsync(ana);
        var page1 = await bea.Engine.GetMessagesAsync(beaId, null, 50);
        var page2 = await bea.Engine.GetMessagesAsync(beaId, page1[0].Id, 50);
        var page3 = await bea.Engine.GetMessagesAsync(beaId, page2[0].Id, 50);
        var all = page3.Concat(page2).Concat(page1).Select(m => m.Text).ToList();
        Assert.Equal(Enumerable.Range(1, 120).Select(i => $"m{i}"), all);
        Assert.Equal(20, page3.Count);
    }

    [Fact]
    public async Task Export_And_ClearHistory_AreLocalOnly()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        var png = StickerTests.MakePng(64, 32, transparent: true);
        var file = Path.Combine(ana.Dir, "s.png");
        await File.WriteAllBytesAsync(file, png);
        var imported = await ana.Engine.Stickers.ImportAsync(new[] { file });
        var m1 = await ana.Engine.SendTextAsync(id, "texto ñ");
        var m2 = await ana.Engine.SendStickerAsync(id, imported[0].Hash!);
        await Wait.Status(ana, m2.Value!.Id, MessageStatus.Delivered);

        var export = Path.Combine(ana.Dir, "export", "chat.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        var r = await ana.Engine.ExportAsync(id, export);
        var text = await File.ReadAllTextAsync(export);
        Assert.Contains("Ana: texto ñ", text);
        Assert.Contains($"[Sticker: {imported[0].Hash![..12].ToLowerInvariant()}.png (64×32)]", text);
        Assert.True(File.Exists(Path.Combine(r.StickerFolder!, imported[0].Hash![..12].ToLowerInvariant() + ".png")));

        Assert.Equal(2, await ana.Engine.ClearHistoryAsync(id));
        Assert.Empty(await ana.MessagesWithAsync(bea));
        Assert.Equal(2, (await bea.MessagesWithAsync(ana)).Count); // la copia del compañero no cambia
        Assert.Equal(Relation.Authorized, await ana.RelationWithAsync(bea));
    }

    [Fact]
    public async Task Drafts_PersistPerConversation()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        await ana.Engine.SaveDraftAsync(id, "borrador a medias 😀");
        await ana.RestartAsync(network: false);
        Assert.Equal("borrador a medias 😀", await ana.Engine.GetDraftAsync(id));
    }
}
