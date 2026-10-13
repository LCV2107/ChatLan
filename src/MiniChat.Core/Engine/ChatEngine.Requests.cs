using System.Net;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Protocol;
using MiniChat.Core.Transport;

namespace MiniChat.Core.Engine;

// Solicitudes de chat y autorizaciones.
//
// Invariantes:
//  - Solo se pueden intercambiar mensajes en estado Authorized, y el receptor lo comprueba siempre.
//  - Authorized exige consentimiento de ambos: quien solicita (al enviar) y quien acepta.
//  - Rechazar no autoriza; desbloquear deja la relación en None (hace falta una solicitud nueva).
//  - Bloquear revoca la autorización, cancela solicitudes y no borra el historial.
//  - Cada transición se decide dentro de UNA transacción, así que no quedan estados a medias.
public sealed partial class ChatEngine
{
    // ======================= Acciones del usuario =======================

    /// <summary>Envía (o reenvía) una solicitud de chat a una identidad descubierta o conocida.</summary>
    public async Task<OperationResult> SendChatRequestAsync(string fingerprint, CancellationToken ct = default)
    {
        if (_pool is null) return OperationResult.Fail("La red no está iniciada.");
        if (fingerprint == Identity.Fingerprint) return OperationResult.Fail("No puede enviarse una solicitud a sí mismo.");
        var contact = await GetContactByFingerprintAsync(fingerprint).ConfigureAwait(false);
        if (contact?.Blocked == true) return OperationResult.Fail("Este contacto está bloqueado. Desbloquéelo antes de enviar una solicitud.");
        if (contact?.Relation == Relation.Authorized) return OperationResult.Ok("Ya pueden chatear.");
        if (contact?.Relation == Relation.IncomingPending) return OperationResult.Fail("Esta persona ya le envió una solicitud: acéptela o recházela.");

        // Una solicitud pendiente se reenvía con el mismo identificador (el receptor la trata como duplicada).
        string rid = contact is null ? NewId()
            : (await Task.Run(() => Db.Read(c => RequestsRepo.GetPending(c, contact.Id, RequestDirection.Outgoing))).ConfigureAwait(false))?.Id ?? NewId();

        PeerClient client;
        try
        {
            (client, _) = await _pool.GetAsync(fingerprint, EndpointsFor(fingerprint, contact), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IncompatibleVersionException or IdentityMismatchException)
        {
            return OperationResult.Fail(Describe(ex, contact));
        }
        MarkDirectContact(fingerprint);

        Frame reply;
        try
        {
            reply = await client.RequestAsync(new ChatRequestPacket { RequestId = rid }, default, ct).ConfigureAwait(false);
        }
        catch (PeerUnreachableException ex)
        {
            await _pool.DropAsync(fingerprint).ConfigureAwait(false);
            return OperationResult.Fail("La solicitud no se pudo entregar: " + ex.Message);
        }
        if (reply.Packet is ErrorPacket err)
            return OperationResult.Fail(err.Code == ErrorCodes.RateLimited ? "Demasiados intentos. Espere un momento." : "El otro equipo rechazó la petición (" + err.Code + ").");
        if (reply.Packet is not RequestStatusPacket status || status.RequestId != rid)
            return OperationResult.Fail("Respuesta inesperada del otro equipo.");

        var peer = client.Peer;
        var (result, contactId) = await Task.Run(() => Db.Write((c, tx) =>
        {
            var ct2 = ContactsRepo.Upsert(c, tx, fingerprint, SealName(peer.DisplayName)!, SealName(peer.MachineName)!, peer.RemoteEndPoint.Address.ToString(), peer.ListenPort);
            if (ct2.Blocked) return (OperationResult.Fail("Este contacto está bloqueado."), ct2.Id);
            switch (status.Status)
            {
                case RequestStatuses.Pending:
                    RequestsRepo.Insert(c, tx, rid, ct2.Id, RequestDirection.Outgoing);
                    if (ct2.Relation == Relation.IncomingPending)
                    {
                        // Solicitudes cruzadas: mientras la nuestra viajaba llegó la suya. Ambos consintieron.
                        // Se notifica nuestra aceptación (idempotente) para que el otro lado converja siempre.
                        RequestsRepo.Resolve(c, tx, rid, RequestStatus.Accepted);
                        var incoming = RequestsRepo.GetPending(c, ct2.Id, RequestDirection.Incoming, tx);
                        if (incoming is not null)
                        {
                            RequestsRepo.Resolve(c, tx, incoming.Id, RequestStatus.Accepted, decisionDelivered: false);
                            Interlocked.Increment(ref _undeliveredDecisions);
                        }
                        ContactsRepo.SetRelation(c, tx, ct2.Id, Relation.Authorized);
                        return (OperationResult.Ok("Ambos se enviaron solicitud. Ya pueden chatear."), ct2.Id);
                    }
                    ContactsRepo.SetRelation(c, tx, ct2.Id, Relation.OutgoingPending);
                    return (OperationResult.Ok("Solicitud enviada. Esperando respuesta."), ct2.Id);
                case RequestStatuses.Accepted:
                case RequestStatuses.AlreadyAuthorized:
                    // Aceptada (p. ej. solicitudes simultáneas, o la aceptación se perdió antes y este reenvío la recupera).
                    RequestsRepo.Insert(c, tx, rid, ct2.Id, RequestDirection.Outgoing);
                    RequestsRepo.Resolve(c, tx, rid, RequestStatus.Accepted);
                    CloseIncomingPending(c, tx, ct2.Id, RequestStatus.Accepted);
                    ContactsRepo.SetRelation(c, tx, ct2.Id, Relation.Authorized);
                    return (OperationResult.Ok("Solicitud aceptada. Ya pueden chatear."), ct2.Id);
                case RequestStatuses.Rejected:
                    RequestsRepo.Insert(c, tx, rid, ct2.Id, RequestDirection.Outgoing);
                    RequestsRepo.Resolve(c, tx, rid, RequestStatus.Rejected);
                    ContactsRepo.SetRelation(c, tx, ct2.Id, Relation.RejectedByPeer);
                    return (OperationResult.Fail("La solicitud fue rechazada."), ct2.Id);
                case RequestStatuses.RateLimited:
                    return (OperationResult.Fail("El otro equipo no admite más solicitudes suyas por ahora. Inténtelo más tarde."), ct2.Id);
                default: // Unavailable
                    RequestsRepo.CancelAllPending(c, tx, ct2.Id);
                    if (ct2.Relation == Relation.OutgoingPending) ContactsRepo.SetRelation(c, tx, ct2.Id, Relation.None);
                    return (OperationResult.Fail("Esta persona no está aceptando solicitudes."), ct2.Id);
            }
        })).ConfigureAwait(false);
        RaiseContact(contactId);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() => RedeliverDecisionsAsync(fingerprint));
        return result;
    }

