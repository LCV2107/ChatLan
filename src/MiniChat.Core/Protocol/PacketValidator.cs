using MiniChat.Core.Config;
using MiniChat.Core.Identity;

namespace MiniChat.Core.Protocol;

/// <summary>Validación estricta de campos tras deserializar. Cualquier fallo es <see cref="FrameFormatException"/>.</summary>
public static class PacketValidator
{
    public static void Validate(PacketBase packet, MiniChatOptions options)
    {
        Require(IsToken(packet.Id, 64), "id no válido");
        if (packet.ReplyTo is not null) Require(IsToken(packet.ReplyTo, 64), "re no válido");

        switch (packet)
        {
            case HelloPacket h:
                Require(h.Protocol == ProtocolInfo.Name, "protocolo desconocido");
                Require(h.MinVersion >= 1 && h.MaxVersion >= h.MinVersion && h.MaxVersion < 1000, "versiones no válidas");
                ValidateHelloCommon(h.InstanceId, h.DisplayName, h.MachineName, h.ListenPort, h.AppVersion, options);
                break;
            case HelloOkPacket ok:
                Require(ok.Version >= 1, "versión no válida");
                ValidateHelloCommon(ok.InstanceId, ok.DisplayName, ok.MachineName, ok.ListenPort, ok.AppVersion, options);
                break;
            case ChatRequestPacket r:
                Require(IsGuid(r.RequestId), "rid no válido");
                break;
            case RequestStatusPacket s:
                Require(IsGuid(s.RequestId), "rid no válido");
                Require(s.Status is RequestStatuses.Pending or RequestStatuses.Accepted or RequestStatuses.AlreadyAuthorized
                    or RequestStatuses.Rejected or RequestStatuses.Unavailable or RequestStatuses.RateLimited, "estado desconocido");
                break;
            case RequestDecisionPacket d:
                Require(IsGuid(d.RequestId), "rid no válido");
                Require(d.Decision is DecisionValues.Accepted or DecisionValues.Rejected, "decisión desconocida");
                break;
            case CancelRequestPacket c:
                Require(IsGuid(c.RequestId), "rid no válido");
                break;
            case AckPacket a:
                Require(IsToken(a.Status, 32), "estado no válido");
                break;
            case MessagePacket m:
                Require(IsGuid(m.MessageId), "mid no válido");
                Require(m.Sequence >= 0, "seq no válida");
                if (m.Kind == MessageKinds.Text)
                {
                    Require(m.Sticker is null, "texto con sticker");
                    Require(m.Text is not null && m.Text.Length > 0 && m.Text.Length <= options.MaxTextChars, "longitud de texto no válida");
                    Require(!m.Text!.Contains('\0'), "carácter nulo");
                }
                else if (m.Kind == MessageKinds.Sticker)
                {
                    Require(m.Text is null && m.Sticker is not null, "sticker sin datos");
                    var s = m.Sticker!;
                    Require(IdentityCodes.IsValidFingerprint(s.Hash), "hash no válido");
                    Require(s.Width > 0 && s.Height > 0 && s.Width <= options.StickerMaxDimension && s.Height <= options.StickerMaxDimension, "dimensiones no válidas");
                    Require(s.Length > 0 && s.Length <= options.MaxStickerWireBytes, "tamaño de sticker no válido");
                }
                else
                {
                    throw new FrameFormatException("Tipo de mensaje desconocido.");
                }
                break;
            case MessageAckPacket ma:
                Require(IsGuid(ma.MessageId), "mid no válido");
                Require(IsToken(ma.Status, 32), "estado no válido");
                break;
            case FileOfferPacket fo:
                Require(IsGuid(fo.FileId), "fid no válido");
                Require(Files.FileNames.IsAcceptableWireName(fo.Name), "nombre de archivo no válido");
                Require(fo.Size > 0 && fo.Size <= options.MaxFileBytes, "tamaño de archivo no válido");
                Require(IdentityCodes.IsValidFingerprint(fo.Sha256), "hash no válido");
                Require(fo.Sequence >= 0, "seq no válida");
                break;
            case FileGetPacket fg:
                Require(IsGuid(fg.FileId), "fid no válido");
                Require(fg.Offset >= 0 && fg.Offset < options.MaxFileBytes, "offset no válido");
                Require(fg.Length > 0 && fg.Length <= options.FileChunkBytes, "longitud no válida");
                break;
            case FileDataPacket fd:
                Require(IsGuid(fd.FileId), "fid no válido");
                Require(fd.Offset >= 0, "offset no válido");
                break;
            case FileDonePacket fdn:
                Require(IsGuid(fdn.FileId), "fid no válido");
                break;
            case FileRejectPacket fr:
                Require(IsGuid(fr.FileId), "fid no válido");
                break;
            case FileCancelPacket fc:
                Require(IsGuid(fc.FileId), "fid no válido");
                break;
            case ErrorPacket e:
                Require(IsToken(e.Code, 64) && (e.Message?.Length ?? 0) <= 512, "error no válido");
                break;
        }
    }

    private static void ValidateHelloCommon(string iid, string dn, string pc, int port, string app, MiniChatOptions o)
    {
        Require(IsToken(iid, 64), "iid no válido");
        Require(IsName(dn, o.MaxNameChars) && IsName(pc, o.MaxNameChars), "nombre no válido");
        Require(port is > 0 and < 65536, "puerto no válido");
        Require(app.Length <= 32, "versión de app no válida");
    }

    public static bool IsName(string? s, int max) =>
        s is not null && s.Trim().Length > 0 && s.Length <= max && !s.Any(char.IsControl);

    public static bool IsGuid(string? s) => s is { Length: 32 } && Guid.TryParseExact(s, "N", out _);

    private static bool IsToken(string? s, int max) =>
        s is not null && s.Length > 0 && s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new FrameFormatException("Paquete no válido: " + what + ".");
    }
}
