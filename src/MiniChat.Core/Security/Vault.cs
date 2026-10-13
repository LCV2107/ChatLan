using System.Security.Cryptography;
using System.Text;
using MiniChat.Core.Persistence;

namespace MiniChat.Core.Security;

/// <summary>
/// Cifrado del historial guardado (datos en reposo).
///
/// Esquema (todo con primitivas estándar de .NET/CNG; no hay criptografía propia):
///  - Par de claves de almacenamiento ECDH P-256. La clave PÚBLICA se guarda en claro: con ella se cifra
///    siempre, incluso con la app bloqueada (así se pueden guardar mensajes recibidos sin conocer el PIN).
///  - Cada valor se cifra con ECIES: clave efímera ECDH + HKDF-SHA256 + AES-256-GCM.
///  - La clave PRIVADA (necesaria para leer) se guarda cifrada con AES-256-GCM bajo una clave derivada de:
///      PBKDF2-SHA256(PIN, sal, ≥600 000 iteraciones)  +  un secreto aleatorio protegido con DPAPI (usuario de Windows).
///    Hacen falta el PIN y la cuenta de Windows del usuario: robar el archivo o conocer solo uno de los dos no basta.
///  - Al bloquear, la clave privada se elimina de la memoria.
/// Limitaciones (documentadas): no protege los metadatos (quién, cuándo, cuántos), ni frente a software espía que
/// lea la pantalla o el teclado mientras la app está desbloqueada; un PIN corto puede adivinarse por fuerza bruta
/// si un atacante obtiene además el secreto DPAPI (es decir, controla la cuenta de Windows del usuario).
/// </summary>
public sealed class Vault : IDisposable
{
    public const string SealedPrefix = "e1:";
    public const int MinPinLength = 6;
    public const int MaxPinLength = 64;
    private const byte FormatVersion = 1;
    private static readonly byte[] DpapiEntropy = "MiniChatLAN vault v1"u8.ToArray();
    private static readonly byte[] SealInfo = "MiniChatLAN seal v1"u8.ToArray();
    private static readonly byte[] KekInfo = "MiniChatLAN kek v1"u8.ToArray();

    private readonly Database _db;
    private readonly object _gate = new();
    private VaultRecord? _record;
    private ECDiffieHellman? _publicKey;
    private ECDiffieHellman? _privateKey;

    public Vault(Database db)
    {
        _db = db;
        _record = db.Read(c => VaultRepo.Get(c));
        if (_record is not null) _publicKey = ImportPublic(_record.PublicKey);
    }

    /// <summary>Hay un PIN configurado.</summary>
    public bool Exists { get { lock (_gate) return _record is not null; } }

    /// <summary>La clave privada está en memoria: se puede leer el historial.</summary>
    public bool IsUnlocked { get { lock (_gate) return _privateKey is not null; } }

    public event EventHandler? StateChanged;

