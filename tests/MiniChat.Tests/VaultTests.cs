using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MiniChat.Core.Persistence;
using MiniChat.Core.Security;

namespace MiniChat.Tests;

public class VaultTests : IDisposable
{
    private const int FastIterations = 1000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "vault-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Database _db;

    public VaultTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Database.Open(Path.Combine(_dir, "minichat.db"), Path.Combine(_dir, "backups"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Create_Unlock_WrongPin_Lock()
    {
        using var v = new Vault(_db);
        Assert.False(v.Exists);
        v.Create("482915", FastIterations);
        Assert.True(v.Exists);
        Assert.True(v.IsUnlocked);
        var sealedText = v.Seal("Hola ñandú 😀");
        Assert.StartsWith(Vault.SealedPrefix, sealedText);
        Assert.DoesNotContain("Hola", sealedText);
        Assert.Equal("Hola ñandú 😀", v.Open(sealedText));

        v.Lock();
        Assert.False(v.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => v.Open(sealedText));

        using var reopened = new Vault(_db); // como al reiniciar la app
        Assert.True(reopened.Exists);
        Assert.False(reopened.IsUnlocked);
        Assert.False(reopened.TryUnlock("000000"));
        Assert.False(reopened.TryUnlock("482916"));
        Assert.True(reopened.TryUnlock("482915"));
        Assert.Equal("Hola ñandú 😀", reopened.Open(sealedText));
    }

    [Fact]
    public void Sealing_Works_WhileLocked_ReadingDoesNot()
    {
        using var v = new Vault(_db);
        v.Create("Clave-Larga-2026", FastIterations);
        v.Lock();
        var sealedWhileLocked = v.Seal("mensaje recibido con la app bloqueada");
        var bytes = v.SealBytes(Encoding.UTF8.GetBytes("sticker"));
        Assert.Throws<VaultLockedException>(() => v.Open(sealedWhileLocked));
        Assert.True(v.TryUnlock("Clave-Larga-2026"));
        Assert.Equal("mensaje recibido con la app bloqueada", v.Open(sealedWhileLocked));
        Assert.Equal("sticker", Encoding.UTF8.GetString(v.OpenBytes(bytes)));
    }

    [Fact]
    public void ChangePin_KeepsData_OldPinStopsWorking()
    {
        using var v = new Vault(_db);
        v.Create("482915", FastIterations);
        var s = v.Seal("antes del cambio");
        Assert.False(v.ChangePin("999999", "735184", FastIterations));
        Assert.True(v.ChangePin("482915", "735184", FastIterations));
        v.Lock();
        Assert.False(v.TryUnlock("482915"));
        Assert.True(v.TryUnlock("735184"));
        Assert.Equal("antes del cambio", v.Open(s));
    }

    [Fact]
    public void TamperedCiphertext_IsDetected_LegacyPlainTextPassesThrough()
    {
        using var v = new Vault(_db);
        v.Create("482915", FastIterations);
        var raw = v.SealBytes(Encoding.UTF8.GetBytes("integridad"));
        raw[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => v.OpenBytes(raw));
        Assert.Equal("texto antiguo sin cifrar", v.Open("texto antiguo sin cifrar"));
        Assert.Null(v.Open(null));
    }

    [Fact]
    public void WeakPins_AreRejected()
    {
        Assert.NotNull(Vault.ValidatePin("12345"));
        Assert.NotNull(Vault.ValidatePin("111111"));
        Assert.NotNull(Vault.ValidatePin("123456"));
        Assert.NotNull(Vault.ValidatePin("654321"));
        Assert.Null(Vault.ValidatePin("482915"));
        Assert.Null(Vault.ValidatePin("mi frase secreta"));
    }

    [Fact]
    public void StoredRecord_ContainsNoUsablePlainKey_AndDestroyMakesDataUnreadable()
    {
        using var v = new Vault(_db);
        v.Create("482915", FastIterations);
        var s = v.Seal("se perderá");
        var record = _db.Read(VaultRepo.Get)!;
        Assert.Equal(FastIterations, record.Iterations);
        Assert.Throws<CryptographicException>(() => { using var k = ECDiffieHellman.Create(); k.ImportPkcs8PrivateKey(record.WrappedPrivateKey, out _); });
        v.Destroy();
        v.Create("735184", FastIterations);
        Assert.ThrowsAny<CryptographicException>(() => v.Open(s));
    }
}
