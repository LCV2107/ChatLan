using System.Net;
using MiniChat.Core.Model;

namespace MiniChat.Core.Engine;

/// <summary>Resultado de una acción del usuario, con un mensaje comprensible si falló.</summary>
public record OperationResult(bool Success, string? Message = null)
{
    public static OperationResult Ok(string? message = null) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed record OperationResult<T>(bool Success, T? Value, string? Message = null) : OperationResult(Success, Message);

/// <summary>
/// Vista combinada de una persona: datos guardados (si es contacto) + presencia en la red (si se anuncia).
/// Disponibilidad y relación son independientes.
/// </summary>
public sealed record PersonView(
    string Fingerprint,
    long? ContactId,
    string DisplayName,
    string MachineName,
    bool Online,
    IPEndPoint? Endpoint,
    Relation Relation,
    bool Blocked,
    int UnreadCount,
    bool CompatibleVersion,
    long? PossibleIdentityChangeOfContactId,
    string? PossibleIdentityChangeOfName,
    bool HasHistory)
{
    public bool IsContact => ContactId is not null;
}

public sealed record MessageEvent(long ContactId, ChatMessage Message, bool IsNew);

public enum NoticeKind { IncomingRequest, RequestAccepted, RequestRejected, NewMessage, IdentityMismatch, FileOffered }

/// <summary>Cambió el estado o el progreso de un documento.</summary>
public sealed record FileEvent(long ContactId, long MessageId, MiniChat.Core.Model.FileTransfer File);

/// <summary>Documento descifrado listo para abrir.</summary>
public sealed record OpenedFile(string Path, bool IsDangerous);

/// <summary>Aviso para notificaciones discretas (nunca roba el foco ni abre conversaciones).</summary>
public sealed record EngineNotice(NoticeKind Kind, long ContactId, string DisplayName, string? Preview);

public sealed record ExportResult(string TextFile, string? StickerFolder, int MessageCount, int StickerCount);