    public Task<OperationResult> AcceptAsync(long contactId) => DecideAsync(contactId, accept: true);

    public Task<OperationResult> RejectAsync(long contactId) => DecideAsync(contactId, accept: false);

    private async Task<OperationResult> DecideAsync(long contactId, bool accept)
    {
        var outcome = await Task.Run(() => Db.Write<(bool Ok, string Msg, string? Rid)>((c, tx) =>
        {
            var contact = ContactsRepo.Get(c, contactId, tx);
            if (contact is null) return (Ok: false, Msg: "El contacto no existe.", Rid: (string?)null);
            if (contact.Blocked) return (false, "El contacto está bloqueado.", null);
            var req = RequestsRepo.GetPending(c, contactId, RequestDirection.Incoming, tx);
            if (contact.Relation != Relation.IncomingPending || req is null)
                return (false, "Ya no hay una solicitud pendiente de esta persona.", null);
            RequestsRepo.Resolve(c, tx, req.Id, accept ? RequestStatus.Accepted : RequestStatus.Rejected, decisionDelivered: false);
            ContactsRepo.SetRelation(c, tx, contactId, accept ? Relation.Authorized : Relation.RejectedByMe);
            return (true, accept ? "Solicitud aceptada." : "Solicitud rechazada.", req.Id);
        })).ConfigureAwait(false);
        if (!outcome.Ok) return OperationResult.Fail(outcome.Msg);
        RaiseContact(contactId);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        // La decisión ya es firme localmente. Se intenta entregar; si falla, se reintenta cuando el par reaparezca
        // o cuando vuelva a enviar su solicitud (recibirá el estado actual).
        var delivered = await DeliverDecisionAsync(outcome.Rid!).ConfigureAwait(false);
        if (!delivered) Interlocked.Increment(ref _undeliveredDecisions);
        return OperationResult.Ok(delivered ? outcome.Msg : outcome.Msg + " Se avisará al otro equipo cuando esté disponible.");
    }

