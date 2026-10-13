// Genera minichat.ico y minichat-unread.ico (16..256 px). Entradas ≤64 px en formato DIB (BMP 32 bits),
// 256 px en PNG, que es lo que admiten Windows y System.Drawing.Icon.
using SkiaSharp;

var outDir = args.Length > 0 ? args[0] : ".";
Write(Path.Combine(outDir, "minichat.ico"), false);
Write(Path.Combine(outDir, "minichat-unread.ico"), true);
Console.WriteLine("ok");

static SKBitmap Draw(int s, bool unread)
{
    var bmp = new SKBitmap(new SKImageInfo(s, s, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var c = new SKCanvas(bmp);
    c.Clear(SKColors.Transparent);
    float r = s * 0.22f;
    using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(0x2E, 0x8B, 0x7A) };
    c.DrawRoundRect(new SKRect(s * 0.03f, s * 0.03f, s * 0.97f, s * 0.97f), r, r, bg);
    using var white = new SKPaint { IsAntialias = true, Color = SKColors.White };
    using var mint = new SKPaint { IsAntialias = true, Color = new SKColor(0xBF, 0xF0, 0xE4) };
    // Burbuja trasera y delantera
    c.DrawRoundRect(new SKRect(s * 0.40f, s * 0.20f, s * 0.84f, s * 0.52f), s * 0.10f, s * 0.10f, mint);
    c.DrawRoundRect(new SKRect(s * 0.16f, s * 0.34f, s * 0.66f, s * 0.70f), s * 0.11f, s * 0.11f, white);
    using var tail = new SKPath();
    tail.MoveTo(s * 0.24f, s * 0.64f); tail.LineTo(s * 0.20f, s * 0.82f); tail.LineTo(s * 0.38f, s * 0.68f); tail.Close();
    c.DrawPath(tail, white);
    if (unread)
    {
        using var dot = new SKPaint { IsAntialias = true, Color = new SKColor(0xF5, 0x8A, 0x1F) };
        using var ring = new SKPaint { IsAntialias = true, Color = SKColors.White };
        c.DrawCircle(s * 0.78f, s * 0.24f, s * 0.21f, ring);
        c.DrawCircle(s * 0.78f, s * 0.24f, s * 0.16f, dot);
    }
    return bmp;
}

static byte[] Dib(SKBitmap b)
{
    int s = b.Width;
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = s - 1; y >= 0; y--)
        for (int x = 0; x < s; x++) { var p = b.GetPixel(x, y); w.Write(p.Blue); w.Write(p.Green); w.Write(p.Red); w.Write(p.Alpha); }
    int maskStride = ((s + 31) / 32) * 4;
    for (int y = 0; y < s; y++) w.Write(new byte[maskStride]); // máscara AND vacía: se usa el canal alfa
    return ms.ToArray();
}

static void Write(string path, bool unread)
{
    int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
    var images = sizes.Select(s =>
    {
        using var bmp = Draw(s, unread);
        if (s == 256) { using var img = SKImage.FromBitmap(bmp); return img.Encode(SKEncodedImageFormat.Png, 100).ToArray(); }
        return Dib(bmp);
    }).ToList();
    using var f = new BinaryWriter(File.Create(path));
    f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
        f.Write(images[i].Length); f.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) f.Write(img);
}
