using System.Collections.Concurrent;
using System.Security.Cryptography;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Files;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Security;
using MiniChat.Core.Transport;

namespace MiniChat.Core.Engine;

// Documentos adjuntos (protocolo v2).
//
//  Emisor: copia el archivo a su almacén cifrado, lo ofrece (file_offer, viaja por la cola de mensajes con su
//  orden, reintentos y estados) y sirve trozos cuando el receptor los pide (file_get).
//  Receptor: guarda la oferta; solo si el usuario acepta, pide trozos de 1 MB, los guarda cifrados y verifica
//  el SHA-256 al terminar. Un corte se reanuda desde el último trozo guardado.
//  Mientras cualquiera de las dos apps está bloqueada la transferencia se pausa (no se puede descifrar ni cifrar
//  con la clave del archivo) y continúa sola al desbloquear o cuando el compañero vuelve a estar disponible.
public sealed partial class ChatEngine
{
    private readonly ConcurrentDictionary<string, byte[]> _fileKeys = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _downloadAttempts = new();
    private SemaphoreSlim? _downloadSlots;
    private SemaphoreSlim DownloadSlots => _downloadSlots ??= new SemaphoreSlim(Options.MaxConcurrentFileTransfers);

    public event EventHandler<FileEvent>? FileChanged;

    private string StoredPath(string fid) => Path.Combine(Paths.FilesDirectory, fid + ".mcf");
    private string PartPath(string fid) => Path.Combine(Paths.FilesDirectory, fid + ".part");

    // ======================= Acciones del usuario =======================

