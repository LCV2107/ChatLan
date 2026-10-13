using System.Text;
using Microsoft.Data.Sqlite;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Security;
using MiniChat.Core.Stickers;
using MiniChat.Core.Transport;

namespace MiniChat.Core.Engine;

// Mensajería.
//
//  - El mensaje saliente se guarda (Pending) ANTES de transmitirse.
//  - Cada conversación tiene una cola que envía en orden de creación. Si un envío falla, los posteriores
//    quedan en espera; el reintento manual reenvía todos en orden. Así el receptor nunca ve un orden
//    distinto al del emisor.
//  - Entregado = el receptor respondió "stored"/"duplicate" tras guardar el mensaje (commit con synchronous=FULL).
//  - El uid del mensaje es estable: reintentar nunca crea duplicados en el receptor.
//  - Transmitido sin respuesta → "no confirmado". No transmitido → "fallido". Ninguno se da por entregado.
//  - El receptor comprueba SIEMPRE la autorización de la identidad TLS antes de guardar.
public sealed partial class ChatEngine : IInboundHandler
{
    public async Task<OperationResult<ChatMessage>> SendTextAsync(long contactId, string text)
    {
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) return new(false, null, "El mensaje está vacío.");
        if (text.Length > Options.MaxTextChars) return new(false, null, $"El mensaje supera el máximo de {Options.MaxTextChars} caracteres.");
        if (text.Contains('\0')) text = text.Replace("\0", "");
        return await EnqueueAsync(contactId, MessageKind.Text, text, null).ConfigureAwait(false);
    }

    public async Task<OperationResult<ChatMessage>> SendStickerAsync(long contactId, string stickerHash)
    {
        var blob = await Task.Run(() => Db.Read(c => StickersRepo.GetBlob(c, stickerHash))).ConfigureAwait(false);
        if (blob is null || !Stickers.BlobExists(stickerHash)) return new(false, null, "El sticker ya no está disponible.");
        var result = await EnqueueAsync(contactId, MessageKind.Sticker, null, stickerHash).ConfigureAwait(false);
        if (result.Success) await Task.Run(() => Db.Write((c, tx) => StickersRepo.MarkUsed(c, tx, stickerHash))).ConfigureAwait(false);
        return result;
    }

    private async Task<OperationResult<ChatMessage>> EnqueueAsync(long contactId, MessageKind kind, string? text, string? stickerHash)
    {
        var (message, error) = await Task.Run(() => Db.Write<(ChatMessage?, string?)>((c, tx) =>
        {
            var contact = ContactsRepo.Get(c, contactId, tx);
            if (contact is null) return (null, "El contacto no existe.");
            if (contact.Blocked) return (null, "El contacto está bloqueado.");
            if (contact.Relation != Relation.Authorized) return (null, "No tiene autorización para escribir a esta persona. Envíe una solicitud de chat.");
            var conv = MessagesRepo.GetOrCreateConversation(c, tx, contactId);
            return (MessagesRepo.InsertOutgoing(c, tx, conv.Id, kind, Vault.Seal(text), stickerHash), null);
        })).ConfigureAwait(false);
        if (message is null) return new(false, null, error);
        RaiseMessage(contactId, message, isNew: true);
        Kick(contactId);
        return new(true, message);
    }

    /// <summary>Reintento manual: reenvía en orden los mensajes fallidos o no confirmados (mismo uid).</summary>
    public async Task<OperationResult> RetryAsync(long contactId)
    {
        var (count, error) = await Task.Run(() => Db.Write<(int, string?)>((c, tx) =>
        {
            var contact = ContactsRepo.Get(c, contactId, tx);
            if (contact is null) return (0, "El contacto no existe.");
            if (contact.Blocked) return (0, "El contacto está bloqueado; los mensajes no se reenviarán.");
            if (contact.Relation != Relation.Authorized) return (0, "Ya no hay autorización para escribir a esta persona; los mensajes no se reenviarán.");
            var conv = MessagesRepo.GetConversation(c, contactId, tx);
            if (conv is null) return (0, null);
            int n = c.Exec("UPDATE messages SET status = 0, error = NULL, status_changed_at = $t WHERE conversation_id = $cv AND direction = 0 AND status IN (3, 4);",
                tx, ("$t", Time.Now()), ("$cv", conv.Id));
            return (n, null);
        })).ConfigureAwait(false);
        if (error is not null) return OperationResult.Fail(error);
        if (count == 0) return OperationResult.Ok("No hay mensajes pendientes de reenviar.");
        foreach (var m in await UndeliveredAsync(contactId).ConfigureAwait(false)) RaiseMessage(contactId, m, false);
        Kick(contactId);
        return OperationResult.Ok($"Reintentando {count} mensaje(s).");
    }

    private Task<List<ChatMessage>> UndeliveredAsync(long contactId) => Task.Run(() => Db.Read(c =>
    {
        var conv = MessagesRepo.GetConversation(c, contactId);
        return conv is null ? new List<ChatMessage>() : MessagesRepo.Undelivered(c, conv.Id);
    }));

    /// <summary>Arranca la cola de la conversación si no está ya en marcha.</summary>
    private void Kick(long contactId)
    {
        lock (_pumps)
        {
            if (_pumps.TryGetValue(contactId, out var running) && !running.IsCompleted) { _pumpAgain[contactId] = true; return; }
            _pumps[contactId] = Task.Run(() => PumpAsync(contactId));
        }
    }

    private readonly Dictionary<long, bool> _pumpAgain = new();

    private async Task PumpAsync(long contactId)
    {
        while (true)
        {
            try
            {
                await PumpOnceAsync(contactId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("messages", "Error en la cola de envío", ex);
            }
            lock (_pumps)
            {
                if (_pumpAgain.Remove(contactId)) continue;
                return;
            }
        }
    }

    private async Task PumpOnceAsync(long contactId)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            // Bloqueado no se puede descifrar lo que hay que enviar: queda en cola hasta el desbloqueo.
            if (!Vault.IsUnlocked) return;
            var (contact, next) = await Task.Run(() => Db.Read(c =>
            {
                var ct = ContactsRepo.Get(c, contactId);
                var conv = MessagesRepo.GetConversation(c, contactId);
                var head = conv is null ? null : MessagesRepo.Undelivered(c, conv.Id).FirstOrDefault();
                return (ct, head);
            })).ConfigureAwait(false);
            if (contact is null || next is null) return;
            // Un mensaje anterior bloqueado (fallido/no confirmado) detiene la cola para conservar el orden.
            if (next.Status is MessageStatus.Failed or MessageStatus.Unconfirmed) return;

            if (contact.Blocked || contact.Relation != Relation.Authorized)
            {
                await FailAllQueuedAsync(contactId, contact.Blocked ? "El contacto está bloqueado." : "Sin autorización para escribir a esta persona.").ConfigureAwait(false);
                return;
            }

            await SetStatusAsync(contactId, next.Id, MessageStatus.Sending, null).ConfigureAwait(false);
            var (status, error) = await TransmitAsync(contact, next).ConfigureAwait(false);
            await SetStatusAsync(contactId, next.Id, status, error).ConfigureAwait(false);
            if (status != MessageStatus.Delivered) return;
        }
    }

    private async Task FailAllQueuedAsync(long contactId, string reason)
    {
        var updated = await Task.Run(() => Db.Write((c, tx) =>
        {
            var conv = MessagesRepo.GetConversation(c, contactId, tx);
            if (conv is null) return new List<ChatMessage>();
            var queued = MessagesRepo.Undelivered(c, conv.Id, tx).Where(m => m.Status is MessageStatus.Pending or MessageStatus.Sending).ToList();
            foreach (var m in queued) MessagesRepo.SetStatus(c, tx, m.Id, MessageStatus.Failed, reason);
            return queued.Select(m => m.Id).ToList().Select(id => MessagesRepo.Get(c, id, tx)!).ToList();
        })).ConfigureAwait(false);
        foreach (var m in updated) RaiseMessage(contactId, m, false);
    }

    private async Task SetStatusAsync(long contactId, long messageId, MessageStatus status, string? error)
    {
        var m = await Task.Run(() => Db.Write((c, tx) =>
        {
            MessagesRepo.SetStatus(c, tx, messageId, status, error);
            return MessagesRepo.Get(c, messageId, tx)!;
        })).ConfigureAwait(false);
        RaiseMessage(contactId, m, false);
    }

    /// <summary>Transmite un mensaje y traduce el resultado a un estado visible honesto.</summary>
    private async Task<(MessageStatus Status, string? Error)> TransmitAsync(Contact contact, ChatMessage message)
    {
        if (_pool is null) return (MessageStatus.Failed, "La red no está iniciada.");
        try
        {
            return await TransmitUnlockedAsync(contact, message).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            // Se bloqueó mientras se preparaba el envío: vuelve a la cola y continuará al desbloquear.
            return (MessageStatus.Pending, null);
        }
    }

    private async Task<(MessageStatus Status, string? Error)> TransmitUnlockedAsync(Contact contact, ChatMessage message)
    {
        ReadOnlyMemory<byte> body = default;
        StickerInfo? sticker = null;
        if (message.Kind == MessageKind.Sticker)
        {
            var bytes = await Stickers.ReadBlobAsync(message.StickerHash!, _shutdown.Token).ConfigureAwait(false);
            var blob = Db.Read(c => StickersRepo.GetBlob(c, message.StickerHash!));
            if (bytes is null || blob is null) return (MessageStatus.Failed, "La imagen del sticker falta o está dañada en este equipo.");
            body = bytes;
            sticker = new StickerInfo { Hash = blob.Hash, Width = blob.Width, Height = blob.Height, Length = bytes.Length };
        }
        PacketBase packet;
        if (message.Kind == MessageKind.File)
        {
            // Oferta de documento: solo metadatos; los datos los pide el receptor si acepta.
            var file = Db.Read(c => FilesRepo.Get(c, message.Uid));
            if (file is null) return (MessageStatus.Failed, "El documento ya no existe en este equipo.");
            if (file.State == FileState.Cancelled) return (MessageStatus.Failed, "Envío cancelado.");
            packet = new FileOfferPacket
            {
                FileId = file.FileId,
                Name = Vault.Open(file.Name)!,
                Size = file.Size,
                Sha256 = file.Sha256,
                Sequence = message.Sequence,
                SentAtUnixMs = message.SenderTime.ToUnixTimeMilliseconds(),
            };
        }
        else packet = new MessagePacket
        {
            MessageId = message.Uid,
            Sequence = message.Sequence,
            SentAtUnixMs = message.SenderTime.ToUnixTimeMilliseconds(),
            Kind = message.Kind == MessageKind.Text ? MessageKinds.Text : MessageKinds.Sticker,
            Text = message.Text is null ? null : Vault.Open(message.Text),
            Sticker = sticker,
        };

        // Si una conexión reutilizada resultó estar muerta, se reintenta una vez con una nueva.
        // Es seguro porque el uid es el mismo y el receptor descarta duplicados.
        int throttled = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            PeerClient client;
            bool reused;
            try
            {
                (client, reused) = await _pool!.GetAsync(contact.Fingerprint, EndpointsFor(contact.Fingerprint, contact), _shutdown.Token).ConfigureAwait(false);
            }
            catch (IdentityMismatchException ex)
            {
                RaiseNotice(new EngineNotice(NoticeKind.IdentityMismatch, contact.Id, RevealName(contact.DisplayName), null));
                return (MessageStatus.Failed, Describe(ex, contact));
            }
            catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IncompatibleVersionException)
            {
                return (MessageStatus.Failed, "No se pudo conectar: " + Describe(ex, contact));
            }
            catch (OperationCanceledException)
            {
                return (MessageStatus.Failed, "Envío interrumpido al cerrar.");
            }
            MarkDirectContact(contact.Fingerprint);
            if (message.Kind == MessageKind.File && client.Peer.ProtocolVersion < ProtocolInfo.FilesVersion)
                return (MessageStatus.Failed, "El compañero usa una versión de MiniChat que no admite documentos. Pídale que la actualice.");

            var sent = new StrongBox<bool>();
            Frame reply;
            try
            {
                reply = await client.RequestAsync(packet, body, _shutdown.Token, sent).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PeerUnreachableException or OperationCanceledException)
            {
                await _pool.DropAsync(contact.Fingerprint).ConfigureAwait(false);
                if (reused && attempt == 0 && !_shutdown.IsCancellationRequested) continue;
                return sent.Value
                    ? (MessageStatus.Unconfirmed, "Se envió pero no llegó la confirmación de recepción. Puede reintentar sin riesgo de duplicarlo.")
                    : (MessageStatus.Failed, "La conexión se perdió antes de enviar el mensaje.");
            }

            if (reply.Packet is ErrorPacket { Code: ErrorCodes.RateLimited } && throttled++ < 5)
            {
                // Control de flujo: el receptor pide ir más despacio. No es un fallo de entrega.
                await Task.Delay(1100, _shutdown.Token).ConfigureAwait(false);
                attempt--;
                continue;
            }
            return reply.Packet switch
            {
                MessageAckPacket { Status: MessageAckStatuses.Stored or MessageAckStatuses.Duplicate } ack when ack.MessageId == message.Uid
                    => (MessageStatus.Delivered, null),
                MessageAckPacket { Status: MessageAckStatuses.NotAuthorized } => await HandleRevokedAsync(contact),
                MessageAckPacket { Status: MessageAckStatuses.TooLarge } => (MessageStatus.Failed, "El otro equipo rechazó el mensaje por su tamaño."),
                MessageAckPacket { Status: MessageAckStatuses.Invalid } => (MessageStatus.Failed, "El otro equipo consideró el mensaje no válido."),
                MessageAckPacket { Status: MessageAckStatuses.StorageError } => (MessageStatus.Failed, "El otro equipo no pudo guardar el mensaje (error de almacenamiento)."),
                ErrorPacket { Code: ErrorCodes.RateLimited } => (MessageStatus.Failed, "El otro equipo está limitando los envíos. Reintente en unos segundos."),
                _ => (MessageStatus.Unconfirmed, "Respuesta inesperada del otro equipo."),
            };
        }
        return (MessageStatus.Failed, "No se pudo enviar.");
    }

    private async Task<(MessageStatus, string?)> HandleRevokedAsync(Contact contact)
    {
        await Task.Run(() => Db.Write((c, tx) =>
        {
            var current = ContactsRepo.Get(c, contact.Id, tx);
            if (current?.Relation == Relation.Authorized) ContactsRepo.SetRelation(c, tx, contact.Id, Relation.RevokedByPeer);
        })).ConfigureAwait(false);
        RaiseContact(contact.Id);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        return (MessageStatus.Failed, "Esta persona ya no acepta sus mensajes. Para volver a chatear envíe una nueva solicitud.");
    }

    // ======================= Recepción =======================

    private async Task<Reply> HandleMessageAsync(PeerInfo peer, MessagePacket packet, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        Reply Ack(string status) => new(new MessageAckPacket { MessageId = packet.MessageId, Status = status });

        // 1) Autorización según la identidad demostrada por TLS (no según la interfaz del emisor).
        var contact = Db.Read(c => ContactsRepo.GetByFingerprint(c, peer.Fingerprint));
        if (contact is null || contact.Blocked) return Ack(MessageAckStatuses.NotAuthorized);
        bool implicitAccept = contact.Relation == Relation.OutgoingPending;
        if (contact.Relation != Relation.Authorized && !implicitAccept) return Ack(MessageAckStatuses.NotAuthorized);

        // 2) Validación del contenido.
        MessageKind kind;
        if (packet.Kind == MessageKinds.Sticker)
        {
            kind = MessageKind.Sticker;
            var s = packet.Sticker!;
            if (body.Length != s.Length) return Ack(MessageAckStatuses.Invalid);
            try
            {
                StickerProcessor.ValidateWirePng(body.Span, s.Hash, s.Width, s.Height, Options);
                await Stickers.WriteBlobFileAsync(s.Hash, body, ct).ConfigureAwait(false);
            }
            catch (StickerRejectedException ex)
            {
                _log.Warn("messages", $"Sticker rechazado de {LogFormat.Short(peer.Fingerprint)}: {ex.Message}");
                return Ack(MessageAckStatuses.Invalid);
            }
            catch (IOException ex)
            {
                _log.Error("messages", "No se pudo guardar un sticker recibido", ex);
                return Ack(MessageAckStatuses.StorageError);
            }
        }
        else
        {
            kind = MessageKind.Text;
            if (body.Length != 0) return Ack(MessageAckStatuses.Invalid);
        }

        // 3) Guardado atómico + deduplicación por uid. El ack se envía solo después del commit.
        (bool Inserted, ChatMessage Message, bool BecameAuthorized) result;
        try
        {
            result = Db.Write((c, tx) =>
            {
                var current = ContactsRepo.Get(c, contact.Id, tx)!;
                if (current.Blocked) return (false, null!, false);
                bool becameAuthorized = false;
                if (current.Relation == Relation.OutgoingPending)
                {
                    // Nuestra solicitud fue aceptada pero la aceptación no llegó: el mensaje la confirma.
                    var pending = RequestsRepo.GetPending(c, contact.Id, RequestDirection.Outgoing, tx);
                    if (pending is not null) RequestsRepo.Resolve(c, tx, pending.Id, RequestStatus.Accepted);
                    ContactsRepo.SetRelation(c, tx, contact.Id, Relation.Authorized);
                    becameAuthorized = true;
                }
                else if (current.Relation != Relation.Authorized)
                {
                    return (false, null!, false);
                }
                if (packet.Sticker is { } s)
                    StickersRepo.InsertBlobIfMissing(c, tx, new StickerBlob(s.Hash, s.Width, s.Height, s.Length));
                var conv = MessagesRepo.GetOrCreateConversation(c, tx, contact.Id);
                var (inserted, msg) = MessagesRepo.InsertIncoming(c, tx, conv.Id, packet.MessageId, kind, Vault.Seal(packet.Text), packet.Sticker?.Hash,
                    packet.Sequence, DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(packet.SentAtUnixMs, 0, 253402300799999)));
                return (inserted, msg, becameAuthorized);
            });
        }
        catch (SqliteException ex)
        {
            _log.Error("messages", "Error de almacenamiento al recibir un mensaje", ex);
            return Ack(MessageAckStatuses.StorageError);
        }
        if (result.Message is null) return Ack(MessageAckStatuses.NotAuthorized);

        if (result.BecameAuthorized)
        {
            RaiseContact(contact.Id);
            PeopleChanged?.Invoke(this, EventArgs.Empty);
        }
        if (result.Inserted)
        {
            // Solo el evento de mensaje: la interfaz actualiza contadores sin recargar la lista de personas.
            RaiseMessage(contact.Id, result.Message, isNew: true);
            var preview = kind == MessageKind.Sticker ? "[Sticker]" : Preview(packet.Text!);
            RaiseNotice(new EngineNotice(NoticeKind.NewMessage, contact.Id, peer.DisplayName, preview));
        }
        return Ack(result.Inserted ? MessageAckStatuses.Stored : MessageAckStatuses.Duplicate);
    }

    private static string Preview(string text)
    {
        var one = text.Replace('\n', ' ').Trim();
        return one.Length <= 80 ? one : one[..80] + "…";
    }

    // ======================= Despacho de peticiones entrantes =======================

    Task IInboundHandler.OnPeerHelloAsync(PeerInfo peer, CancellationToken ct)
    {
        MarkDirectContact(peer.Fingerprint);
        // Un contacto conocido que conecta desde otra IP se actualiza (no se crea otro contacto).
        long? updated = Db.Write((c, tx) =>
        {
            var existing = ContactsRepo.GetByFingerprint(c, peer.Fingerprint, tx);
            if (existing is null) return (long?)null;
            ContactsRepo.Upsert(c, tx, peer.Fingerprint, SealName(peer.DisplayName)!, SealName(peer.MachineName)!, peer.RemoteEndPoint.Address.ToString(), peer.ListenPort);
            return existing.Id;
        });
        if (updated is long id) RaiseContact(id);
        return Task.CompletedTask;
    }

    async Task<Reply> IInboundHandler.HandleAsync(PeerInfo peer, Frame frame, CancellationToken ct)
    {
        MarkDirectContact(peer.Fingerprint);
        return frame.Packet switch
        {
            ChatRequestPacket r => HandleChatRequest(peer, r),
            RequestDecisionPacket d => HandleRequestDecision(peer, d),
            CancelRequestPacket x => HandleCancelRequest(peer, x),
            MessagePacket m => await HandleMessageAsync(peer, m, frame.Body, ct).ConfigureAwait(false),
            FileOfferPacket or FileGetPacket or FileDonePacket or FileRejectPacket or FileCancelPacket
                => await HandleFilePacketAsync(peer, frame.Packet, ct).ConfigureAwait(false),
            _ => new Reply(new ErrorPacket { Code = ErrorCodes.UnsupportedOp, Message = "Operación no admitida." }),
        };
    }

    // ======================= Historial: exportar y borrar =======================

    /// <summary>
    /// Exporta la conversación a texto UTF-8. Cada sticker se escribe como
    /// "[Sticker: &lt;12 primeros caracteres del hash&gt;.png (ancho×alto)]" y, si hay stickers, sus PNG se copian
    /// a una carpeta "&lt;nombre&gt;_stickers" junto al archivo.
    /// </summary>
    public Task<ExportResult> ExportAsync(long contactId, string textFile) => Task.Run(() =>
    {
        if (!Vault.IsUnlocked) throw new VaultLockedException();
        var contact = Reveal(Db.Read(c => ContactsRepo.Get(c, contactId))) ?? throw new InvalidOperationException("El contacto no existe.");
        var conv = Db.Read(c => MessagesRepo.GetConversation(c, contactId));
        var stickerDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(textFile))!, Path.GetFileNameWithoutExtension(textFile) + "_stickers");
        int count = 0;
        var stickers = new HashSet<string>();
        using (var w = new StreamWriter(textFile, false, new UTF8Encoding(true)))
        {
            w.WriteLine($"MiniChat LAN — Conversación con {contact.DisplayName} ({contact.MachineName})");
            w.WriteLine($"Exportada el {DateTimeOffset.Now:yyyy-MM-dd HH:mm} desde {MachineName}. Copia local de este equipo.");
            w.WriteLine("Los stickers aparecen como [Sticker: archivo.png (ancho×alto)]; las imágenes están en la carpeta indicada al final.");
            w.WriteLine(new string('-', 60));
            long? after = 0;
            while (conv is not null)
            {
                var page = Db.Read(c => MessagesRepo.After(c, conv.Id, after!.Value, 500)).Select(x => Reveal(x)!).ToList();
                if (page.Count == 0) break;
                foreach (var m in page)
                {
                    var who = m.Direction == MessageDirection.Outgoing ? DisplayName : contact.DisplayName;
                    var when = m.LocalTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    string content;
                    if (m.Kind == MessageKind.File && m.File is { } f)
                    {
                        content = $"[Archivo: {f.Name} ({MiniChat.Core.Files.FileNames.FormatSize(f.Size)})]";
                    }
                    else if (m.Kind == MessageKind.Sticker && m.StickerHash is { } h)
                    {
                        var blob = Db.Read(c => StickersRepo.GetBlob(c, h));
                        content = $"[Sticker: {h[..12].ToLowerInvariant()}.png ({blob?.Width}×{blob?.Height})]";
                        stickers.Add(h);
                    }
                    else content = (m.Text ?? "").Replace("\n", "\n    ");
                    var state = m.Direction == MessageDirection.Outgoing && m.Status != MessageStatus.Delivered ? $" ({StatusText(m.Status)})" : "";
                    w.WriteLine($"[{when}] {who}{state}: {content}");
                    count++;
                }
                after = page[^1].Id;
            }
            if (stickers.Count > 0) w.WriteLine($"{Environment.NewLine}Imágenes de stickers: {stickerDir}");
        }
        if (stickers.Count > 0)
        {
            Directory.CreateDirectory(stickerDir);
            foreach (var h in stickers)
            {
                // En disco están cifrados: se exporta el PNG descifrado.
                var png = Stickers.ReadBlobAsync(h, CancellationToken.None).GetAwaiter().GetResult();
                if (png is not null) File.WriteAllBytes(Path.Combine(stickerDir, h[..12].ToLowerInvariant() + ".png"), png);
            }
        }
        return new ExportResult(textFile, stickers.Count > 0 ? stickerDir : null, count, stickers.Count);
    });

    public static string StatusText(MessageStatus s) => s switch
    {
        MessageStatus.Pending => "pendiente",
        MessageStatus.Sending => "enviando",
        MessageStatus.Delivered => "entregado",
        MessageStatus.Unconfirmed => "no confirmado",
        MessageStatus.Failed => "fallido",
        _ => "recibido",
    };

    /// <summary>Borra el historial LOCAL de una conversación. No afecta a la copia del compañero.</summary>
    public async Task<int> ClearHistoryAsync(long contactId)
    {
        var removedFiles = new List<string>();
        int n = await Task.Run(() => Db.Write((c, tx) =>
        {
            var conv = MessagesRepo.GetConversation(c, contactId, tx);
            if (conv is null) return 0;
            foreach (var fid in FilesRepo.DeleteForConversation(c, tx, conv.Id)) removedFiles.Add(fid);
            return MessagesRepo.DeleteAll(c, tx, conv.Id);
        })).ConfigureAwait(false);
        foreach (var fid in removedFiles)
        {
            StopDownload(fid);
            TryDelete(StoredPath(fid));
            TryDelete(PartPath(fid));
        }
        await Stickers.CollectGarbageAsync().ConfigureAwait(false);
        RaiseContact(contactId);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        return n;
    }
}
