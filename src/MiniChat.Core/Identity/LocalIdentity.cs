using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MiniChat.Core.Identity;

/// <summary>
/// Identidad persistente de esta instalación: un par de claves ECDSA P-256 guardado en el almacén
/// de claves CNG del usuario de Windows (Microsoft Software Key Storage Provider). La clave privada
/// es no exportable y Windows la protege con DPAPI ligada a la cuenta del usuario.
/// La huella (SHA-256 de la clave pública) es el identificador único: no el nombre, ni la PC, ni la IP.
/// </summary>
public sealed class LocalIdentity : IDisposable
{
    private readonly ECDsaCng _key;

    private LocalIdentity(string keyName, ECDsaCng key, X509Certificate2 certificate, bool created)
    {
        KeyName = keyName;
        _key = key;
        Certificate = certificate;
        Fingerprint = IdentityCodes.FingerprintOf(certificate);
        WasCreated = created;
    }

    public string KeyName { get; }
    public string Fingerprint { get; }
    /// <summary>Certificado autofirmado de uso exclusivo para TLS; su clave privada vive en CNG.</summary>
    public X509Certificate2 Certificate { get; }
    /// <summary>True si la clave se generó en esta ejecución (primera vez o clave perdida).</summary>
    public bool WasCreated { get; }
    public string DisplayCode => IdentityCodes.DisplayCode(Fingerprint);

    public static LocalIdentity OpenOrCreate(string keyName)
    {
        bool created = false;
        CngKey key;
        if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey))
        {
            key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
        }
        else
        {
            key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.None, // almacén del usuario, no de la máquina
                KeyUsage = CngKeyUsages.AllUsages,
            });
            created = true;
        }

        var ecdsa = new ECDsaCng(key);
        // El certificado se regenera en cada arranque; la identidad depende solo de la clave.
        var request = new CertificateRequest("CN=MiniChat LAN", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
        var now = DateTimeOffset.UtcNow;
        var cert = request.CreateSelfSigned(now.AddDays(-2), now.AddYears(10));
        return new LocalIdentity(keyName, ecdsa, cert, created);
    }

    /// <summary>Elimina la clave del almacén del usuario (restablecer identidad / limpieza de pruebas).</summary>
    public static void Delete(string keyName)
    {
        if (!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey)) return;
        using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
        key.Delete();
    }

    public void Dispose()
    {
        Certificate.Dispose();
        _key.Dispose();
    }
}

public static class IdentityCodes
{
    /// <summary>SHA-256 (hex en mayúsculas) de SubjectPublicKeyInfo del certificado.</summary>
    public static string FingerprintOf(X509Certificate2 certificate)
    {
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki));
    }

    public static bool IsValidFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>Código de identidad propio para mostrar en Ajustes (80 bits de la huella).</summary>
    public static string DisplayCode(string fingerprint) =>
        string.Join("-", Enumerable.Range(0, 5).Select(i => fingerprint.Substring(i * 4, 4)));

    /// <summary>
    /// Código de emparejamiento: depende de las DOS identidades (simétrico) y ambos lo ven igual.
    /// 24 dígitos (~79 bits) en 6 grupos para que un intermediario no pueda forzar una coincidencia.
    /// </summary>
    public static string PairingCode(string fingerprintA, string fingerprintB)
    {
        var (a, b) = string.CompareOrdinal(fingerprintA, fingerprintB) <= 0 ? (fingerprintA, fingerprintB) : (fingerprintB, fingerprintA);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"MiniChatLAN pairing v1\n{a}\n{b}"));
        var number = new BigInteger(hash.AsSpan(0, 12), isUnsigned: true, isBigEndian: true) % BigInteger.Pow(10, 24);
        var digits = number.ToString().PadLeft(24, '0');
        return string.Join(" ", Enumerable.Range(0, 6).Select(i => digits.Substring(i * 4, 4)));
    }
}
