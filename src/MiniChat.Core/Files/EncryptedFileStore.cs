using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MiniChat.Core.Files;

/// <summary>
/// Documentos guardados cifrados por trozos (AES-256-GCM con una clave aleatoria por archivo; esa clave se
/// guarda cifrada por la bóveda). Formato del archivo:
/// <code>
///   "MCF1" | uint32 BE tamaño de trozo
///   por cada trozo i: texto cifrado (≤ tamaño de trozo) | etiqueta GCM 16 B
///   nonce = 0x00000000 ‖ uint64 BE i ;  dato asociado = fid ‖ i   (impide reordenar o mezclar trozos)
/// </code>
/// Permite leer cualquier rango y añadir trozos al recibir (reanudación) sin descifrar el resto.
/// </summary>
public static class EncryptedFileStore
{
    private static readonly byte[] Magic = "MCF1"u8.ToArray();
    private const int HeaderLength = 8;
    private const int TagLength = 16;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private static long ChunkPosition(long index, int chunkSize) => HeaderLength + index * (chunkSize + (long)TagLength);

    private static byte[] Nonce(long index)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(n.AsSpan(4), index);
        return n;
    }

    private static byte[] Aad(string fid, long index)
    {
        var a = new byte[fid.Length + 8];
        Encoding.ASCII.GetBytes(fid, a);
        BinaryPrimitives.WriteInt64BigEndian(a.AsSpan(fid.Length), index);
        return a;
    }

    /// <summary>Cifra un archivo de origen en <paramref name="destination"/>. Devuelve tamaño y SHA-256 del original.</summary>
    public static async Task<(long Size, string Sha256)> ImportAsync(string source, string destination, string fid, byte[] key, int chunkSize, long maxBytes, CancellationToken ct)
    {
        var temp = destination + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await WriteHeaderAsync(output, chunkSize, ct).ConfigureAwait(false);
            var plain = new byte[chunkSize];
            var cipher = new byte[chunkSize + TagLength];
            using var aes = new AesGcm(key, TagLength);
            for (long i = 0; ; i++)
            {
                int read = await input.ReadAtLeastAsync(plain, chunkSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw new FileTooLargeException(maxBytes);
                sha.AppendData(plain, 0, read);
                aes.Encrypt(Nonce(i), plain.AsSpan(0, read), cipher.AsSpan(0, read), cipher.AsSpan(read, TagLength), Aad(fid, i));
                await output.WriteAsync(cipher.AsMemory(0, read + TagLength), ct).ConfigureAwait(false);
                if (read < chunkSize) break;
            }
            output.Flush(flushToDisk: true);
            CryptographicOperations.ZeroMemory(plain);
        }
        File.Move(temp, destination, overwrite: true);
        return (total, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>Lee y descifra el rango [offset, offset+length) del contenido original.</summary>
    public static async Task<byte[]> ReadRangeAsync(string path, string fid, byte[] key, long offset, int length, long size, CancellationToken ct)
    {
        if (offset >= size) return Array.Empty<byte>();
        length = (int)Math.Min(length, size - offset);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        int chunkSize = await ReadHeaderAsync(fs, ct).ConfigureAwait(false);
        var result = new byte[length];
        using var aes = new AesGcm(key, TagLength);
        long first = offset / chunkSize, last = (offset + length - 1) / chunkSize;
        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize + TagLength];
        for (long i = first; i <= last; i++)
        {
            int plainLen = (int)Math.Min(chunkSize, size - i * chunkSize);
            fs.Position = ChunkPosition(i, chunkSize);
            await fs.ReadExactlyAsync(cipher.AsMemory(0, plainLen + TagLength), ct).ConfigureAwait(false);
            aes.Decrypt(Nonce(i), cipher.AsSpan(0, plainLen), cipher.AsSpan(plainLen, TagLength), plain.AsSpan(0, plainLen), Aad(fid, i));
            long chunkStart = i * chunkSize;
            long from = Math.Max(offset, chunkStart), to = Math.Min(offset + length, chunkStart + plainLen);
            plain.AsSpan((int)(from - chunkStart), (int)(to - from)).CopyTo(result.AsSpan((int)(from - offset)));
        }
        CryptographicOperations.ZeroMemory(plain);
        return result;
    }

    /// <summary>
    /// Prepara el archivo parcial de una descarga para continuar en <paramref name="validBytes"/>: crea la
    /// cabecera si no existe y descarta cualquier resto de un trozo incompleto.
    /// </summary>
    public static async Task PrepareResumeAsync(string partPath, int chunkSize, long validBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        await using var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        if (fs.Length < HeaderLength) { fs.SetLength(0); await WriteHeaderAsync(fs, chunkSize, ct).ConfigureAwait(false); }
        long chunks = validBytes / chunkSize;
        fs.SetLength(ChunkPosition(chunks, chunkSize));
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Añade un trozo descargado (todos completos salvo el último).</summary>
    public static async Task AppendChunkAsync(string partPath, string fid, byte[] key, int chunkSize, long index, ReadOnlyMemory<byte> plain, CancellationToken ct)
    {
        var cipher = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(key, TagLength))
            aes.Encrypt(Nonce(index), plain.Span, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length, TagLength), Aad(fid, index));
        await using var fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        fs.Position = ChunkPosition(index, chunkSize);
        await fs.WriteAsync(cipher, ct).ConfigureAwait(false);
        fs.SetLength(fs.Position);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Descifra todo el archivo hacia un flujo, calculando su SHA-256.</summary>
    public static async Task<string> DecryptToAsync(string path, string fid, byte[] key, long size, Stream destination, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int chunkSize = await ReadHeaderAsync(fs, ct).ConfigureAwait(false);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aes = new AesGcm(key, TagLength);
        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize + TagLength];
        long done = 0;
        for (long i = 0; done < size; i++)
        {
            int plainLen = (int)Math.Min(chunkSize, size - done);
            await fs.ReadExactlyAsync(cipher.AsMemory(0, plainLen + TagLength), ct).ConfigureAwait(false);
            aes.Decrypt(Nonce(i), cipher.AsSpan(0, plainLen), cipher.AsSpan(plainLen, TagLength), plain.AsSpan(0, plainLen), Aad(fid, i));
            sha.AppendData(plain, 0, plainLen);
            await destination.WriteAsync(plain.AsMemory(0, plainLen), ct).ConfigureAwait(false);
            done += plainLen;
        }
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Marca un archivo como procedente de otro equipo (Mark-of-the-Web) para que Windows avise al abrirlo.</summary>
    public static void MarkAsDownloaded(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=MiniChat LAN\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unidades sin flujos alternativos (FAT, USB): sin marca, pero MiniChat ya advirtió.
        }
    }

    private static async Task WriteHeaderAsync(Stream s, int chunkSize, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)chunkSize);
        await s.WriteAsync(header, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadHeaderAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("No es un documento cifrado de MiniChat.");
        int chunk = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (chunk is < 1024 or > 16 * 1024 * 1024) throw new CryptographicException("Cabecera dañada.");
        return chunk;
    }
}

public sealed class FileTooLargeException : Exception
{
    public FileTooLargeException(long max) : base($"El archivo supera el máximo de {FileNames.FormatSize(max)}.") { }
}
