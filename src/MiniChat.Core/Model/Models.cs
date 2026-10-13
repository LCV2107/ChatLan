namespace MiniChat.Core.Model;

/// <summary>
/// Estado de la relación con un contacto (separado de la disponibilidad en red y del bloqueo).
/// Los valores se guardan en la base de datos: no cambiar los números.
/// </summary>
public enum Relation
{
    /// <summary>Conocido, sin autorización (p. ej. tras cancelar, desbloquear o una revocación).</summary>
    None = 0,
    /// <summary>Le envié una solicitud y el otro la recibió; espera respuesta.</summary>
    OutgoingPending = 1,
    /// <summary>Me envió una solicitud; debo aceptar o rechazar.</summary>
    IncomingPending = 2,
    /// <summary>Ambos consintieron: se pueden intercambiar mensajes.</summary>
    Authorized = 3,
    /// <summary>Rechacé su solicitud.</summary>
    RejectedByMe = 4,
    /// <summary>Rechazó mi solicitud.</summary>
    RejectedByPeer = 5,
    /// <summary>Al enviarle un mensaje respondió que ya no me autoriza.</summary>
    RevokedByPeer = 6,
}

public enum RequestDirection { Outgoing = 0, Incoming = 1 }

public enum RequestStatus { Pending = 0, Accepted = 1, Rejected = 2, Cancelled = 3, Expired = 4 }

public enum MessageDirection { Outgoing = 0, Incoming = 1 }

public enum MessageKind { Text = 0, Sticker = 1, File = 2 }

/// <summary>Estados visibles de un mensaje. Los valores se guardan: no cambiar los números.</summary>
public enum MessageStatus
{
    /// <summary>Guardado localmente, en cola para enviarse.</summary>
    Pending = 0,
    /// <summary>Transmitiéndose.</summary>
    Sending = 1,
    /// <summary>La aplicación receptora lo recibió y lo guardó (no implica que se haya leído).</summary>
    Delivered = 2,
    /// <summary>Se transmitió pero no llegó confirmación (puede haberse recibido o no).</summary>
    Unconfirmed = 3,
    /// <summary>No se pudo transmitir.</summary>
    Failed = 4,
    /// <summary>Mensaje entrante guardado.</summary>
    Received = 5,
}

public sealed record Contact(
    long Id,
    string Fingerprint,
    string DisplayName,
    string MachineName,
    string? LastAddress,
    int? LastPort,
    Relation Relation,
    bool Blocked,
    DateTimeOffset RelationChangedAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? LastSeenAt);

public sealed record ChatRequest(
    string Id,
    long ContactId,
    RequestDirection Direction,
    RequestStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    bool DecisionDelivered,
    DateTimeOffset? LastDeliveryAttemptAt);

public sealed record ChatMessage(
    long Id,
    long ConversationId,
    string Uid,
    MessageDirection Direction,
    MessageKind Kind,
    string? Text,
    string? StickerHash,
    MessageStatus Status,
    long Sequence,
    DateTimeOffset SenderTime,
    DateTimeOffset LocalTime,
    string? Error)
{
    /// <summary>Datos del documento cuando <see cref="Kind"/> es File.</summary>
    public FileTransfer? File { get; init; }
}

/// <summary>Estado de un documento. Los valores se guardan: no cambiar los números.</summary>
public enum FileState
{
    /// <summary>Ofrecido; el receptor aún no decidió.</summary>
    Offered = 0,
    /// <summary>Aceptado y transfiriéndose (o pausado hasta poder continuar).</summary>
    Transferring = 1,
    /// <summary>Recibido completo y verificado (en el emisor: el receptor confirmó).</summary>
    Completed = 2,
    Rejected = 3,
    Cancelled = 4,
    Failed = 5,
}

public sealed record FileTransfer(
    string FileId,
    long ConversationId,
    MessageDirection Direction,
    string Name,
    long Size,
    string Sha256,
    FileState State,
    long Transferred,
    string? Error);

public sealed record Conversation(long Id, long ContactId, int UnreadCount, string? Draft);

public sealed record StickerBlob(string Hash, int Width, int Height, long Size);

public sealed record LibrarySticker(long Id, string Hash, string Name, bool Favorite, DateTimeOffset AddedAt, DateTimeOffset? LastUsedAt, int Width, int Height);

public static class Time
{
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static DateTimeOffset From(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public static DateTimeOffset? From(long? ms) => ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
}