    public static string? ValidatePin(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < MinPinLength)
            return $"El PIN debe tener al menos {MinPinLength} caracteres.";
        if (pin.Length > MaxPinLength) return $"El PIN no puede superar {MaxPinLength} caracteres.";
        if (pin.Distinct().Count() == 1) return "El PIN no puede repetir un único carácter.";
        if ("0123456789012345678901234567890".Contains(pin) || "9876543210987654321098765432109".Contains(pin))
            return "El PIN no puede ser una secuencia (123456…).";
        return null;
    }

    /// <summary>Crea las claves y las protege con el PIN. Queda desbloqueada.</summary>
    public void Create(string pin, int iterations)
    {
        if (ValidatePin(pin) is { } error) throw new ArgumentException(error);
        var priv = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = priv.ExportSubjectPublicKeyInfo();
        var salt = RandomNumberGenerator.GetBytes(16);
        var secret = RandomNumberGenerator.GetBytes(32);
        var protectedSecret = ProtectedData.Protect(secret, DpapiEntropy, DataProtectionScope.CurrentUser);
        var kek = DeriveKek(pin, salt, iterations, secret);
        var wrapped = Wrap(kek, priv.ExportPkcs8PrivateKey(), publicKey);
        CryptographicOperations.ZeroMemory(kek);
        CryptographicOperations.ZeroMemory(secret);
        var record = new VaultRecord(FormatVersion, salt, iterations, protectedSecret, publicKey, wrapped);
        _db.Write((c, tx) => VaultRepo.Save(c, tx, record));
        lock (_gate)
        {
            _record = record;
            _publicKey?.Dispose();
            _publicKey = ImportPublic(publicKey);
            _privateKey?.Dispose();
            _privateKey = priv;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Comprueba el PIN y carga la clave privada. Lento a propósito (PBKDF2).</summary>
    public bool TryUnlock(string pin)
    {
        VaultRecord record;
        lock (_gate) record = _record ?? throw new InvalidOperationException("No hay PIN configurado.");
        var priv = TryUnwrap(record, pin);
        if (priv is null) return false;
        lock (_gate)
        {
            _privateKey?.Dispose();
            _privateKey = priv;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Lock()
    {
        bool changed;
        lock (_gate)
        {
            changed = _privateKey is not null;
            _privateKey?.Dispose();
            _privateKey = null;
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cambia el PIN: vuelve a proteger la misma clave privada (el historial no se recifra).</summary>
    public bool ChangePin(string currentPin, string newPin, int iterations)
    {
        if (ValidatePin(newPin) is { } error) throw new ArgumentException(error);
        VaultRecord record;
        lock (_gate) record = _record ?? throw new InvalidOperationException("No hay PIN configurado.");
        using var priv = TryUnwrap(record, currentPin);
        if (priv is null) return false;
        var salt = RandomNumberGenerator.GetBytes(16);
        var secret = RandomNumberGenerator.GetBytes(32);
        var protectedSecret = ProtectedData.Protect(secret, DpapiEntropy, DataProtectionScope.CurrentUser);
        var kek = DeriveKek(newPin, salt, iterations, secret);
        var wrapped = Wrap(kek, priv.ExportPkcs8PrivateKey(), record.PublicKey);
        CryptographicOperations.ZeroMemory(kek);
        CryptographicOperations.ZeroMemory(secret);
        var updated = record with { Salt = salt, Iterations = iterations, ProtectedSecret = protectedSecret, WrappedPrivateKey = wrapped };
        _db.Write((c, tx) => VaultRepo.Save(c, tx, updated));
        lock (_gate) _record = updated;
        return true;
    }

    /// <summary>Elimina las claves (PIN olvidado). Lo cifrado con ellas queda ilegible para siempre.</summary>
    public void Destroy()
    {
        _db.Write((c, tx) => VaultRepo.Delete(c, tx));
        lock (_gate)
        {
            _record = null;
            _publicKey?.Dispose();
            _publicKey = null;
            _privateKey?.Dispose();
            _privateKey = null;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ======================= Cifrado de valores =======================

    public static bool IsSealed(string? value) => value is not null && value.StartsWith(SealedPrefix, StringComparison.Ordinal);

    public string? Seal(string? plain)
    {
        if (plain is null) return null;
        return SealedPrefix + Convert.ToBase64String(SealBytes(Encoding.UTF8.GetBytes(plain)));
    }

    /// <summary>Descifra un valor. Los valores antiguos sin cifrar se devuelven tal cual.</summary>
    /// <exception cref="VaultLockedException">La app está bloqueada.</exception>
    public string? Open(string? value)
    {
        if (value is null || !IsSealed(value)) return value;
        var bytes = OpenBytes(Convert.FromBase64String(value[SealedPrefix.Length..]));
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Cifra con la clave pública: funciona aunque la app esté bloqueada.</summary>
    public byte[] SealBytes(ReadOnlySpan<byte> plain)
    {
        ECDiffieHellman recipient;
        lock (_gate) recipient = _publicKey ?? throw new InvalidOperationException("No hay PIN configurado.");
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = EncodePoint(ephemeral.ExportParameters(false));
        byte[] shared;
        lock (_gate) shared = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, ephPublic, SealInfo);
        CryptographicOperations.ZeroMemory(shared);

        var output = new byte[1 + 65 + 12 + 16 + plain.Length];
        output[0] = FormatVersion;
        ephPublic.CopyTo(output, 1);
        var nonce = output.AsSpan(66, 12);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, output.AsSpan(94), output.AsSpan(78, 16), ephPublic);
        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    public byte[] OpenBytes(ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < 94 || sealedData[0] != FormatVersion) throw new CryptographicException("Formato cifrado desconocido.");
        var ephPublic = sealedData.Slice(1, 65).ToArray();
        byte[] shared;
        lock (_gate)
        {
            if (_privateKey is null) throw new VaultLockedException();
            using var eph = ImportPoint(ephPublic);
            shared = _privateKey.DeriveRawSecretAgreement(eph.PublicKey);
        }
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, ephPublic, SealInfo);
        CryptographicOperations.ZeroMemory(shared);
        var plain = new byte[sealedData.Length - 94];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(sealedData.Slice(66, 12), sealedData[94..], sealedData.Slice(78, 16), plain, ephPublic);
        CryptographicOperations.ZeroMemory(key);
        return plain;
    }

    // ======================= Auxiliares =======================

    private static byte[] DeriveKek(string pin, byte[] salt, int iterations, byte[] secret)
    {
        var pinBytes = Encoding.UTF8.GetBytes(pin.Normalize(NormalizationForm.FormC));
        var pinKey = Rfc2898DeriveBytes.Pbkdf2(pinBytes, salt, iterations, HashAlgorithmName.SHA256, 32);
        CryptographicOperations.ZeroMemory(pinBytes);
        var ikm = new byte[64];
        pinKey.CopyTo(ikm, 0);
        secret.CopyTo(ikm, 32);
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, KekInfo);
        CryptographicOperations.ZeroMemory(pinKey);
        CryptographicOperations.ZeroMemory(ikm);
        return kek;
    }

    private static byte[] Wrap(byte[] kek, byte[] pkcs8, byte[] aad)
    {
        var output = new byte[12 + 16 + pkcs8.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using (var aes = new AesGcm(kek, 16))
            aes.Encrypt(output.AsSpan(0, 12), pkcs8, output.AsSpan(28), output.AsSpan(12, 16), aad);
        CryptographicOperations.ZeroMemory(pkcs8);
        return output;
    }

    private static ECDiffieHellman? TryUnwrap(VaultRecord record, string pin)
    {
        byte[] secret;
        try
        {
            secret = ProtectedData.Unprotect(record.ProtectedSecret, DpapiEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new VaultUnavailableException("Windows no pudo abrir la protección de la clave (¿otro usuario de Windows o perfil restaurado en otro equipo?).", ex);
        }
        var kek = DeriveKek(pin ?? "", record.Salt, record.Iterations, secret);
        CryptographicOperations.ZeroMemory(secret);
        var pkcs8 = new byte[record.WrappedPrivateKey.Length - 28];
        try
        {
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(record.WrappedPrivateKey.AsSpan(0, 12), record.WrappedPrivateKey.AsSpan(28), record.WrappedPrivateKey.AsSpan(12, 16), pkcs8, record.PublicKey);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null; // PIN incorrecto
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        CryptographicOperations.ZeroMemory(pkcs8);
        return key;
    }

    private static ECDiffieHellman ImportPublic(byte[] spki)
    {
        var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key;
    }

    private static byte[] EncodePoint(ECParameters p)
    {
        var b = new byte[65];
        b[0] = 0x04;
        p.Q.X!.CopyTo(b, 1);
        p.Q.Y!.CopyTo(b, 33);
        return b;
    }

    private static ECDiffieHellman ImportPoint(byte[] point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new CryptographicException("Punto no válido.");
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _publicKey?.Dispose();
            _privateKey?.Dispose();
            _publicKey = _privateKey = null;
        }
    }
}

public sealed record VaultRecord(int Version, byte[] Salt, int Iterations, byte[] ProtectedSecret, byte[] PublicKey, byte[] WrappedPrivateKey);

/// <summary>Se intentó leer contenido cifrado con la app bloqueada.</summary>
public sealed class VaultLockedException : Exception
{
    public VaultLockedException() : base("MiniChat está bloqueado. Introduzca el PIN.") { }
}

/// <summary>La protección de Windows (DPAPI) no se pudo abrir.</summary>
public sealed class VaultUnavailableException : Exception
{
    public VaultUnavailableException(string message, Exception inner) : base(message, inner) { }
}
