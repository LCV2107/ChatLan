using System.Text;
using Microsoft.Data.Sqlite;
using MiniChat.Core.Config;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Stickers;

namespace MiniChat.Tests;

[Collection("engine")]
public class EncryptionAtRestTests
{
    /// <summary>Lee el archivo de base de datos y su WAL tal como los vería alguien que copia la carpeta.</summary>
    private static byte[] RawDatabaseBytes(TestPeer p)
    {
        var parts = new List<byte>();
        foreach (var f in new[] { p.Engine.Paths.DatabaseFile, p.Engine.Paths.DatabaseFile + "-wal" })
        {
            if (!File.Exists(f)) continue;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            parts.AddRange(buf);
        }
        return parts.ToArray();
    }

    /// <summary>Lee un archivo aunque SQLite lo tenga abierto (como haría alguien que copia la carpeta).</summary>
    private static byte[] SharedRead(string path)
    {
        if (!File.Exists(path)) return Array.Empty<byte>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    private static bool Contains(byte[] haystack, string text) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

    [Fact]
    public async Task MessagesNamesAndDrafts_AreNotReadableInTheDatabaseFile()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        var m = await ana.Engine.SendTextAsync(id, "TEXTO-SECRETO-ÑANDÚ enviado");
        var r = await bea.Engine.SendTextAsync(await bea.ContactIdAsync(ana), "RESPUESTA-CONFIDENCIAL recibida");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered);
        await Wait.Status(bea, r.Value!.Id, MessageStatus.Delivered);
        await ana.Engine.SaveDraftAsync(id, "BORRADOR-PRIVADO a medias");

        // La aplicación los lee sin problema...
        Assert.Contains(await ana.MessagesWithAsync(bea), x => x.Text == "RESPUESTA-CONFIDENCIAL recibida");
        Assert.Equal("BORRADOR-PRIVADO a medias", await ana.Engine.GetDraftAsync(id));

