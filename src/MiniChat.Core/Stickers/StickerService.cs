using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Security;

namespace MiniChat.Core.Stickers;

public sealed record StickerImportResult(string SourcePath, bool Success, string Message, string? Hash, bool AlreadyInLibrary);

/// <summary>
/// Biblioteca de stickers y almacén de imágenes por contenido (stickers\ab\&lt;sha256&gt;.mcs).
/// En disco las imágenes están CIFRADAS con la clave pública de la bóveda (se pueden guardar con la app
/// bloqueada; leerlas exige el PIN). Los nombres de la biblioteca también se guardan cifrados.
/// Un mismo PNG se guarda una sola vez aunque aparezca en la biblioteca y en muchos mensajes.
/// Quitar un sticker de la biblioteca no borra la imagen si algún mensaje la usa.
/// </summary>
public sealed class StickerService
{
    private readonly Database _db;
    private readonly AppPaths _paths;
    private readonly MiniChatOptions _options;
    private readonly ILog _log;
    private readonly SemaphoreSlim _decodeSlots;
    private readonly Vault _vault;

    public StickerService(Database db, AppPaths paths, MiniChatOptions options, ILog log, Vault vault)
    {
        _vault = vault;
        _db = db;
        _paths = paths;
        _options = options;
        _log = log;
        _decodeSlots = new SemaphoreSlim(options.MaxConcurrentImageDecodes);
    }

    public event EventHandler? LibraryChanged;

    /// <summary>Ruta del archivo cifrado.</summary>
    public string BlobPath(string hash) => Path.Combine(_paths.StickerDirectory, hash[..2].ToLowerInvariant(), hash + ".mcs");

    /// <summary>Ruta usada por versiones anteriores (PNG sin cifrar).</summary>
    private string LegacyPath(string hash) => Path.Combine(_paths.StickerDirectory, hash[..2].ToLowerInvariant(), hash + ".png");

