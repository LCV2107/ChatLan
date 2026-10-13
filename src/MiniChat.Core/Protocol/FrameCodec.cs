using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace MiniChat.Core.Protocol;

/// <summary>
/// Formato de trama (sobre TLS):
/// <code>
///   uint32 BE  longitud del resto (L), 2 ≤ L ≤ MaxFrameBytes
///   uint16 BE  longitud de la cabecera JSON (H), 2 ≤ H ≤ MaxHeaderBytes, H ≤ L-2
///   H bytes    cabecera JSON UTF-8 (un PacketBase con discriminador "op")
///   L-2-H      cuerpo binario opcional (p. ej. el PNG de un sticker)
/// </code>
/// Las lecturas no suponen que cada Read del transporte trae una trama completa.
/// </summary>
public sealed class FrameCodec
{
    private readonly int _maxFrameBytes;
    private readonly int _maxHeaderBytes;

    public FrameCodec(int maxFrameBytes, int maxHeaderBytes)
    {
        _maxFrameBytes = maxFrameBytes;
        _maxHeaderBytes = maxHeaderBytes;
    }

    public async Task WriteAsync(Stream stream, PacketBase packet, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(packet, ProtocolJson.Default.PacketBase);
        if (header.Length > _maxHeaderBytes) throw new ProtocolException(ErrorCodes.TooLarge, "Cabecera demasiado grande.");
        long total = 2L + header.Length + body.Length;
        if (total > _maxFrameBytes) throw new ProtocolException(ErrorCodes.TooLarge, "Trama demasiado grande.");

        var buffer = new byte[4 + total];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)total);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), (ushort)header.Length);
        header.CopyTo(buffer.AsSpan(6));
        body.Span.CopyTo(buffer.AsSpan(6 + header.Length));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lee una trama completa. Devuelve null si el otro extremo cerró limpiamente entre tramas.
    /// Lanza <see cref="EndOfStreamException"/> si la conexión se corta a mitad de una trama y
    /// <see cref="FrameFormatException"/> si la trama es inválida pero se pudo consumir entera
    /// (la conexión sigue sincronizada), o <see cref="ProtocolException"/> si no se puede resincronizar.
    /// </summary>
    public async Task<Frame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        int first = await stream.ReadAtLeastAsync(lengthBytes, 4, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (first == 0) return null;
        if (first < 4) throw new EndOfStreamException("Conexión cerrada a mitad de la cabecera de trama.");
        uint total = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (total < 2 || total > _maxFrameBytes)
            throw new ProtocolException(ErrorCodes.TooLarge, $"Longitud de trama no válida ({total}).");

        var payload = new byte[total];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        int headerLength = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (headerLength < 2 || headerLength > _maxHeaderBytes || headerLength > total - 2)
            throw new FrameFormatException("Longitud de cabecera no válida.");

        PacketBase? packet;
        try
        {
            packet = JsonSerializer.Deserialize(payload.AsSpan(2, headerLength), ProtocolJson.Default.PacketBase);
        }
        catch (JsonException ex)
        {
            throw new FrameFormatException("Cabecera JSON mal formada o tipo de operación desconocido.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new FrameFormatException("Tipo de operación no admitido.", ex);
        }
        if (packet is null) throw new FrameFormatException("Cabecera vacía.");
        var body = new ReadOnlyMemory<byte>(payload, 2 + headerLength, (int)total - 2 - headerLength);
        return new Frame(packet, body);
    }
}

public sealed record Frame(PacketBase Packet, ReadOnlyMemory<byte> Body);

/// <summary>Error de protocolo que obliga a cerrar la conexión.</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
    public string Code { get; }
}

/// <summary>Trama consumida entera pero con contenido inválido: se responde con error y se sigue.</summary>
public sealed class FrameFormatException : ProtocolException
{
    public FrameFormatException(string message, Exception? inner = null) : base(ErrorCodes.Malformed, message, inner) { }
}

public static class Utf8
{
    public static readonly UTF8Encoding Strict = new(false, true);
}