        // ...pero en el archivo no aparecen ni los textos ni el nombre del PC del contacto.
        foreach (var peer in new[] { ana, bea })
        {
            var raw = RawDatabaseBytes(peer);
            Assert.False(Contains(raw, "TEXTO-SECRETO"), $"texto enviado en claro en {peer.Name}");
            Assert.False(Contains(raw, "RESPUESTA-CONFIDENCIAL"), $"texto recibido en claro en {peer.Name}");
            Assert.False(Contains(raw, "BORRADOR-PRIVADO"), $"borrador en claro en {peer.Name}");
        }
        Assert.False(Contains(RawDatabaseBytes(ana), "PC-Bea"));
        Assert.False(Contains(RawDatabaseBytes(bea), "PC-Ana"));
    }

    [Fact]
    public async Task LockedApp_StillReceivesAndStores_ButCannotReadUntilUnlocked()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        bea.Engine.Lock();
        Assert.True(bea.Engine.IsLocked);

        var m = await ana.Engine.SendTextAsync(await ana.ContactIdAsync(bea), "llega con Bea bloqueada");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered); // Bea lo guardó (cifrado) sin conocer el PIN

        var whileLocked = await bea.Engine.GetMessagesAsync(await bea.ContactIdAsync(ana), null);
        Assert.Single(whileLocked);
        Assert.Null(whileLocked[0].Text); // no se puede leer
        Assert.Equal("(bloqueado)", (await bea.Engine.GetContactAsync(await bea.ContactIdAsync(ana)))!.DisplayName);

        Assert.True((await bea.Engine.UnlockAsync(TestPeer.Pin)).Success);
        Assert.Equal("llega con Bea bloqueada", (await bea.MessagesWithAsync(ana)).Single().Text);
    }

    [Fact]
    public async Task SendsQueuedWhileLocked_ContinueAfterUnlock()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        ana.Engine.Lock();
        var m = await ana.Engine.SendTextAsync(id, "en cola mientras estaba bloqueada");
        await Task.Delay(600);
        Assert.Equal(MessageStatus.Pending, (await ana.Engine.GetMessageAsync(m.Value!.Id))!.Status);
        Assert.Empty(await bea.MessagesWithAsync(ana));
        await ana.Engine.UnlockAsync(TestPeer.Pin);
        await Wait.Status(ana, m.Value.Id, MessageStatus.Delivered);
        Assert.Equal("en cola mientras estaba bloqueada", (await bea.MessagesWithAsync(ana)).Single().Text);
    }

    [Fact]
    public async Task WrongPins_AreThrottled()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc, options: TestPeer.FastOptions(disc, TestNet.NextPort()) with { PinFreeAttempts = 2 });
        ana.Engine.Lock();
        Assert.Equal("PIN incorrecto.", (await ana.Engine.UnlockAsync("000001")).Message);
        Assert.Contains("Espere", (await ana.Engine.UnlockAsync("000002")).Message);
        // Durante la espera ni siquiera el PIN correcto se acepta.
        var blocked = await ana.Engine.UnlockAsync(TestPeer.Pin);
        Assert.False(blocked.Success);
        Assert.True(ana.Engine.IsLocked);
    }

    [Fact]
    public async Task ForgottenPin_Reset_DeletesHistory_KeepsContactsAndAuthorization()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var id = await ana.ContactIdAsync(bea);
        var m = await ana.Engine.SendTextAsync(id, "se perderá");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered);
        ana.Engine.Lock();

        Assert.True((await ana.Engine.ResetForgottenPinAsync("735184")).Success);
        Assert.False(ana.Engine.IsLocked);
        Assert.Empty(await ana.Engine.GetMessagesAsync(id, null));
        Assert.Equal(Relation.Authorized, await ana.RelationWithAsync(bea));
        // Con el PIN nuevo todo sigue funcionando
        var again = await ana.Engine.SendTextAsync(id, "con el PIN nuevo");
        await Wait.Status(ana, again.Value!.Id, MessageStatus.Delivered);
        ana.Engine.Lock();
        Assert.False((await ana.Engine.UnlockAsync(TestPeer.Pin)).Success);
        Assert.True((await ana.Engine.UnlockAsync("735184")).Success);
    }

    [Fact]
    public async Task ProfileFromPreviousVersion_IsEncrypted_WhenPinIsCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "legacy-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(dir);
        paths.EnsureCreated();
        // Datos como los dejaba la versión sin cifrado: textos, nombres y PNG en claro.
        var db = Database.Open(paths.DatabaseFile, paths.BackupDirectory);
        var png = StickerProcessor.Normalize(StickerTests.MakePng(40, 40, true), new MiniChatOptions());
        db.Write((c, tx) =>
        {
            var contact = ContactsRepo.Upsert(c, tx, new string('C', 64), "NOMBRE-ANTIGUO", "PC-ANTIGUO", null, null);
            var conv = MessagesRepo.GetOrCreateConversation(c, tx, contact.Id);
            MessagesRepo.InsertIncoming(c, tx, conv.Id, Guid.NewGuid().ToString("N"), MessageKind.Text, "HISTORIAL-ANTIGUO en claro", null, 1, DateTimeOffset.UtcNow);
            MessagesRepo.SetDraft(c, tx, conv.Id, "BORRADOR-ANTIGUO");
            StickersRepo.InsertBlobIfMissing(c, tx, new StickerBlob(png.Hash, png.Width, png.Height, png.Png.Length));
            StickersRepo.AddToLibrary(c, tx, png.Hash, "STICKER-ANTIGUO");
        });
        var legacyFile = Path.Combine(paths.StickerDirectory, png.Hash[..2].ToLowerInvariant(), png.Hash + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        File.WriteAllBytes(legacyFile, png.Png);
        SqliteConnection.ClearAllPools();

        var options = TestPeer.FastOptions(TestNet.NextPort(), TestNet.NextPort());
        var engine = ChatEngine.Open(paths, options, new LoopbackInventory());
        try
        {
            Assert.True(engine.NeedsPinSetup);
            Assert.Throws<InvalidOperationException>(() => { engine.SetDisplayNameAsync("Yo").Wait(); engine.StartNetworking(); });
            Assert.True((await engine.SetupPinAsync(TestPeer.Pin)).Success);

            var contact = (await engine.GetPeopleAsync()).Single();
            Assert.Equal("NOMBRE-ANTIGUO", contact.DisplayName);
            Assert.Equal("HISTORIAL-ANTIGUO en claro", (await engine.GetMessagesAsync(contact.ContactId!.Value, null)).Single().Text);
            Assert.Equal("BORRADOR-ANTIGUO", await engine.GetDraftAsync(contact.ContactId!.Value));
            Assert.Equal("STICKER-ANTIGUO", (await engine.Stickers.GetLibraryAsync()).Single().Name);
            Assert.NotNull(await engine.Stickers.ReadBlobAsync(png.Hash, default));
            Assert.False(File.Exists(legacyFile));

            var raw = SharedRead(paths.DatabaseFile).Concat(SharedRead(paths.DatabaseFile + "-wal")).ToArray();
            foreach (var secret in new[] { "HISTORIAL-ANTIGUO", "BORRADOR-ANTIGUO", "NOMBRE-ANTIGUO", "PC-ANTIGUO", "STICKER-ANTIGUO" })
                Assert.False(Contains(raw, secret), secret + " sigue en claro en el archivo");
        }
        finally
        {
            var key = engine.Identity.KeyName;
            await engine.DisposeAsync();
            Core.Identity.LocalIdentity.Delete(key);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task InternetAddresses_AreRefused_BeforeOpeningAnyConnection()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        foreach (var ip in new[] { "8.8.8.8", "1.1.1.1", "142.250.0.1" })
        {
            var r = await ana.Engine.ConnectManualAsync(ip);
            Assert.False(r.Success);
            Assert.Contains("nunca se comunica a través de Internet", r.Message);
        }
    }
}