    public async Task<OperationResult> CancelRequestAsync(long contactId)
    {
        var rid = await Task.Run(() => Db.Write((c, tx) =>
        {
            var contact = ContactsRepo.Get(c, contactId, tx);
            var req = RequestsRepo.GetPending(c, contactId, RequestDirection.Outgoing, tx);
            if (contact?.Relation != Relation.OutgoingPending || req is null) return null;
            RequestsRepo.Resolve(c, tx, req.Id, RequestStatus.Cancelled);
            ContactsRepo.SetRelation(c, tx, contactId, Relation.None);
            return req.Id;
        })).ConfigureAwait(false);
        if (rid is null) return OperationResult.Fail("No hay una solicitud pendiente que cancelar.");
        RaiseContact(contactId);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        // Aviso al otro equipo (mejor esfuerzo). Si no llega y el otro acepta después, recibirá "cancelled".
        var contact = await GetContactAsync(contactId).ConfigureAwait(false);
        if (contact is not null) _ = SendBestEffortAsync(contact, new CancelRequestPacket { RequestId = rid });
        return OperationResult.Ok("Solicitud cancelada.");
    }

    /// <summary>Bloquea: revoca la autorización, cancela solicitudes y rechaza todo lo posterior. Conserva el historial.</summary>
    public async Task<OperationResult> BlockAsync(long contactId)
    {
        await Task.Run(() => Db.Write((c, tx) =>
        {
            ContactsRepo.SetBlocked(c, tx, contactId, true);
            ContactsRepo.SetRelation(c, tx, contactId, Relation.None);
            RequestsRepo.CancelAllPending(c, tx, contactId);
        })).ConfigureAwait(false);
        var contact = await GetContactAsync(contactId).ConfigureAwait(false);
        if (contact is not null && _pool is not null) await _pool.DropAsync(contact.Fingerprint).ConfigureAwait(false);
        RaiseContact(contactId);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Contacto bloqueado. El historial se conserva.");
    }

    /// <summary>Desbloquea sin autorizar: para volver a chatear hace falta una solicitud nueva y su aceptación.</summary>
    public async Task<OperationResult> UnblockAsync(long contactId)
    {
        await Task.Run(() => Db.Write((c, tx) =>
        {
            ContactsRepo.SetBlocked(c, tx, contactId, false);
            ContactsRepo.SetRelation(c, tx, contactId, Relation.None);
        })).ConfigureAwait(false);
        RaiseContact(contactId);
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Contacto desbloqueado. Para chatear, envíe una nueva solicitud.");
    }

