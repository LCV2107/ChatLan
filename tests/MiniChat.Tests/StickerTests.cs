using System.Buffers.Binary;
using System.Text;
using MiniChat.Core.Config;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Stickers;
using MiniChat.Core.Transport;
using SkiaSharp;

namespace MiniChat.Tests;

[Collection("engine")]
public class StickerTests
{
    private static readonly MiniChatOptions O = new();

    public static byte[] Encode(SKEncodedImageFormat format, int w, int h, bool transparent, int quality = 90, int seed = 0)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)(seed * 40), transparent && x < w / 2 ? (byte)0 : (byte)255));
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(format, quality).ToArray();
    }

    public static byte[] MakePng(int w, int h, bool transparent) => Encode(SKEncodedImageFormat.Png, w, h, transparent);

    [Fact]
    public void Png_KeepsTransparency_AndAspect_WhenDownscaled()
    {
        var n = StickerProcessor.Normalize(Encode(SKEncodedImageFormat.Png, 1600, 800, transparent: true), O);
        Assert.Equal((512, 256), (n.Width, n.Height));
        using var bmp = SKBitmap.Decode(n.Png);
        Assert.Equal(0, bmp.GetPixel(10, 128).Alpha);       // mitad izquierda transparente
        Assert.Equal(255, bmp.GetPixel(500, 128).Alpha);
        Assert.False(n.KeptFirstFrameOnly);
    }

    [Fact]
    public void WebP_WithAlpha_IsDecodedWithoutWindowsCodecs()
    {
        var n = StickerProcessor.Normalize(Encode(SKEncodedImageFormat.Webp, 300, 300, transparent: true), O);
        Assert.Equal("WebP", n.SourceFormat);
        Assert.Equal(300, n.Width);
        using var bmp = SKBitmap.Decode(n.Png);
        Assert.True(bmp.GetPixel(10, 150).Alpha < 10);
        Assert.Equal("PNG", StickerProcessor.SniffFormat(n.Png));
    }

    [Fact]
    public void Jpeg_IsAccepted_AndSmallImagesAreNotUpscaled()
    {
        var n = StickerProcessor.Normalize(Encode(SKEncodedImageFormat.Jpeg, 120, 90, transparent: false), O);
        Assert.Equal((120, 90), (n.Width, n.Height));
        Assert.Equal("JPEG", n.SourceFormat);
    }

    [Fact]
    public void AnimatedWebP_KeepsOnlyFirstFrame_AndSaysSo()
    {
        var anim = BuildAnimatedWebP(Encode(SKEncodedImageFormat.Webp, 40, 40, false, 100, seed: 1), Encode(SKEncodedImageFormat.Webp, 40, 40, false, 100, seed: 5), 40, 40);
        using (var codec = SKCodec.Create(new SKMemoryStream(anim))) Assert.True(codec.FrameCount > 1, "la prueba debe generar una animación real");
        var n = StickerProcessor.Normalize(anim, O);
        Assert.True(n.KeptFirstFrameOnly);
        Assert.Equal((40, 40), (n.Width, n.Height));
    }

    [Fact]
    public void ContentIsCheckedNotExtension_AndLimitsAreEnforced()
    {
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(Encoding.UTF8.GetBytes("<script>alert(1)</script>"), O));
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(Encoding.UTF8.GetBytes("MZ\x90\0 ejecutable"), O));
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3B }, O)); // GIF no admitido
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(Array.Empty<byte>(), O));
        var png = MakePng(100, 100, false);
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(png[..(png.Length / 2)], O)); // truncado
        var big = MakePng(5000, 10, false);
        var ex = Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(big, O));
        Assert.Contains("5000", ex.Message);
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(new byte[6 * 1024 * 1024], O));
        // Bomba de descompresión: cabecera PNG que declara 60000×60000 (rechazada antes de reservar memoria)
        var bomb = MakePng(10, 10, false);
        BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(16), 60000);
        BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(20), 60000);
        Assert.Throws<StickerRejectedException>(() => StickerProcessor.Normalize(bomb, O));
    }

    [Fact]
    public async Task Library_Deduplicates_Favorites_Recent_AndRemovalKeepsHistoryImages()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var dir = Path.Combine(ana.Dir, "in");
        Directory.CreateDirectory(dir);
        var bytes = MakePng(80, 80, true);
        await File.WriteAllBytesAsync(Path.Combine(dir, "a.png"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(dir, "copia.png"), bytes);              // duplicado exacto
        await File.WriteAllBytesAsync(Path.Combine(dir, "w.webp"), Encode(SKEncodedImageFormat.Webp, 90, 60, true));
        await File.WriteAllBytesAsync(Path.Combine(dir, "falso.png"), Encoding.UTF8.GetBytes("no soy imagen"));

        var results = await ana.Engine.Stickers.ImportAsync(Directory.GetFiles(dir).OrderBy(f => f));
        Assert.Equal(3, results.Count(r => r.Success));
        Assert.Single(results, r => r.AlreadyInLibrary);
        Assert.Single(results, r => !r.Success);
        var lib = await ana.Engine.Stickers.GetLibraryAsync();
        Assert.Equal(2, lib.Count);
        // Dos imágenes únicas, guardadas cifradas: ningún PNG legible en disco.
        var stored = Directory.GetFiles(ana.Engine.Paths.StickerDirectory, "*.mcs", SearchOption.AllDirectories);
        Assert.Equal(2, stored.Length);
        Assert.Empty(Directory.GetFiles(ana.Engine.Paths.StickerDirectory, "*.png", SearchOption.AllDirectories));
        Assert.All(stored, f => Assert.NotEqual("PNG", StickerProcessor.SniffFormat(File.ReadAllBytes(f))));

        var a = lib.Single(s => s.Width == 80);
        await ana.Engine.Stickers.SetFavoriteAsync(a.Id, true);
        Assert.True((await ana.Engine.Stickers.GetLibraryAsync())[0].Favorite);

        var id = await ana.ContactIdAsync(bea);
        var m = await ana.Engine.SendStickerAsync(id, a.Hash);
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered);
        Assert.Equal(a.Hash, (await ana.Engine.Stickers.GetRecentAsync())[0].Hash);

        // El receptor tiene la imagen aunque no la tuviera antes (no depende de rutas del emisor).
        var received = (await bea.MessagesWithAsync(ana)).Single();
        Assert.Equal(MessageKind.Sticker, received.Kind);
        Assert.True(bea.Engine.Stickers.BlobExists(a.Hash));
        Assert.NotNull(await bea.Engine.Stickers.ReadBlobAsync(a.Hash, default));

        // Quitarlo de la biblioteca no borra la imagen usada por el historial
        await ana.Engine.Stickers.RemoveFromLibraryAsync(a.Id);
        Assert.True(ana.Engine.Stickers.BlobExists(a.Hash));
        Assert.DoesNotContain(await ana.Engine.Stickers.GetLibraryAsync(), s => s.Hash == a.Hash);
        // Tras borrar el historial ya no se necesita y se libera
        await ana.Engine.ClearHistoryAsync(id);
        Assert.False(ana.Engine.Stickers.BlobExists(a.Hash));
    }

    [Fact]
    public async Task TamperedStickerOnTheWire_IsRejectedByReceiver()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var png = StickerProcessor.Normalize(MakePng(50, 50, false), O);
        await using var client = await PeerClient.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, bea.Options.TcpPort), bea.Fingerprint,
            ana.Engine.Identity.Certificate, new LocalHello("x", "Ana", "PC", 1, "t"), ana.Engine.Policy, ana.Options, default);

        async Task<string> Send(StickerInfo info, byte[] body) =>
            Assert.IsType<MessageAckPacket>((await client.RequestAsync(new MessagePacket { MessageId = Guid.NewGuid().ToString("N"), Kind = MessageKinds.Sticker, Sticker = info }, body, default)).Packet).Status;

        var good = new StickerInfo { Hash = png.Hash, Width = png.Width, Height = png.Height, Length = png.Png.Length };
        var corrupted = (byte[])png.Png.Clone();
        corrupted[^20] ^= 0xFF;
        Assert.Equal(MessageAckStatuses.Invalid, await Send(good, corrupted));                                     // hash no coincide
        Assert.Equal(MessageAckStatuses.Invalid, await Send(good with { Width = 49 }, png.Png));                  // dimensiones falsas
        var notPng = Encoding.UTF8.GetBytes("GIF89a....");
        Assert.Equal(MessageAckStatuses.Invalid, await Send(new StickerInfo { Hash = StickerProcessor.HashOf(notPng), Width = 1, Height = 1, Length = notPng.Length }, notPng));
        Assert.Equal(MessageAckStatuses.Stored, await Send(good, png.Png));
        Assert.Single(await bea.MessagesWithAsync(ana));
    }

    /// <summary>Construye un WebP animado real (contenedor VP8X+ANIM+ANMF) a partir de dos WebP sin pérdida.</summary>
    internal static byte[] BuildAnimatedWebP(byte[] frame1, byte[] frame2, int w, int h)
    {
        static byte[] ImageChunk(byte[] webp)
        {
            // Busca el primer chunk VP8L/VP8 /ALPH... dentro del RIFF y lo devuelve entero (cabecera incluida)
            int pos = 12;
            var ms = new MemoryStream();
            while (pos + 8 <= webp.Length)
            {
                var id = Encoding.ASCII.GetString(webp, pos, 4);
                int size = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(pos + 4));
                int total = 8 + size + (size & 1);
                if (id is "VP8L" or "VP8 " or "ALPH") ms.Write(webp, pos, Math.Min(total, webp.Length - pos));
                pos += total;
            }
            return ms.ToArray();
        }
        static void U24(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); }
        static void Chunk(Stream s, string id, byte[] data)
        {
            s.Write(Encoding.ASCII.GetBytes(id));
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
            s.Write(len);
            s.Write(data);
            if ((data.Length & 1) == 1) s.WriteByte(0);
        }
        var body = new MemoryStream();
        var vp8x = new MemoryStream();
        vp8x.WriteByte(0x02 | 0x10); vp8x.Write(new byte[3]); U24(vp8x, w - 1); U24(vp8x, h - 1);
        Chunk(body, "VP8X", vp8x.ToArray());
        Chunk(body, "ANIM", new byte[] { 0, 0, 0, 0, 0, 0 });
        foreach (var f in new[] { frame1, frame2 })
        {
            var anmf = new MemoryStream();
            U24(anmf, 0); U24(anmf, 0); U24(anmf, w - 1); U24(anmf, h - 1); U24(anmf, 100); anmf.WriteByte(0);
            anmf.Write(ImageChunk(f));
            Chunk(body, "ANMF", anmf.ToArray());
        }
        var result = new MemoryStream();
        result.Write("RIFF"u8);
        Span<byte> riffLen = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(riffLen, (int)body.Length + 4);
        result.Write(riffLen);
        result.Write("WEBP"u8);
        body.Position = 0;
        body.CopyTo(result);
        return result.ToArray();
    }
}
