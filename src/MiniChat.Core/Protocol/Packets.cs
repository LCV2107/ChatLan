using System.Text.Json.Serialization;

namespace MiniChat.Core.Protocol;

public static class ProtocolInfo
{
    public const string Name = "mclan";
    /// <summary>Versiones de protocolo que esta aplicación entiende.</summary>
    public const int MinVersion = 1;
    /// <summary>v2 añade el envío de documentos (file_offer, file_get…). Con pares v1 se negocia v1.</summary>
    public const int MaxVersion = 2;
    /// <summary>Primera versión que admite documentos adjuntos.</summary>
    public const int FilesVersion = 2;

    /// <summary>Versión común más alta, o null si los rangos no se solapan.</summary>
    public static int? Negotiate(int peerMin, int peerMax, int localMin = MinVersion, int localMax = MaxVersion)
    {
        int v = Math.Min(peerMax, localMax);
        return v >= Math.Max(peerMin, localMin) ? v : null;
    }
}

public static class ErrorCodes
{
    public const string IncompatibleVersion = "incompatible_version";
    public const string Malformed = "malformed";
    public const string UnsupportedOp = "unsupported_op";
    public const string TooLarge = "too_large";
    public const string RateLimited = "rate_limited";
    public const string HandshakeRequired = "handshake_required";
    public const string Internal = "internal";
}

public static class RequestStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string AlreadyAuthorized = "already_authorized";
    public const string Rejected = "rejected";
    /// <summary>Respuesta genérica cuando el receptor no admite solicitudes de este emisor (no revela bloqueos).</summary>
    public const string Unavailable = "unavailable";
    public const string RateLimited = "rate_limited";
}

public static class DecisionValues
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public static class AckStatuses
{
    public const string Ok = "ok";
    /// <summary>La solicitud ya no existe del otro lado (fue cancelada o expiró).</summary>
    public const string Cancelled = "cancelled";
    public const string UnknownRequest = "unknown_request";
}

public static class MessageAckStatuses
{
    /// <summary>El receptor guardó el mensaje.</summary>
    public const string Stored = "stored";
    /// <summary>El receptor ya lo tenía guardado (reintento): también cuenta como entregado.</summary>
    public const string Duplicate = "duplicate";
    public const string NotAuthorized = "not_authorized";
    public const string Invalid = "invalid";
    public const string TooLarge = "too_large";
    public const string StorageError = "storage_error";
}

public static class MessageKinds
{
    public const string Text = "text";
    public const string Sticker = "sticker";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloPacket), "hello")]
[JsonDerivedType(typeof(HelloOkPacket), "hello_ok")]
[JsonDerivedType(typeof(ChatRequestPacket), "chat_request")]
[JsonDerivedType(typeof(RequestStatusPacket), "request_status")]
[JsonDerivedType(typeof(RequestDecisionPacket), "request_decision")]
[JsonDerivedType(typeof(CancelRequestPacket), "cancel_request")]
[JsonDerivedType(typeof(AckPacket), "ack")]
[JsonDerivedType(typeof(MessagePacket), "msg")]
[JsonDerivedType(typeof(MessageAckPacket), "msg_ack")]
[JsonDerivedType(typeof(PingPacket), "ping")]
[JsonDerivedType(typeof(PongPacket), "pong")]
[JsonDerivedType(typeof(ErrorPacket), "error")]
[JsonDerivedType(typeof(FileOfferPacket), "file_offer")]
[JsonDerivedType(typeof(FileGetPacket), "file_get")]
[JsonDerivedType(typeof(FileDataPacket), "file_data")]
[JsonDerivedType(typeof(FileDonePacket), "file_done")]
[JsonDerivedType(typeof(FileRejectPacket), "file_reject")]
[JsonDerivedType(typeof(FileCancelPacket), "file_cancel")]
public abstract record PacketBase
{
    /// <summary>Identificador único de la trama (para correlacionar respuestas).</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Id de la trama a la que responde.</summary>
    [JsonPropertyName("re")] public string? ReplyTo { get; init; }
}

public sealed record HelloPacket : PacketBase
{
    [JsonPropertyName("proto")] public string Protocol { get; init; } = ProtocolInfo.Name;
    [JsonPropertyName("min")] public int MinVersion { get; init; } = ProtocolInfo.MinVersion;
    [JsonPropertyName("max")] public int MaxVersion { get; init; } = ProtocolInfo.MaxVersion;
    [JsonPropertyName("iid")] public string InstanceId { get; init; } = "";
    [JsonPropertyName("dn")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("pc")] public string MachineName { get; init; } = "";
    [JsonPropertyName("port")] public int ListenPort { get; init; }
    [JsonPropertyName("app")] public string AppVersion { get; init; } = "";
}

public sealed record HelloOkPacket : PacketBase
{
    [JsonPropertyName("ver")] public int Version { get; init; }
    [JsonPropertyName("iid")] public string InstanceId { get; init; } = "";
    [JsonPropertyName("dn")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("pc")] public string MachineName { get; init; } = "";
    [JsonPropertyName("port")] public int ListenPort { get; init; }
    [JsonPropertyName("app")] public string AppVersion { get; init; } = "";
}

