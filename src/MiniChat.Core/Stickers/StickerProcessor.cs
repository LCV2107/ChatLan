using System.Security.Cryptography;
using MiniChat.Core.Config;
using SkiaSharp;

namespace MiniChat.Core.Stickers;

public sealed record NormalizedSticker(byte[] Png, int Width, int Height, string Hash, bool KeptFirstFrameOnly, string SourceFormat);

public sealed class StickerRejectedException : Exception
{
    public StickerRejectedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Validación y normalización de imágenes de sticker con SkiaSharp (decodificadores PNG/JPEG/WebP incluidos
/// en libSkiaSharp; no depende de los códecs instalados en Windows).
///  - Se identifica el formato por su firma binaria, no por la extensión.
///  - Se comprueban dimensiones ANTES de decodificar (límite de memoria de decodificación).
///  - El resultado es siempre un PNG nuevo (sin metadatos ni contenido original), ≤ StickerMaxDimension,
///    conservando proporciones y transparencia.
///  - Imágenes animadas: se conserva solo el primer fotograma y se informa.
/// </summary>
public static class StickerProcessor
{
    public static string SniffFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "PNG";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "JPEG";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "WebP";
        return "";
    }

    public static NormalizedSticker Normalize(byte[] data, MiniChatOptions options)
    {
        if (data.Length == 0) throw new StickerRejectedException("El archivo está vacío.");
        if (data.Length > options.MaxStickerImportFileBytes)
            throw new StickerRejectedException($"El archivo supera el máximo de {options.MaxStickerImportFileBytes / (1024 * 1024)} MB.");
        var format = SniffFormat(data);
        if (format == "")
            throw new StickerRejectedException("El contenido no es una imagen PNG, JPG ni WebP (se comprobó el contenido real, no la extensión).");

        using var codec = SKCodec.Create(new SKMemoryStream(data), out var createResult)
            ?? throw new StickerRejectedException($"La imagen está dañada o no se puede leer ({createResult}).");
        var expected = format switch { "PNG" => SKEncodedImageFormat.Png, "JPEG" => SKEncodedImageFormat.Jpeg, _ => SKEncodedImageFormat.Webp };
        if (codec.EncodedFormat != expected) throw new StickerRejectedException("El contenido de la imagen no coincide con su formato.");

        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new StickerRejectedException("Dimensiones no válidas.");
        if (w > options.MaxStickerSourceDimension || h > options.MaxStickerSourceDimension || (long)w * h > options.MaxStickerSourcePixels)
            throw new StickerRejectedException($"La imagen mide {w}×{h}; el máximo admitido es {options.MaxStickerSourceDimension}×{options.MaxStickerSourceDimension}.");

        bool animated = codec.FrameCount > 1;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap();
        if (!decoded.TryAllocPixels(info)) throw new StickerRejectedException("No hay memoria suficiente para procesar la imagen.");
        var result = codec.GetPixels(info, decoded.GetPixels(), new SKCodecOptions(0));
        if (result != SKCodecResult.Success)
            throw new StickerRejectedException($"La imagen está incompleta o dañada ({result}).");

        using var oriented = ApplyOrigin(decoded, codec.EncodedOrigin);
        var source = oriented ?? decoded;

        foreach (var maxSide in new[] { options.StickerMaxDimension, options.StickerMaxDimension * 3 / 4, options.StickerMaxDimension / 2 })
        {
            var (nw, nh) = Fit(source.Width, source.Height, maxSide);
            using var resized = nw == source.Width && nh == source.Height
                ? source.Copy()
                : source.Resize(new SKImageInfo(nw, nh, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resized is null) throw new StickerRejectedException("No se pudo redimensionar la imagen.");
            using var png = resized.Encode(SKEncodedImageFormat.Png, 100);
            if (png is null) throw new StickerRejectedException("No se pudo convertir la imagen a PNG.");
            if (png.Size <= options.MaxStickerWireBytes)
            {
                var bytes = png.ToArray();
                return new NormalizedSticker(bytes, nw, nh, HashOf(bytes), animated, format);
            }
        }
        throw new StickerRejectedException("La imagen es demasiado compleja para usarse como sticker.");
    }

    /// <summary>Valida un sticker recibido por la red antes de guardarlo.</summary>
    public static void ValidateWirePng(ReadOnlySpan<byte> png, string expectedHash, int width, int height, MiniChatOptions options)
    {
        if (png.Length == 0 || png.Length > options.MaxStickerWireBytes) throw new StickerRejectedException("Tamaño no válido.");
        if (SniffFormat(png) != "PNG") throw new StickerRejectedException("No es un PNG.");
        if (!string.Equals(HashOf(png), expectedHash, StringComparison.Ordinal)) throw new StickerRejectedException("El hash no coincide.");
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data) ?? throw new StickerRejectedException("PNG ilegible.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png) throw new StickerRejectedException("No es un PNG.");
        if (codec.Info.Width != width || codec.Info.Height != height) throw new StickerRejectedException("Dimensiones incoherentes.");
        if (width > options.StickerMaxDimension || height > options.StickerMaxDimension) throw new StickerRejectedException("Dimensiones excesivas.");
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result != SKCodecResult.Success) throw new StickerRejectedException($"PNG dañado ({result}).");
    }

    public static string HashOf(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    private static (int W, int H) Fit(int w, int h, int max)
    {
        if (w <= max && h <= max) return (w, h);
        double scale = Math.Min((double)max / w, (double)max / h);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    /// <summary>Aplica la orientación EXIF de JPEG (rotaciones y espejos).</summary>
    private static SKBitmap? ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return null;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? src.Height : src.Width, h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        float sw = src.Width, sh = src.Height;
        // x' = scaleX·x + skewX·y + transX ; y' = skewY·x + scaleY·y + transY  (coordenadas de la imagen original)
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, sw, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, sw, 0, -1, sh, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, sh, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, sh, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, sh, -1, 0, sw, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, sw, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        using var srcImage = SKImage.FromBitmap(src);
        canvas.DrawImage(srcImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return dst;
    }
}
