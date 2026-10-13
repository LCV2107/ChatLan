using System.Net;
using System.Security.Cryptography;
using System.Text;
using MiniChat.Core.Engine;
using MiniChat.Core.Files;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Transport;

namespace MiniChat.Tests;

[Collection("engine")]
public class FileTransferTests
{
    private static async Task<(TestPeer Ana, TestPeer Bea)> PairAsync(int chunk = 64 * 1024, long maxBytes = 100L * 1024 * 1024)
    {
        int disc = TestNet.NextPort();
        var ana = await TestPeer.StartAsync("Ana", disc, options: TestPeer.FastOptions(disc, TestNet.NextPort()) with { FileChunkBytes = chunk, MaxFileBytes = maxBytes });
        var bea = await TestPeer.StartAsync("Bea", disc, options: TestPeer.FastOptions(disc, TestNet.NextPort()) with { FileChunkBytes = chunk, MaxFileBytes = maxBytes });
        await Wait.Pair(ana, bea);
        return (ana, bea);
    }

    private static string MakeFile(TestPeer owner, string name, int size, int seed = 1)
    {
        var dir = Path.Combine(owner.Dir, "origen");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        Encoding.ASCII.GetBytes("CONTENIDO-SECRETO-DEL-DOCUMENTO").CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static async Task<FileTransfer> FileOf(TestPeer p, string fid) =>
        (await Task.Run(() => p.Engine.Db.Read(c => FilesRepo.Get(c, fid))))!;

    private static Task WaitFile(TestPeer p, string fid, FileState state, int ms = 15000) =>
        Wait.Until(async () => (await FileOf(p, fid))?.State == state, $"{p.Name}: documento → {state}", ms);

    [Fact]
    public async Task Offer_Accept_Transfer_Verify_Save_BothSidesEncrypted()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        var path = MakeFile(ana, "Informe trimestral.pdf", 1_000_000 + 123); // varios trozos y uno final parcial
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), path);
        Assert.True(sent.Success, sent.Message);
        var fid = sent.Value!.Uid;
        await Wait.Status(ana, sent.Value.Id, MessageStatus.Delivered); // la oferta llegó

        // Bea ve la oferta (no se descarga nada todavía)
        await WaitFile(bea, fid, FileState.Offered);
        var offer = (await bea.MessagesWithAsync(ana)).Single();
        Assert.Equal(MessageKind.File, offer.Kind);
        Assert.Equal("Informe trimestral.pdf", offer.File!.Name);
        Assert.Equal(1_000_123, offer.File.Size);
        Assert.Equal(1, bea.NoticeCount(NoticeKind.FileOffered));
        Assert.False(File.Exists(Path.Combine(bea.Engine.Paths.FilesDirectory, fid + ".mcf")));

        Assert.True((await bea.Engine.AcceptFileAsync(fid)).Success);
        await WaitFile(bea, fid, FileState.Completed);
        await WaitFile(ana, fid, FileState.Completed); // el emisor recibe la confirmación

        // Guardar como: idéntico al original y marcado como descargado de otro equipo
        var dest = Path.Combine(bea.Dir, "guardado.pdf");
        Assert.True((await bea.Engine.SaveFileAsync(fid, dest)).Success);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(dest));
        Assert.True(File.Exists(dest + ":Zone.Identifier"));
        Assert.Contains("ZoneId=3", File.ReadAllText(dest + ":Zone.Identifier"));

        // En disco, en ambos equipos, el documento y su nombre están cifrados
        foreach (var p in new[] { ana, bea })
        {
            var stored = File.ReadAllBytes(Path.Combine(p.Engine.Paths.FilesDirectory, fid + ".mcf"));
            Assert.Equal(-1, stored.AsSpan().IndexOf("CONTENIDO-SECRETO"u8));
            using var fs = new FileStream(p.Engine.Paths.DatabaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var db = new byte[fs.Length]; fs.ReadExactly(db);
            Assert.Equal(-1, db.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Informe trimestral")));
        }
    }

    [Fact]
    public async Task Reject_IsReportedToSender_AndNothingIsDownloaded()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), MakeFile(ana, "foto.jpg", 50_000));
        var fid = sent.Value!.Uid;
        await WaitFile(bea, fid, FileState.Offered);
        Assert.True((await bea.Engine.RejectFileAsync(fid)).Success);
        await WaitFile(ana, fid, FileState.Rejected);
        Assert.False(File.Exists(Path.Combine(bea.Engine.Paths.FilesDirectory, fid + ".mcf")));
        Assert.False((await bea.Engine.AcceptFileAsync(fid)).Success);
    }

    [Fact]
    public async Task SenderCancels_BeforeAcceptance_ReceiverCannotDownload()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), MakeFile(ana, "datos.xlsx", 300_000));
        var fid = sent.Value!.Uid;
        await WaitFile(bea, fid, FileState.Offered);
        Assert.True((await ana.Engine.CancelFileAsync(fid)).Success);
        await WaitFile(bea, fid, FileState.Cancelled); // aviso de cancelación
        Assert.False((await bea.Engine.AcceptFileAsync(fid)).Success);
    }

    [Fact]
    public async Task Transfer_PausesWhenSenderLocks_AndResumesFromSamePoint()
    {
        var (ana, bea) = await PairAsync(chunk: 16 * 1024);
        await using var _a = ana; await using var _b = bea;
        var path = MakeFile(ana, "grande.zip", 6 * 1024 * 1024, seed: 7);
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), path);
        var fid = sent.Value!.Uid;
        await WaitFile(bea, fid, FileState.Offered);
        await bea.Engine.AcceptFileAsync(fid);
        await Wait.Until(async () => (await FileOf(bea, fid)).Transferred > 0, "descarga en marcha");

        ana.Engine.Lock(); // el emisor se bloquea a mitad
        await Wait.Until(async () => (await FileOf(bea, fid)).Error?.Contains("bloqueado") == true, "pausa por bloqueo del emisor");
        var paused = await FileOf(bea, fid);
        Assert.Equal(FileState.Transferring, paused.State);
        Assert.InRange(paused.Transferred, 1, paused.Size - 1);

        Assert.True((await ana.Engine.UnlockAsync(TestPeer.Pin)).Success);
        await WaitFile(bea, fid, FileState.Completed, 30000); // continúa sola al volver a ver al emisor
        var dest = Path.Combine(bea.Dir, "grande.zip");
        Assert.True((await bea.Engine.SaveFileAsync(fid, dest)).Success);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(File.ReadAllBytes(dest)));
    }

    [Fact]
    public async Task ReceiverOffline_WhenAccepting_ResumesAfterRestart()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        var path = MakeFile(ana, "acta.docx", 400_000);
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), path);
        var fid = sent.Value!.Uid;
        await WaitFile(bea, fid, FileState.Offered);
        await ana.StopAsync();
        await bea.Engine.AcceptFileAsync(fid);
        await Wait.Until(async () => (await FileOf(bea, fid)).Error?.Contains("sin conexión") == true, "pausa sin conexión");
        await bea.RestartAsync(); // Bea también se cierra y vuelve
        await ana.OpenAsync();
        await WaitFile(bea, fid, FileState.Completed, 30000);
    }

    [Fact]
    public async Task OnlyTheAuthorizedRecipient_CanDownload()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        await using var caro = await TestPeer.StartAsync("Caro", bea.Options.DiscoveryPort);
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), MakeFile(ana, "privado.pdf", 10_000));
        var fid = sent.Value!.Uid;
        await WaitFile(bea, fid, FileState.Offered);

        // Caro conoce el identificador y la IP de Ana, pero no está autorizada: no obtiene nada.
        await using var client = await PeerClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ana.Options.TcpPort), ana.Fingerprint,
            caro.Engine.Identity.Certificate, new LocalHello("x", "Caro", "PC", 1, "t"), caro.Engine.Policy, caro.Options, default);
        var reply = await client.RequestAsync(new FileGetPacket { FileId = fid, Offset = 0, Length = 1024 }, default, default);
        Assert.Equal(FileErrorCodes.Unavailable, Assert.IsType<ErrorPacket>(reply.Packet).Code);
        Assert.Equal(0, reply.Body.Length);
    }

    [Fact]
    public async Task TooLarge_IsRefused_AndOfferWhileLocked_IsStored()
    {
        var (ana, bea) = await PairAsync(maxBytes: 200_000);
        await using var _a = ana; await using var _b = bea;
        var big = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), MakeFile(ana, "enorme.iso", 300_000));
        Assert.False(big.Success);
        Assert.Contains("máximo", big.Message);

        bea.Engine.Lock();
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), MakeFile(ana, "instalar.exe", 50_000));
        await Wait.Status(ana, sent.Value!.Id, MessageStatus.Delivered); // la oferta se guarda aunque Bea esté bloqueada
        Assert.False((await bea.Engine.AcceptFileAsync(sent.Value.Uid)).Success); // pero no se puede aceptar bloqueada
        await bea.Engine.UnlockAsync(TestPeer.Pin);
        var offer = (await bea.MessagesWithAsync(ana)).Single();
        Assert.True(FileNames.IsDangerous(offer.File!.Name));
        Assert.True((await bea.Engine.AcceptFileAsync(offer.Uid)).Success);
        await WaitFile(bea, offer.Uid, FileState.Completed);
        var opened = await bea.Engine.PrepareOpenFileAsync(offer.Uid);
        Assert.True(opened.Success);
        Assert.True(opened.Value!.IsDangerous);
        Assert.True(File.Exists(opened.Value.Path + ":Zone.Identifier"));
        bea.Engine.Lock(); // al bloquear se borran las copias abiertas
        Assert.False(File.Exists(opened.Value.Path));
    }

    [Fact]
    public async Task ClearHistory_RemovesStoredDocuments()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        var sent = await ana.Engine.SendFileAsync(await ana.ContactIdAsync(bea), MakeFile(ana, "a.txt", 5000));
        await Wait.Status(ana, sent.Value!.Id, MessageStatus.Delivered);
        Assert.True(File.Exists(Path.Combine(ana.Engine.Paths.FilesDirectory, sent.Value.Uid + ".mcf")));
        await ana.Engine.ClearHistoryAsync(await ana.ContactIdAsync(bea));
        Assert.False(File.Exists(Path.Combine(ana.Engine.Paths.FilesDirectory, sent.Value.Uid + ".mcf")));
    }

    [Fact]
    public void FileNames_AreSanitized_AndRiskyTypesDetected()
    {
        Assert.Equal("facturafdp.exe", FileNames.Sanitize("factura‮fdp.exe")); // sin el carácter que invierte el texto
        Assert.True(FileNames.IsDangerous(FileNames.Sanitize("factura‮fdp.exe")));
        Assert.Equal("_CON.txt", FileNames.Sanitize("CON.txt"));
        Assert.Equal("a_b_c.txt", FileNames.Sanitize("a/b\\c.txt"));
        Assert.Equal("archivo", FileNames.Sanitize("..."));
        Assert.Equal("informe.pdf", FileNames.Sanitize("informe.pdf. . "));
        Assert.True(FileNames.Sanitize(new string('x', 400) + ".pdf").Length <= FileNames.MaxNameLength);
        Assert.EndsWith(".pdf", FileNames.Sanitize(new string('x', 400) + ".pdf"));
        foreach (var ext in new[] { "a.exe", "b.PS1", "c.bat", "d.msi", "e.js", "f.vbs", "g.lnk", "h.docm" })
            Assert.True(FileNames.IsDangerous(ext), ext);
        foreach (var ok in new[] { "a.pdf", "b.docx", "c.xlsx", "d.png", "e.zip", "f.txt" })
            Assert.False(FileNames.IsDangerous(ok), ok);
        Assert.False(FileNames.IsAcceptableWireName("..\\..\\windows\\system32\\x.dll"));
        Assert.False(FileNames.IsAcceptableWireName("c:evil"));
        Assert.True(FileNames.IsAcceptableWireName("Presupuesto 2026 (final).xlsx"));
    }

    [Fact]
    public async Task Version1Peer_CannotUseFileOperations()
    {
        var (ana, bea) = await PairAsync();
        await using var _a = ana; await using var _b = bea;
        // Un cliente que solo habla v1 se conecta a Ana e intenta un file_get: operación no admitida.
        await using var raw = await RawTls.ConnectAsync(ana.Options.TcpPort, bea.Engine.Identity);
        await raw.SendAsync(new HelloPacket { MinVersion = 1, MaxVersion = 1, InstanceId = "v1", DisplayName = "Bea", MachineName = "PC", ListenPort = 1 });
        var hello = Assert.IsType<HelloOkPacket>((await raw.ReadAsync())!.Packet);
        Assert.Equal(1, hello.Version);
        await raw.SendAsync(new FileGetPacket { FileId = Guid.NewGuid().ToString("N"), Offset = 0, Length = 1024 });
        Assert.Equal(ErrorCodes.UnsupportedOp, Assert.IsType<ErrorPacket>((await raw.ReadAsync())!.Packet).Code);
    }
}