    public async Task<IReadOnlyList<StickerImportResult>> ImportAsync(IEnumerable<string> files, CancellationToken ct = default)
    {
        var tasks = files.Select(f => ImportOneAsync(f, ct)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (results.Any(r => r.Success && !r.AlreadyInLibrary)) LibraryChanged?.Invoke(this, EventArgs.Empty);
        return results;
    }

    private async Task<StickerImportResult> ImportOneAsync(string file, CancellationToken ct)
    {
        await _decodeSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return new(file, false, "El archivo no existe.", null, false);
            if (info.Length > _options.MaxStickerImportFileBytes)
                return new(file, false, $"Supera el máximo de {_options.MaxStickerImportFileBytes / (1024 * 1024)} MB.", null, false);
            // Solo se leen bytes; nunca se ejecuta ni se abre con asociaciones del sistema.
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            var normalized = await Task.Run(() => StickerProcessor.Normalize(bytes, _options), ct).ConfigureAwait(false);
            await WriteBlobFileAsync(normalized.Hash, normalized.Png, ct).ConfigureAwait(false);
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length > 60) name = name[..60];
            bool added = _db.Write((c, tx) =>
            {
                StickersRepo.InsertBlobIfMissing(c, tx, new StickerBlob(normalized.Hash, normalized.Width, normalized.Height, normalized.Png.Length));
                return StickersRepo.AddToLibrary(c, tx, normalized.Hash, _vault.Seal(name)!);
            });
            var message = !added ? "Ya estaba en la biblioteca."
                : normalized.KeptFirstFrameOnly ? $"Importado. Era una imagen animada ({normalized.SourceFormat}): se conservó solo el primer fotograma."
                : "Importado.";
            return new(file, true, message, normalized.Hash, !added);
        }
        catch (StickerRejectedException ex)
        {
            return new(file, false, ex.Message, null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("stickers", "No se pudo leer o guardar un sticker", ex);
            return new(file, false, "No se pudo leer el archivo o guardar el sticker: " + ex.Message, null, false);
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    /// <summary>Cifra y escribe el PNG de forma atómica (temporal + renombrado) y forzado a disco.</summary>
    public async Task WriteBlobFileAsync(string hash, ReadOnlyMemory<byte> png, CancellationToken ct)
    {
        // Dos importaciones o recepciones del mismo contenido pueden coincidir: se serializan por hash.
        var gate = _blobGates.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = BlobPath(hash);
            if (File.Exists(path)) return; // mismo contenido (el nombre es el hash del PNG)
            var sealedBytes = _vault.SealBytes(png.Span);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(sealedBytes, ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _blobGates = new();

    /// <summary>Lee, descifra y verifica un PNG almacenado (detecta archivos dañados o manipulados).</summary>
    /// <exception cref="VaultLockedException">La app está bloqueada.</exception>
    public async Task<byte[]?> ReadBlobAsync(string hash, CancellationToken ct)
    {
        byte[] png;
        if (File.Exists(BlobPath(hash)))
        {
            var sealedBytes = await File.ReadAllBytesAsync(BlobPath(hash), ct).ConfigureAwait(false);
            try { png = _vault.OpenBytes(sealedBytes); }
            catch (System.Security.Cryptography.CryptographicException) { return null; }
        }
        else if (File.Exists(LegacyPath(hash)))
        {
            png = await File.ReadAllBytesAsync(LegacyPath(hash), ct).ConfigureAwait(false);
        }
        else
        {
            return null;
        }
        return StickerProcessor.HashOf(png) == hash ? png : null;
    }

    public bool BlobExists(string hash) => File.Exists(BlobPath(hash)) || File.Exists(LegacyPath(hash));

    public Task<List<LibrarySticker>> GetLibraryAsync() => Task.Run(() => _db.Read(StickersRepo.Library).Select(RevealName).ToList());

    public Task<List<LibrarySticker>> GetRecentAsync(int limit = 12) => Task.Run(() => _db.Read(c => StickersRepo.Recent(c, limit)).Select(RevealName).ToList());

    private LibrarySticker RevealName(LibrarySticker s)
    {
        try { return s with { Name = _vault.Open(s.Name) ?? "" }; }
        catch (Exception ex) when (ex is VaultLockedException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            return s with { Name = "Sticker" };
        }
    }

    /// <summary>Convierte los PNG sin cifrar de versiones anteriores en archivos cifrados. Devuelve cuántos.</summary>
    public int EncryptLegacyFiles()
    {
        if (!Directory.Exists(_paths.StickerDirectory)) return 0;
        int n = 0;
        foreach (var legacy in Directory.EnumerateFiles(_paths.StickerDirectory, "*.png", SearchOption.AllDirectories).ToList())
        {
            var hash = Path.GetFileNameWithoutExtension(legacy);
            try
            {
                var png = File.ReadAllBytes(legacy);
                if (StickerProcessor.HashOf(png) == hash && !File.Exists(BlobPath(hash)))
                {
                    var temp = BlobPath(hash) + ".tmp";
                    File.WriteAllBytes(temp, _vault.SealBytes(png));
                    File.Move(temp, BlobPath(hash), overwrite: true);
                }
                WipeAndDelete(legacy);
                n++;
            }
            catch (IOException ex)
            {
                _log.Warn("stickers", "No se pudo cifrar una imagen antigua", ex);
            }
        }
        return n;
    }

    /// <summary>Borra todas las imágenes (PIN olvidado).</summary>
    public void DeleteAllFiles()
    {
        if (!Directory.Exists(_paths.StickerDirectory)) return;
        foreach (var f in Directory.EnumerateFiles(_paths.StickerDirectory, "*", SearchOption.AllDirectories).ToList())
        {
            try { WipeAndDelete(f); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Sobrescribe con ceros antes de borrar. Es un mejor esfuerzo: en un SSD el controlador puede conservar
    /// bloques físicos antiguos; por eso lo determinante es que los datos se escriban ya cifrados.
    /// </summary>
    private static void WipeAndDelete(string path)
    {
        try
        {
            long length = new FileInfo(path).Length;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var zeros = new byte[(int)Math.Min(Math.Max(length, 1), 64 * 1024)];
            for (long done = 0; done < length; done += zeros.Length)
                fs.Write(zeros, 0, (int)Math.Min(zeros.Length, length - done));
            fs.Flush(flushToDisk: true);
        }
        catch (IOException) { }
        File.Delete(path);
    }

    public async Task SetFavoriteAsync(long id, bool favorite)
    {
        await Task.Run(() => _db.Write((c, tx) => StickersRepo.SetFavorite(c, tx, id, favorite))).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveFromLibraryAsync(long id)
    {
        await Task.Run(() => _db.Write((c, tx) => StickersRepo.RemoveFromLibrary(c, tx, id))).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        await CollectGarbageAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Elimina imágenes que no usa ni la biblioteca ni ningún mensaje, y archivos temporales huérfanos.
    /// Nunca borra un PNG referenciado por mensajes históricos.
    /// </summary>
    public Task<int> CollectGarbageAsync() => Task.Run(() =>
    {
        int removed = 0;
        var unreferenced = _db.Write((c, tx) =>
        {
            var list = StickersRepo.Unreferenced(c, tx);
            foreach (var h in list) StickersRepo.DeleteBlob(c, tx, h);
            return list;
        });
        foreach (var hash in unreferenced)
        {
            try
            {
                if (File.Exists(BlobPath(hash))) File.Delete(BlobPath(hash));
                if (File.Exists(LegacyPath(hash))) WipeAndDelete(LegacyPath(hash));
                removed++;
            }
            catch (IOException ex) { _log.Warn("stickers", "No se pudo borrar una imagen sin uso", ex); }
        }

        // Archivos sin registro (p. ej. cierre entre la escritura del archivo y la transacción) y temporales viejos.
        var known = _db.Read(StickersRepo.AllBlobHashes);
        if (Directory.Exists(_paths.StickerDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_paths.StickerDirectory, "*", SearchOption.AllDirectories))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age < TimeSpan.FromHours(1)) continue;
                var name = Path.GetFileName(file);
                bool orphanTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                bool orphanBlob = (name.EndsWith(".mcs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                  && !known.Contains(Path.GetFileNameWithoutExtension(name));
                if (!orphanTemp && !orphanBlob) continue;
                try { File.Delete(file); removed++; } catch (IOException) { }
            }
        }
        return removed;
    });
}