public sealed record ChatRequestPacket : PacketBase
{
    [JsonPropertyName("rid")] public string RequestId { get; init; } = "";
}

public sealed record RequestStatusPacket : PacketBase
{
    [JsonPropertyName("rid")] public string RequestId { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}

public sealed record RequestDecisionPacket : PacketBase
{
    [JsonPropertyName("rid")] public string RequestId { get; init; } = "";
    [JsonPropertyName("decision")] public string Decision { get; init; } = "";
}

public sealed record CancelRequestPacket : PacketBase
{
    [JsonPropertyName("rid")] public string RequestId { get; init; } = "";
}

public sealed record AckPacket : PacketBase
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}

public sealed record StickerInfo
{
    /// <summary>SHA-256 hex del PNG normalizado que viaja en el cuerpo de la trama.</summary>
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    [JsonPropertyName("w")] public int Width { get; init; }
    [JsonPropertyName("h")] public int Height { get; init; }
    [JsonPropertyName("len")] public int Length { get; init; }
}

public sealed record MessagePacket : PacketBase
{
    /// <summary>Identificador estable del mensaje: se mantiene en los reintentos para evitar duplicados.</summary>
    [JsonPropertyName("mid")] public string MessageId { get; init; } = "";
    /// <summary>Secuencia del emisor dentro de la conversación (informativa).</summary>
    [JsonPropertyName("seq")] public long Sequence { get; init; }
    /// <summary>Hora del emisor (ms Unix). Solo informativa: el orden no depende de los relojes.</summary>
    [JsonPropertyName("sent")] public long SentAtUnixMs { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = MessageKinds.Text;
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("sticker")] public StickerInfo? Sticker { get; init; }
}

public sealed record MessageAckPacket : PacketBase
{
    [JsonPropertyName("mid")] public string MessageId { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}

public sealed record PingPacket : PacketBase;

public sealed record PongPacket : PacketBase;

public sealed record ErrorPacket : PacketBase
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("msg")] public string Message { get; init; } = "";
    [JsonPropertyName("min")] public int? MinVersion { get; init; }
    [JsonPropertyName("max")] public int? MaxVersion { get; init; }
}

// ======================= Documentos (protocolo v2) =======================
// Modelo «el receptor tira»: el emisor ofrece (file_offer); si el usuario receptor acepta, su equipo pide
// trozos (file_get) y el emisor responde con file_data + bytes. Así la reanudación tras un corte es trivial
// (se pide desde el último byte guardado) y el receptor controla el ritmo.

public static class FileErrorCodes
{
    /// <summary>El archivo no existe o no pertenece a una conversación con quien lo pide.</summary>
    public const string Unavailable = "file_unavailable";
    /// <summary>El emisor tiene MiniChat bloqueado: no puede descifrar su copia. Reintentar más tarde.</summary>
    public const string Locked = "file_locked";
    /// <summary>El emisor canceló el envío.</summary>
    public const string Cancelled = "file_cancelled";
}

public sealed record FileOfferPacket : PacketBase
{
    /// <summary>Identificador estable (también es el uid del mensaje).</summary>
    [JsonPropertyName("fid")] public string FileId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    /// <summary>SHA-256 (hex) del contenido completo, para verificar al terminar.</summary>
    [JsonPropertyName("sha")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("seq")] public long Sequence { get; init; }
    [JsonPropertyName("sent")] public long SentAtUnixMs { get; init; }
}

public sealed record FileGetPacket : PacketBase
{
    [JsonPropertyName("fid")] public string FileId { get; init; } = "";
    [JsonPropertyName("off")] public long Offset { get; init; }
    [JsonPropertyName("len")] public int Length { get; init; }
}

/// <summary>Respuesta a file_get; los bytes van en el cuerpo binario de la trama.</summary>
public sealed record FileDataPacket : PacketBase
{
    [JsonPropertyName("fid")] public string FileId { get; init; } = "";
    [JsonPropertyName("off")] public long Offset { get; init; }
    [JsonPropertyName("last")] public bool Last { get; init; }
}

/// <summary>El receptor guardó y verificó el archivo completo.</summary>
public sealed record FileDonePacket : PacketBase
{
    [JsonPropertyName("fid")] public string FileId { get; init; } = "";
}

/// <summary>El receptor no quiere el archivo.</summary>
public sealed record FileRejectPacket : PacketBase
{
    [JsonPropertyName("fid")] public string FileId { get; init; } = "";
}

/// <summary>El emisor cancela el envío.</summary>
public sealed record FileCancelPacket : PacketBase
{
    [JsonPropertyName("fid")] public string FileId { get; init; } = "";
}

[JsonSourceGenerationOptions(AllowOutOfOrderMetadataProperties = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PacketBase))]
internal sealed partial class ProtocolJson : JsonSerializerContext;