    public async Task<OperationResult<ChatMessage>> SendFileAsync(long contactId, string path, CancellationToken ct = default)
    {
        if (!Vault.IsUnlocked) return new(false, null, "Desbloquee MiniChat para enviar documentos.");
        var info = new FileInfo(path);
        if (!info.Exists) return new(false, null, "El archivo no existe.");
        if (info.Length == 0) return new(false, null, "El archivo está vacío.");
        if (info.Length > Options.MaxFileBytes) return new(false, null, $"El archivo supera el máximo de {FileNames.FormatSize(Options.MaxFileBytes)}.");
        var contact = await GetContactAsync(contactId).ConfigureAwait(false);
        if (contact is null) return new(false, null, "El contacto no existe.");
        if (contact.Blocked || contact.Relation != Relation.Authorized)
            return new(false, null, "No tiene autorización para escribir a esta persona.");

        var fid = Guid.NewGuid().ToString("N");
        var key = EncryptedFileStore.NewKey();
        long size;
        string sha;
        try
        {
            (size, sha) = await EncryptedFileStore.ImportAsync(path, StoredPath(fid), fid, key, Options.FileChunkBytes, Options.MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileTooLargeException)
        {
            TryDelete(StoredPath(fid));
            return new(false, null, ex is FileTooLargeException ? ex.Message : "No se pudo leer el archivo: " + ex.Message);
        }
        var name = FileNames.Sanitize(info.Name);
        var message = await Task.Run(() => Db.Write((c, tx) =>
        {
            var conv = MessagesRepo.GetOrCreateConversation(c, tx, contactId);
            var m = MessagesRepo.InsertOutgoing(c, tx, conv.Id, MessageKind.File, null, null, fid);
            FilesRepo.Insert(c, tx, fid, conv.Id, MessageDirection.Outgoing, Vault.Seal(name)!, size, sha, FileState.Offered, Vault.Seal(Convert.ToBase64String(key)));
            return m;
        }), ct).ConfigureAwait(false);
        _fileKeys[fid] = key;
        _log.Info("files", $"Documento preparado para enviar ({FileNames.FormatSize(size)})");
        RaiseMessage(contactId, message, isNew: true);
        Kick(contactId);
        return new(true, Reveal(message));
    }

    /// <summary>El usuario acepta un documento ofrecido: comienza la descarga.</summary>
    public async Task<OperationResult> AcceptFileAsync(string fid)
    {
        if (!Vault.IsUnlocked) return OperationResult.Fail("Desbloquee MiniChat para aceptar documentos.");
        var file = await Task.Run(() => Db.Read(c => FilesRepo.Get(c, fid))).ConfigureAwait(false);
        if (file is null || file.Direction != MessageDirection.Incoming) return OperationResult.Fail("El documento no existe.");
        if (file.State != FileState.Offered) return OperationResult.Fail("Este documento ya no está pendiente de aceptar.");
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Paths.DataDirectory)!).AvailableFreeSpace;
            if (free < file.Size + 50L * 1024 * 1024)
                return OperationResult.Fail($"No hay espacio suficiente en el disco (hacen falta {FileNames.FormatSize(file.Size)}).");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }

        var key = EncryptedFileStore.NewKey();
        await Task.Run(() => Db.Write((c, tx) =>
        {
            FilesRepo.SetKey(c, tx, fid, Vault.Seal(Convert.ToBase64String(key))!);
            FilesRepo.SetState(c, tx, fid, FileState.Transferring, null);
        })).ConfigureAwait(false);
        _fileKeys[fid] = key;
        RaiseFile(fid);
        _ = Task.Run(() => DownloadAsync(fid));
        return OperationResult.Ok();
    }

    public async Task<OperationResult> RejectFileAsync(string fid)
    {
        var file = await Task.Run(() => Db.Read(c => FilesRepo.Get(c, fid))).ConfigureAwait(false);
        if (file is null || file.Direction != MessageDirection.Incoming) return OperationResult.Fail("El documento no existe.");
        if (file.State is FileState.Completed or FileState.Rejected or FileState.Cancelled)
            return OperationResult.Fail("Este documento ya no se puede rechazar.");
        StopDownload(fid);
        await Task.Run(() => Db.Write((c, tx) => FilesRepo.SetState(c, tx, fid, FileState.Rejected, null, peerNotified: false))).ConfigureAwait(false);
        TryDelete(PartPath(fid));
        RaiseFile(fid);
        _ = Task.Run(() => NotifyFileDecisionAsync(fid));
        return OperationResult.Ok();
    }

    /// <summary>El emisor cancela un envío (el receptor deja de poder descargarlo).</summary>
    public async Task<OperationResult> CancelFileAsync(string fid)
    {
        var file = await Task.Run(() => Db.Read(c => FilesRepo.Get(c, fid))).ConfigureAwait(false);
        if (file is null || file.Direction != MessageDirection.Outgoing) return OperationResult.Fail("El documento no existe.");
        if (file.State is FileState.Completed or FileState.Rejected or FileState.Cancelled)
            return OperationResult.Fail("Este envío ya terminó.");
        await Task.Run(() => Db.Write((c, tx) => FilesRepo.SetState(c, tx, fid, FileState.Cancelled, null))).ConfigureAwait(false);
        RaiseFile(fid);
        var contact = ContactOfFile(file);
        if (contact is not null) _ = SendBestEffortAsync(contact, new FileCancelPacket { FileId = fid });
        return OperationResult.Ok("Envío cancelado.");
    }

    /// <summary>Descifra el documento y lo guarda donde el usuario elija (marcado como descargado de otro equipo).</summary>
    public async Task<OperationResult> SaveFileAsync(string fid, string destination, CancellationToken ct = default)
    {
        var (file, key, error) = await ReadableFileAsync(fid).ConfigureAwait(false);
        if (error is not null) return OperationResult.Fail(error);
        var temp = destination + ".minichat.tmp";
        try
        {
            string sha;
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                sha = await EncryptedFileStore.DecryptToAsync(StoredPath(fid), fid, key!, file!.Size, output, ct).ConfigureAwait(false);
            if (sha != file.Sha256) { TryDelete(temp); return OperationResult.Fail("El documento guardado está dañado (no coincide su huella)."); }
            File.Move(temp, destination, overwrite: true);
            EncryptedFileStore.MarkAsDownloaded(destination);
            return OperationResult.Ok("Guardado (sin cifrar) en " + destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDelete(temp);
            return OperationResult.Fail("No se pudo guardar: " + ex.Message);
        }
    }

    /// <summary>
    /// Descifra el documento en la carpeta privada «abiertos» para abrirlo con su programa. Esa copia queda sin
    /// cifrar mientras esté abierta; se borra al bloquear MiniChat o al volver a abrirlo (si ya no está en uso).
    /// </summary>
    public async Task<OperationResult<OpenedFile>> PrepareOpenFileAsync(string fid, CancellationToken ct = default)
    {
        var (file, key, error) = await ReadableFileAsync(fid).ConfigureAwait(false);
        if (error is not null) return new(false, null, error);
        var name = Vault.Open(file!.Name) ?? "archivo";
        var dir = Path.Combine(Paths.OpenedDirectory, fid);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, FileNames.Sanitize(name));
        var result = await SaveFileAsync(fid, target, ct).ConfigureAwait(false);
        return result.Success ? new(true, new OpenedFile(target, FileNames.IsDangerous(target))) : new(false, null, result.Message);
    }

    /// <summary>Borra las copias descifradas temporales que ya no estén en uso.</summary>
    public int CleanupOpenedFiles()
    {
        int removed = 0;
        if (!Directory.Exists(Paths.OpenedDirectory)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(Paths.OpenedDirectory))
        {
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* abierto en otro programa */ }
        }
        return removed;
    }

    private async Task<(FileTransfer? File, byte[]? Key, string? Error)> ReadableFileAsync(string fid)
    {
        if (!Vault.IsUnlocked) return (null, null, "Desbloquee MiniChat para abrir documentos.");
        var file = await Task.Run(() => Db.Read(c => FilesRepo.Get(c, fid))).ConfigureAwait(false);
        if (file is null) return (null, null, "El documento no existe.");
        bool available = file.Direction == MessageDirection.Outgoing || file.State == FileState.Completed;
        if (!available || !File.Exists(StoredPath(fid))) return (null, null, "El documento aún no está disponible en este equipo.");
        var key = GetFileKey(fid);
        return key is null ? (null, null, "No se pudo abrir la clave del documento.") : (file, key, null);
    }

    // ======================= Descarga (receptor) =======================

    private async Task DownloadAsync(string fid)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_downloads.TryAdd(fid, cts)) { cts.Dispose(); return; }
        _downloadAttempts[fid] = DateTimeOffset.UtcNow;
        bool slot = false;
        try
        {
            await DownloadSlots.WaitAsync(cts.Token).ConfigureAwait(false);
            slot = true;
            await DownloadCoreAsync(fid, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (VaultLockedException)
        {
            SetFileError(fid, "En pausa: MiniChat está bloqueado. Continuará al desbloquear.");
        }
        catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IdentityMismatchException or IncompatibleVersionException)
        {
            SetFileError(fid, "En pausa: sin conexión con el compañero. Continuará cuando esté disponible.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("files", "Error de disco en una descarga", ex);
            SetFileError(fid, "En pausa: error al escribir en el disco (" + ex.Message + ").");
        }
        catch (Exception ex)
        {
            _log.Error("files", "Error inesperado en una descarga", ex);
            SetFileError(fid, "En pausa por un error inesperado.");
        }
        finally
        {
            if (slot) DownloadSlots.Release();
            _downloads.TryRemove(fid, out _);
            cts.Dispose();
        }
    }

    private async Task DownloadCoreAsync(string fid, CancellationToken ct)
    {
        if (_pool is null) return;
        var file = Db.Read(c => FilesRepo.Get(c, fid));
        if (file is null || file.Direction != MessageDirection.Incoming || file.State != FileState.Transferring) return;
        if (!Vault.IsUnlocked) throw new VaultLockedException();
        var key = GetFileKey(fid) ?? throw new VaultLockedException();
        var contact = ContactOfFile(file) ?? throw new PeerUnreachableException("Contacto desconocido.");
        if (contact.Blocked || contact.Relation != Relation.Authorized)
        {
            SetFileState(fid, FileState.Failed, "Ya no hay autorización con esta persona.");
            return;
        }

        int chunk = Options.FileChunkBytes;
        long offset = file.Transferred / chunk * chunk;
        var part = PartPath(fid);
        await EncryptedFileStore.PrepareResumeAsync(part, chunk, offset, ct).ConfigureAwait(false);
        var (client, _) = await _pool.GetAsync(contact.Fingerprint, EndpointsFor(contact.Fingerprint, contact), ct).ConfigureAwait(false);
        if (client.Peer.ProtocolVersion < ProtocolInfo.FilesVersion)
        {
            SetFileState(fid, FileState.Failed, "El compañero usa una versión de MiniChat sin documentos.");
            return;
        }
        SetFileError(fid, null);
        var lastUi = DateTime.MinValue;

        while (offset < file.Size)
        {
            if (!Vault.IsUnlocked) throw new VaultLockedException();
            int want = (int)Math.Min(chunk, file.Size - offset);
            var reply = await client.RequestAsync(new FileGetPacket { FileId = fid, Offset = offset, Length = want }, default, ct).ConfigureAwait(false);
            switch (reply.Packet)
            {
                case FileDataPacket d when d.FileId == fid && d.Offset == offset && reply.Body.Length == want:
                    await EncryptedFileStore.AppendChunkAsync(part, fid, key, chunk, offset / chunk, reply.Body, ct).ConfigureAwait(false);
                    offset += want;
                    Db.Write((c, tx) => FilesRepo.SetProgress(c, tx, fid, offset));
                    if ((DateTime.UtcNow - lastUi).TotalMilliseconds > 250 || offset >= file.Size)
                    {
                        lastUi = DateTime.UtcNow;
                        RaiseFile(fid);
                    }
                    break;
                case ErrorPacket { Code: FileErrorCodes.Locked }:
                    SetFileError(fid, "En pausa: el remitente tiene MiniChat bloqueado. Continuará cuando lo desbloquee.");
                    return;
                case ErrorPacket { Code: FileErrorCodes.Cancelled }:
                    TryDelete(part);
                    SetFileState(fid, FileState.Cancelled, "El remitente canceló el envío.");
                    return;
                case ErrorPacket { Code: ErrorCodes.RateLimited }:
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    break;
                case ErrorPacket { Code: FileErrorCodes.Unavailable }:
                    TryDelete(part);
                    SetFileState(fid, FileState.Failed, "El remitente ya no tiene este documento.");
                    return;
                default:
                    TryDelete(part);
                    Db.Write((c, tx) => FilesRepo.SetProgress(c, tx, fid, 0));
                    SetFileState(fid, FileState.Failed, "El remitente envió datos no válidos.");
                    return;
            }
        }

        // Verificación de integridad antes de dar el documento por recibido.
        string sha;
        await using (var sink = Stream.Null)
            sha = await EncryptedFileStore.DecryptToAsync(part, fid, key, file.Size, sink, ct).ConfigureAwait(false);
        if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            Db.Write((c, tx) => FilesRepo.SetProgress(c, tx, fid, 0));
            SetFileState(fid, FileState.Failed, "El documento recibido no coincide con el original (dañado). Pida que se lo reenvíen.");
            return;
        }
        File.Move(part, StoredPath(fid), overwrite: true);
        Db.Write((c, tx) => FilesRepo.SetState(c, tx, fid, FileState.Completed, null, peerNotified: false));
        RaiseFile(fid);
        _log.Info("files", $"Documento recibido y verificado ({FileNames.FormatSize(file.Size)})");
        await NotifyFileDecisionAsync(fid).ConfigureAwait(false);
    }

    private void StopDownload(string fid)
    {
        if (_downloads.TryRemove(fid, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// <summary>Continúa las descargas en pausa y avisa decisiones pendientes (al desbloquear o al ver al compañero).</summary>
    private void ResumeFileWork(string? fingerprint)
    {
        if (!Vault.IsUnlocked || _pool is null) return;
        try
        {
            foreach (var f in Db.Read(c => FilesRepo.InState(c, MessageDirection.Incoming, FileState.Transferring)))
            {
                if (_downloads.ContainsKey(f.FileId)) continue;
                var contact = ContactOfFile(f);
                if (contact is null || (fingerprint is not null && contact.Fingerprint != fingerprint)) continue;
                if (_downloadAttempts.TryGetValue(f.FileId, out var last) && DateTimeOffset.UtcNow - last < Options.DecisionRedeliveryMinInterval) continue;
                _ = Task.Run(() => DownloadAsync(f.FileId));
            }
            foreach (var f in Db.Read(FilesRepo.PendingNotifications))
            {
                var contact = ContactOfFile(f);
                if (contact is null || (fingerprint is not null && contact.Fingerprint != fingerprint)) continue;
                _ = Task.Run(() => NotifyFileDecisionAsync(f.FileId));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("files", "No se pudieron reanudar las transferencias", ex);
        }
    }

    /// <summary>Comunica al emisor «recibido» o «rechazado». Si no llega, se reintenta más tarde.</summary>
    private async Task NotifyFileDecisionAsync(string fid)
    {
        if (_pool is null) return;
        var file = Db.Read(c => FilesRepo.Get(c, fid));
        if (file is null || file.Direction != MessageDirection.Incoming) return;
        PacketBase? packet = file.State switch
        {
            FileState.Completed => new FileDonePacket { FileId = fid },
            FileState.Rejected => new FileRejectPacket { FileId = fid },
            _ => null,
        };
        var contact = ContactOfFile(file);
        if (packet is null || contact is null) return;
        try
        {
            var (client, _) = await _pool.GetAsync(contact.Fingerprint, EndpointsFor(contact.Fingerprint, contact), _shutdown.Token).ConfigureAwait(false);
            if (client.Peer.ProtocolVersion < ProtocolInfo.FilesVersion) return;
            var reply = await client.RequestAsync(packet, default, _shutdown.Token).ConfigureAwait(false);
            if (reply.Packet is AckPacket) Db.Write((c, tx) => FilesRepo.MarkNotified(c, tx, fid));
        }
        catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IdentityMismatchException or IncompatibleVersionException or OperationCanceledException)
        {
            _log.Debug("files", "Aviso de documento pendiente de entrega");
        }
    }

    // ======================= Lado emisor y receptor (peticiones entrantes) =======================

    private Reply HandleFileOffer(PeerInfo peer, FileOfferPacket packet)
    {
        Reply Ack(string status) => new(new MessageAckPacket { MessageId = packet.FileId, Status = status });
        if (packet.Size > Options.MaxFileBytes) return Ack(MessageAckStatuses.TooLarge);
        var contact = Db.Read(c => ContactsRepo.GetByFingerprint(c, peer.Fingerprint));
        if (contact is null || contact.Blocked || contact.Relation != Relation.Authorized) return Ack(MessageAckStatuses.NotAuthorized);
        var name = FileNames.Sanitize(packet.Name);
        (bool Inserted, ChatMessage Message) result;
        try
        {
            result = Db.Write((c, tx) =>
            {
                var conv = MessagesRepo.GetOrCreateConversation(c, tx, contact.Id);
                var r = MessagesRepo.InsertIncoming(c, tx, conv.Id, packet.FileId, MessageKind.File, null, null, packet.Sequence,
                    DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(packet.SentAtUnixMs, 0, 253402300799999)));
                if (r.Inserted)
                    FilesRepo.Insert(c, tx, packet.FileId, conv.Id, MessageDirection.Incoming, Vault.Seal(name)!, packet.Size,
                        packet.Sha256.ToUpperInvariant(), FileState.Offered, null);
                return r;
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _log.Error("files", "Error de almacenamiento al recibir una oferta de documento", ex);
            return Ack(MessageAckStatuses.StorageError);
        }
        if (result.Inserted)
        {
            RaiseMessage(contact.Id, result.Message, isNew: true);
            RaiseNotice(new EngineNotice(NoticeKind.FileOffered, contact.Id, peer.DisplayName, $"{name} ({FileNames.FormatSize(packet.Size)})"));
        }
        return Ack(result.Inserted ? MessageAckStatuses.Stored : MessageAckStatuses.Duplicate);
    }

    private async Task<Reply> HandleFileGetAsync(PeerInfo peer, FileGetPacket packet, CancellationToken ct)
    {
        Reply Error(string code) => new(new ErrorPacket { Code = code, Message = code });
        var file = Db.Read(c => FilesRepo.Get(c, packet.FileId));
        if (file is null || file.Direction != MessageDirection.Outgoing) return Error(FileErrorCodes.Unavailable);
        var contact = ContactOfFile(file);
        // La autorización se comprueba en cada trozo con la identidad demostrada por TLS.
        if (contact is null || contact.Fingerprint != peer.Fingerprint || contact.Blocked || contact.Relation != Relation.Authorized)
            return Error(FileErrorCodes.Unavailable);
        if (file.State == FileState.Cancelled) return Error(FileErrorCodes.Cancelled);
        if (file.State is FileState.Rejected or FileState.Failed) return Error(FileErrorCodes.Unavailable);
        if (!Vault.IsUnlocked) return Error(FileErrorCodes.Locked);
        var key = GetFileKey(file.FileId);
        if (key is null) return Error(FileErrorCodes.Locked);
        if (packet.Offset >= file.Size) return Error(FileErrorCodes.Unavailable);
        byte[] data;
        try
        {
            data = await EncryptedFileStore.ReadRangeAsync(StoredPath(file.FileId), file.FileId, key, packet.Offset, packet.Length, file.Size, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            _log.Error("files", "No se pudo leer un documento para enviarlo", ex);
            return Error(FileErrorCodes.Unavailable);
        }
        long end = packet.Offset + data.Length;
        if (file.State == FileState.Offered || end > file.Transferred)
        {
            Db.Write((c, tx) =>
            {
                if (file.State == FileState.Offered) FilesRepo.SetState(c, tx, file.FileId, FileState.Transferring, null);
                FilesRepo.SetProgress(c, tx, file.FileId, end);
            });
            RaiseFile(file.FileId);
        }
        return new Reply(new FileDataPacket { FileId = file.FileId, Offset = packet.Offset, Last = end >= file.Size }, data);
    }

    private Reply HandleFileOutcome(PeerInfo peer, string fid, FileState state, MessageDirection expected)
    {
        var file = Db.Read(c => FilesRepo.Get(c, fid));
        var contact = file is null ? null : ContactOfFile(file);
        if (file is not null && file.Direction == expected && contact?.Fingerprint == peer.Fingerprint
            && file.State is not (FileState.Completed or FileState.Rejected or FileState.Cancelled))
        {
            if (state == FileState.Cancelled) { StopDownload(fid); TryDelete(PartPath(fid)); }
            Db.Write((c, tx) => FilesRepo.SetState(c, tx, fid, state, state == FileState.Cancelled ? "El remitente canceló el envío." : null));
            RaiseFile(fid);
        }
        return new Reply(new AckPacket { Status = AckStatuses.Ok });
    }

    private async Task<Reply> HandleFilePacketAsync(PeerInfo peer, PacketBase packet, CancellationToken ct)
    {
        if (peer.ProtocolVersion < ProtocolInfo.FilesVersion)
            return new Reply(new ErrorPacket { Code = ErrorCodes.UnsupportedOp, Message = "Documentos no negociados." });
        return packet switch
        {
            FileOfferPacket o => HandleFileOffer(peer, o),
            FileGetPacket g => await HandleFileGetAsync(peer, g, ct).ConfigureAwait(false),
            FileDonePacket d => HandleFileOutcome(peer, d.FileId, FileState.Completed, MessageDirection.Outgoing),
            FileRejectPacket r => HandleFileOutcome(peer, r.FileId, FileState.Rejected, MessageDirection.Outgoing),
            FileCancelPacket x => HandleFileOutcome(peer, x.FileId, FileState.Cancelled, MessageDirection.Incoming),
            _ => new Reply(new ErrorPacket { Code = ErrorCodes.UnsupportedOp, Message = "Operación no admitida." }),
        };
    }

    // ======================= Auxiliares =======================

    /// <summary>Clave del documento: en memoria mientras está desbloqueado; si no, se abre con la bóveda.</summary>
    private byte[]? GetFileKey(string fid)
    {
        if (_fileKeys.TryGetValue(fid, out var cached)) return cached;
        if (!Vault.IsUnlocked) return null;
        var sealedKey = Db.Read(c => FilesRepo.GetKey(c, fid));
        if (sealedKey is null) return null;
        try
        {
            var key = Convert.FromBase64String(Vault.Open(sealedKey)!);
            _fileKeys[fid] = key;
            return key;
        }
        catch (Exception ex) when (ex is VaultLockedException or CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Al bloquear: fuera de la memoria las claves de documentos y fuera del disco las copias abiertas.</summary>
    private void ForgetFileSecrets()
    {
        foreach (var key in _fileKeys.Values) CryptographicOperations.ZeroMemory(key);
        _fileKeys.Clear();
        foreach (var fid in _downloads.Keys.ToList()) StopDownload(fid);
        CleanupOpenedFiles();
    }

    private Contact? ContactOfFile(FileTransfer file) => Db.Read(c =>
    {
        var contactId = c.Scalar("SELECT contact_id FROM conversations WHERE id = $id;", null, ("$id", file.ConversationId));
        return contactId is long id ? ContactsRepo.Get(c, id) : null;
    });

    private FileTransfer RevealFile(FileTransfer f) => f with { Name = RevealName(f.Name) };

    private ChatMessage AttachFile(ChatMessage m)
    {
        if (m.Kind != MessageKind.File) return m;
        var f = Db.Read(c => FilesRepo.Get(c, m.Uid));
        return f is null ? m : m with { File = RevealFile(f) };
    }

    private void RaiseFile(string fid)
    {
        var file = Db.Read(c => FilesRepo.Get(c, fid));
        if (file is null) return;
        var (contactId, messageId) = Db.Read(c => (
            c.Scalar("SELECT contact_id FROM conversations WHERE id = $id;", null, ("$id", file.ConversationId)) as long? ?? 0,
            c.Scalar("SELECT id FROM messages WHERE conversation_id = $c AND uid = $u;", null, ("$c", file.ConversationId), ("$u", fid)) as long? ?? 0));
        FileChanged?.Invoke(this, new FileEvent(contactId, messageId, RevealFile(file)));
    }

    private void SetFileState(string fid, FileState state, string? error)
    {
        Db.Write((c, tx) => FilesRepo.SetState(c, tx, fid, state, error));
        RaiseFile(fid);
    }

    private void SetFileError(string fid, string? error)
    {
        Db.Write((c, tx) => c.Exec("UPDATE files SET error = $e, updated_at = $t WHERE fid = $f;", tx, ("$e", error), ("$t", Time.Now()), ("$f", fid)));
        RaiseFile(fid);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