    /// <summary>Conexión manual por IP: verifica que es MiniChat y obtiene su identidad, con los mismos controles.</summary>
    public async Task<OperationResult<PersonView>> ConnectManualAsync(string ipText, int? port = null, CancellationToken ct = default)
    {
        if (_pool is null) return new(false, null, "La red no está iniciada.");
        if (!IPAddress.TryParse((ipText ?? "").Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return new(false, null, "Escriba una dirección IPv4 válida, por ejemplo 192.168.1.25.");
        if (Policy.InterfaceFor(ip) is null) return new(false, null, Policy.ExplainRejection(ip));
        var endpoint = new IPEndPoint(ip, port ?? Options.TcpPort);
        try
        {
            await using var client = await _pool.ProbeAsync(endpoint, ct).ConfigureAwait(false);
            var peer = client.Peer;
            if (peer.Fingerprint == Identity.Fingerprint) return new(false, null, "Esa dirección es este mismo equipo y perfil.");
            _manualEndpoints[peer.Fingerprint] = new IPEndPoint(ip, peer.ListenPort);
            MarkDirectContact(peer.Fingerprint);
            var existing = await GetContactByFingerprintAsync(peer.Fingerprint).ConfigureAwait(false);
            if (existing is not null)
            {
                await Task.Run(() => Db.Write((c, tx) => ContactsRepo.Upsert(c, tx, peer.Fingerprint, SealName(peer.DisplayName)!, SealName(peer.MachineName)!, ip.ToString(), peer.ListenPort))).ConfigureAwait(false);
                RaiseContact(existing.Id);
            }
            PeopleChanged?.Invoke(this, EventArgs.Empty);
            var view = new PersonView(peer.Fingerprint, existing?.Id, peer.DisplayName, peer.MachineName, true, endpoint,
                existing?.Relation ?? Relation.None, existing?.Blocked ?? false, 0, true, null, null, false);
            return new(true, view, $"Encontrado: {peer.DisplayName} ({peer.MachineName}).");
        }
        catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IncompatibleVersionException)
        {
            return new(false, null, Describe(ex, null));
        }
    }

    // ======================= Entrega de decisiones =======================

    private async Task<bool> DeliverDecisionAsync(string rid)
    {
        if (_pool is null) return false;
        var (req, contact) = await Task.Run(() => Db.Write((c, tx) =>
        {
            var r = RequestsRepo.Get(c, rid, tx);
            if (r is not null) RequestsRepo.RecordDeliveryAttempt(c, tx, rid);
            return (r, r is null ? null : ContactsRepo.Get(c, r.ContactId, tx));
        })).ConfigureAwait(false);
        if (req is null || contact is null || req.DecisionDelivered) return true;
        if (req.Status is not (RequestStatus.Accepted or RequestStatus.Rejected)) return true;

        var packet = new RequestDecisionPacket
        {
            RequestId = rid,
            Decision = req.Status == RequestStatus.Accepted ? DecisionValues.Accepted : DecisionValues.Rejected,
        };
        Frame reply;
        try
        {
            var (client, _) = await _pool.GetAsync(contact.Fingerprint, EndpointsFor(contact.Fingerprint, contact), _shutdown.Token).ConfigureAwait(false);
            reply = await client.RequestAsync(packet, default, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or NetworkNotAllowedException or IncompatibleVersionException or IdentityMismatchException or OperationCanceledException)
        {
            _log.Info("requests", $"Decisión para {LogFormat.Short(contact.Fingerprint)} pendiente de entrega: {ex.GetType().Name}");
            await _pool.DropAsync(contact.Fingerprint).ConfigureAwait(false);
            return false;
        }
        if (reply.Packet is not AckPacket ack) return false;

        await Task.Run(() => Db.Write((c, tx) =>
        {
            RequestsRepo.MarkDelivered(c, tx, rid);
            if (ack.Status == AckStatuses.Cancelled && req.Status == RequestStatus.Accepted)
            {
                // El solicitante retiró su solicitud antes de recibir la aceptación: no hay consentimiento mutuo.
                var current = ContactsRepo.Get(c, contact.Id, tx);
                if (current?.Relation == Relation.Authorized) ContactsRepo.SetRelation(c, tx, contact.Id, Relation.None);
                RequestsRepo.Resolve(c, tx, rid, RequestStatus.Cancelled);
            }
        })).ConfigureAwait(false);
        if (ack.Status == AckStatuses.Cancelled) RaiseContact(contact.Id);
        return true;
    }

    /// <summary>Reintenta entregar decisiones pendientes (todas o solo las de un par), respetando un intervalo mínimo.</summary>
    private async Task RedeliverDecisionsAsync(string? fingerprint)
    {
        try
        {
            var pending = Db.Read(c => RequestsRepo.UndeliveredDecisions(c)
                .Select(r => (Req: r, Contact: ContactsRepo.Get(c, r.ContactId)))
                .Where(x => x.Contact is not null && (fingerprint is null || x.Contact.Fingerprint == fingerprint))
                .ToList());
            foreach (var (req, contact) in pending)
            {
                if (req.LastDeliveryAttemptAt is { } last && DateTimeOffset.UtcNow - last < Options.DecisionRedeliveryMinInterval) continue;
                if (!IsOnline(contact!.Fingerprint)) continue;
                await DeliverDecisionAsync(req.Id).ConfigureAwait(false);
            }
            Volatile.Write(ref _undeliveredDecisions, Db.Read(c => RequestsRepo.UndeliveredDecisions(c).Count));
        }
        catch (Exception ex)
        {
            _log.Warn("requests", "Error reentregando decisiones", ex);
        }
    }

    private async Task SendBestEffortAsync(Contact contact, PacketBase packet)
    {
        if (_pool is null) return;
        try
        {
            var (client, _) = await _pool.GetAsync(contact.Fingerprint, EndpointsFor(contact.Fingerprint, contact), _shutdown.Token).ConfigureAwait(false);
            await client.RequestAsync(packet, default, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("requests", $"Aviso no entregado ({packet.GetType().Name}): {ex.GetType().Name}");
        }
    }

    // ======================= Lado receptor =======================

    private Reply HandleChatRequest(PeerInfo peer, ChatRequestPacket packet)
    {
        var (status, contactId, notify) = Db.Write((c, tx) =>
        {
            var existing = ContactsRepo.GetByFingerprint(c, peer.Fingerprint, tx);
            if (existing?.Blocked == true) return (RequestStatuses.Unavailable, existing.Id, false);

            // Límite de solicitudes para evitar avisos continuos.
            var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
            bool duplicateOfPending = existing?.Relation == Relation.IncomingPending;
            if (!duplicateOfPending && existing?.Relation != Relation.Authorized && existing?.Relation != Relation.OutgoingPending)
            {
                if (RequestsRepo.CountIncoming(c, tx, null, hourAgo) >= Options.MaxIncomingRequestsPerHour ||
                    RequestsRepo.CountIncoming(c, tx, peer.Fingerprint, hourAgo) >= Options.MaxRequestsPerPeerPerHour)
                    return (RequestStatuses.RateLimited, existing?.Id ?? 0, false);
                if (existing?.Relation == Relation.RejectedByMe && DateTimeOffset.UtcNow - existing.RelationChangedAt < Options.RequestCooldownAfterReject)
                    return (RequestStatuses.Rejected, existing.Id, false);
            }

            var contact = ContactsRepo.Upsert(c, tx, peer.Fingerprint, SealName(peer.DisplayName)!, SealName(peer.MachineName)!, peer.RemoteEndPoint.Address.ToString(), peer.ListenPort);
            switch (contact.Relation)
            {
                case Relation.Authorized:
                    // Ya autorizado (p. ej. nuestra aceptación no le llegó): se lo confirmamos.
                    MarkDecisionDeliveredFor(c, tx, contact.Id);
                    return (RequestStatuses.AlreadyAuthorized, contact.Id, false);
                case Relation.IncomingPending:
                    // Duplicada: no se vuelve a avisar al usuario.
                    var pending = RequestsRepo.GetPending(c, contact.Id, RequestDirection.Incoming, tx);
                    if (pending is null) RequestsRepo.Insert(c, tx, packet.RequestId, contact.Id, RequestDirection.Incoming);
                    return (RequestStatuses.Pending, contact.Id, false);
                case Relation.OutgoingPending:
                    // Solicitudes simultáneas: ambos pidieron chatear con el otro → consentimiento mutuo.
                    var mine = RequestsRepo.GetPending(c, contact.Id, RequestDirection.Outgoing, tx);
                    if (mine is not null) RequestsRepo.Resolve(c, tx, mine.Id, RequestStatus.Accepted);
                    RequestsRepo.Insert(c, tx, packet.RequestId, contact.Id, RequestDirection.Incoming);
                    RequestsRepo.Resolve(c, tx, packet.RequestId, RequestStatus.Accepted);
                    ContactsRepo.SetRelation(c, tx, contact.Id, Relation.Authorized);
                    return (RequestStatuses.Accepted, contact.Id, true);
                default:
                    RequestsRepo.LogIncoming(c, tx, peer.Fingerprint);
                    RequestsRepo.Insert(c, tx, packet.RequestId, contact.Id, RequestDirection.Incoming);
                    ContactsRepo.SetRelation(c, tx, contact.Id, Relation.IncomingPending);
                    return (RequestStatuses.Pending, contact.Id, true);
            }
        });
        if (contactId != 0)
        {
            RaiseContact(contactId);
            PeopleChanged?.Invoke(this, EventArgs.Empty);
        }
        if (notify)
            RaiseNotice(new EngineNotice(status == RequestStatuses.Accepted ? NoticeKind.RequestAccepted : NoticeKind.IncomingRequest,
                contactId, peer.DisplayName, null));
        _log.Info("requests", $"Solicitud de {LogFormat.Short(peer.Fingerprint)}: {status}");
        return new Reply(new RequestStatusPacket { RequestId = packet.RequestId, Status = status });
    }

    private static void MarkDecisionDeliveredFor(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, long contactId) =>
        c.Exec("UPDATE requests SET decision_delivered = 1 WHERE contact_id = $c AND direction = 1 AND status = 1;", tx, ("$c", contactId));

    private static void CloseIncomingPending(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, long contactId, RequestStatus status)
    {
        var incoming = RequestsRepo.GetPending(c, contactId, RequestDirection.Incoming, tx);
        if (incoming is not null) RequestsRepo.Resolve(c, tx, incoming.Id, status);
    }

    private Reply HandleRequestDecision(PeerInfo peer, RequestDecisionPacket packet)
    {
        var (ack, contactId, kind) = Db.Write((c, tx) =>
        {
            var contact = ContactsRepo.GetByFingerprint(c, peer.Fingerprint, tx);
            if (contact is null) return (AckStatuses.UnknownRequest, 0L, (NoticeKind?)null);
            var req = RequestsRepo.Get(c, packet.RequestId, tx);
            bool ours = req is not null && req.ContactId == contact.Id && req.Direction == RequestDirection.Outgoing;
            bool accepted = packet.Decision == DecisionValues.Accepted;

            if (contact.Blocked) return (AckStatuses.Cancelled, contact.Id, null);
            if (contact.Relation == Relation.Authorized && accepted) return (AckStatuses.Ok, contact.Id, null); // idempotente
            if (contact.Relation == Relation.OutgoingPending && (ours || req is null))
            {
                var pending = RequestsRepo.GetPending(c, contact.Id, RequestDirection.Outgoing, tx);
                if (pending is not null) RequestsRepo.Resolve(c, tx, pending.Id, accepted ? RequestStatus.Accepted : RequestStatus.Rejected);
                ContactsRepo.SetRelation(c, tx, contact.Id, accepted ? Relation.Authorized : Relation.RejectedByPeer);
                return (AckStatuses.Ok, contact.Id, accepted ? NoticeKind.RequestAccepted : NoticeKind.RequestRejected);
            }
            if (ours && req!.Status is RequestStatus.Cancelled or RequestStatus.Expired) return (AckStatuses.Cancelled, contact.Id, null);
            if (contact.Relation is Relation.None or Relation.RejectedByMe or Relation.RevokedByPeer) return (AckStatuses.Cancelled, contact.Id, null);
            return (AckStatuses.UnknownRequest, contact.Id, null);
        });
        if (kind is { } k)
        {
            RaiseContact(contactId);
            PeopleChanged?.Invoke(this, EventArgs.Empty);
            RaiseNotice(new EngineNotice(k, contactId, peer.DisplayName, null));
        }
        return new Reply(new AckPacket { Status = ack });
    }

    private Reply HandleCancelRequest(PeerInfo peer, CancelRequestPacket packet)
    {
        long changed = Db.Write((c, tx) =>
        {
            var contact = ContactsRepo.GetByFingerprint(c, peer.Fingerprint, tx);
            if (contact is null) return 0L;
            var req = RequestsRepo.Get(c, packet.RequestId, tx);
            if (req is null || req.ContactId != contact.Id || req.Direction != RequestDirection.Incoming) return 0L;
            if (req.Status == RequestStatus.Pending)
            {
                RequestsRepo.Resolve(c, tx, req.Id, RequestStatus.Cancelled);
                if (contact.Relation == Relation.IncomingPending) ContactsRepo.SetRelation(c, tx, contact.Id, Relation.None);
                return contact.Id;
            }
            if (req.Status == RequestStatus.Accepted && contact.Relation == Relation.Authorized && !req.DecisionDelivered)
            {
                // Aceptamos pero el solicitante canceló antes de enterarse: se deshace la autorización.
                RequestsRepo.Resolve(c, tx, req.Id, RequestStatus.Cancelled);
                ContactsRepo.SetRelation(c, tx, contact.Id, Relation.None);
                return contact.Id;
            }
            return 0L;
        });
        if (changed != 0)
        {
            RaiseContact(changed);
            PeopleChanged?.Invoke(this, EventArgs.Empty);
        }
        return new Reply(new AckPacket { Status = AckStatuses.Ok });
    }

    // ======================= Utilidades =======================

    private Task<Contact?> GetContactByFingerprintAsync(string fp) => Task.Run(() => Db.Read(c => ContactsRepo.GetByFingerprint(c, fp)));

    private static string NewId() => Guid.NewGuid().ToString("N");

    private string Describe(Exception ex, Contact? contact) => ex switch
    {
        IdentityMismatchException => "El equipo de este contacto" +
            " presenta ahora una identidad distinta. Puede ser una reinstalación de MiniChat u otro equipo. " +
            "No se envió nada. Para volver a chatear hace falta un nuevo emparejamiento: comparen el código por otro medio.",
        IncompatibleVersionException iv => iv.Message + " Actualicen MiniChat en ambos equipos a una versión compatible.",
        _ => ex.Message,
    };
}
