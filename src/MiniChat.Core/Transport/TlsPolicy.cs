using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MiniChat.Core.Identity;

namespace MiniChat.Core.Transport;

/// <summary>
/// Política TLS de MiniChat.
///
/// Los certificados son autofirmados (no hay CA en una LAN sin servidor). En lugar de confiar en una
/// cadena PKI, la confianza se basa en la clave pública (fijación de clave, como SSH o Syncthing):
///  - TLS mutuo: ambos extremos presentan certificado y demuestran poseer la clave privada.
///  - La huella SHA-256 de la clave pública es la identidad del par.
///  - Al conectar con un contacto conocido se exige su huella exacta (<see cref="IdentityMismatchException"/>).
///  - Ninguna operación privilegiada (mensajes) se acepta de una huella no autorizada en la base de datos.
/// Solo se aceptan los errores de cadena inherentes a un certificado autofirmado; se rechaza la ausencia de
/// certificado y cualquier clave que no sea ECDSA P-256. No hay descargas de certificados ni consultas de
/// revocación (funcionamiento sin Internet).
/// </summary>
public static class TlsPolicy
{
    public const string TargetHost = "minichat.lan";
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    public static X509ChainPolicy OfflineChainPolicy() => new()
    {
        RevocationMode = X509RevocationMode.NoCheck,
        DisableCertificateDownloads = true,
    };

    /// <summary>Valida el certificado del par y devuelve su huella, o null si debe rechazarse.</summary>
    public static string? AcceptPeerCertificate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return null;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return null;
        try
        {
            var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var ec = cert.GetECDsaPublicKey();
            if (ec is null || ec.KeySize != 256) return null;
            return IdentityCodes.FingerprintOf(cert);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class IdentityMismatchException : Exception
{
    public IdentityMismatchException(string expected, string actual)
        : base("El equipo presenta una identidad distinta de la esperada.")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }
    public string Actual { get; }
}

public sealed class IncompatibleVersionException : Exception
{
    public IncompatibleVersionException(int? peerMin, int? peerMax)
        : base($"Versión de protocolo incompatible (el otro equipo admite {peerMin}–{peerMax}; esta versión admite {Protocol.ProtocolInfo.MinVersion}–{Protocol.ProtocolInfo.MaxVersion}).")
    {
        PeerMin = peerMin;
        PeerMax = peerMax;
    }

    public int? PeerMin { get; }
    public int? PeerMax { get; }
}
