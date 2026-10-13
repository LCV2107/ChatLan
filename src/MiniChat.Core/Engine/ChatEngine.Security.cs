using System.Security.Cryptography;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;
using MiniChat.Core.Security;

namespace MiniChat.Core.Engine;

// Cifrado del historial en reposo y bloqueo con PIN.
//
//  - Se cifran: textos de mensajes, borradores, nombres de contactos y de sus PCs, nombres de stickers de la
//    biblioteca y las imágenes de los stickers en disco.
//  - Quedan en claro (necesarios para funcionar bloqueado o difundidos de todos modos por la red): huellas,
//    IP y puerto de los contactos, estados, fechas, contadores, el nombre visible propio y los hashes de stickers.
//  - Bloqueado, la app sigue recibiendo y guardando mensajes (se cifran con la clave pública), pero no puede
//    leerlos ni enviarlos: los envíos en cola esperan al desbloqueo.
public sealed partial class ChatEngine
{
    private int _failedPinAttempts;
    private DateTimeOffset _pinRetryNotBefore = DateTimeOffset.MinValue;

    /// <summary>Aún no hay PIN: hay que crearlo (primera ejecución o perfil anterior a la versión con cifrado).</summary>
    public bool NeedsPinSetup => !Vault.Exists;

    public bool IsLocked => !Vault.IsUnlocked;

    /// <summary>Cambió el estado bloqueado/desbloqueado.</summary>
    public event EventHandler? LockStateChanged;

    /// <summary>Crea el PIN y cifra lo que hubiera guardado sin cifrar (perfiles de versiones anteriores).</summary>
    public async Task<OperationResult> SetupPinAsync(string pin)
    {
        if (Vault.Exists) return OperationResult.Fail("Ya hay un PIN configurado.");
        if (Vault.ValidatePin(pin) is { } error) return OperationResult.Fail(error);
        await Task.Run(() => Vault.Create(pin, Options.PinIterations)).ConfigureAwait(false);
        int migrated = await Task.Run(EncryptLegacyData).ConfigureAwait(false);
        _log.Info("security", $"PIN creado; {migrated} valores existentes cifrados");
        return OperationResult.Ok(migrated > 0 ? "El historial existente se ha cifrado." : null);
    }

    public async Task<OperationResult> UnlockAsync(string pin)
    {
        var wait = _pinRetryNotBefore - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
            return OperationResult.Fail($"Demasiados intentos. Espere {Math.Ceiling(wait.TotalSeconds)} s.");
        bool ok;
        try
        {
            ok = await Task.Run(() => Vault.TryUnlock(pin ?? "")).ConfigureAwait(false);
        }
        catch (VaultUnavailableException ex)
        {
            _log.Error("security", "DPAPI no disponible", ex);
            return OperationResult.Fail(ex.Message);
        }
        if (ok)
        {
            _failedPinAttempts = 0;
            return OperationResult.Ok();
        }
        _failedPinAttempts++;
        int over = _failedPinAttempts - Options.PinFreeAttempts;
        if (over >= 0) _pinRetryNotBefore = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, over)));
        _log.Warn("security", $"PIN incorrecto (intento {_failedPinAttempts})");
        return OperationResult.Fail(over >= 0
            ? $"PIN incorrecto. Espere {Math.Min(300, 5 * Math.Pow(2, over)):0} s antes de reintentar."
            : "PIN incorrecto.");
    }

    public void Lock() => Vault.Lock();

    public async Task<OperationResult> ChangePinAsync(string currentPin, string newPin)
    {
        if (Vault.ValidatePin(newPin) is { } error) return OperationResult.Fail(error);
        bool ok = await Task.Run(() => Vault.ChangePin(currentPin ?? "", newPin, Options.PinIterations)).ConfigureAwait(false);
        return ok ? OperationResult.Ok("PIN cambiado.") : OperationResult.Fail("El PIN actual no es correcto.");
    }

    /// <summary>
    /// PIN olvidado: el historial cifrado es irrecuperable. Se borran mensajes, borradores, biblioteca e imágenes
    /// de stickers y se crea un PIN nuevo. Contactos, autorizaciones y bloqueos se conservan (sus nombres se
    /// recuperan al volver a verlos en la red).
    /// </summary>
    public async Task<OperationResult> ResetForgottenPinAsync(string newPin)
    {
        if (Vault.ValidatePin(newPin) is { } error) return OperationResult.Fail(error);
        await Task.Run(() =>
        {
            Vault.Destroy();
            Db.Write((c, tx) =>
            {
                c.Exec("DELETE FROM messages;", tx);
                c.Exec("UPDATE conversations SET draft = NULL, unread_count = 0;", tx);
                c.Exec("DELETE FROM sticker_library;", tx);
                c.Exec("DELETE FROM sticker_blobs;", tx);
                c.Exec("DELETE FROM files;", tx);
            });
            Vault.Create(newPin, Options.PinIterations);
            var unknown = Vault.Seal("(nombre pendiente)");
            Db.Write((c, tx) => c.Exec("UPDATE contacts SET display_name = $n, machine_name = $n;", tx, ("$n", unknown)));
            Stickers.DeleteAllFiles();
            _fileKeys.Clear();
            if (Directory.Exists(Paths.FilesDirectory)) Directory.Delete(Paths.FilesDirectory, recursive: true);
            CleanupOpenedFiles();
            Compact();
        }).ConfigureAwait(false);
        _log.Warn("security", "PIN restablecido: historial cifrado eliminado");
        PeopleChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Se creó un PIN nuevo. El historial anterior se ha eliminado de este equipo.");
    }

    private void OnVaultStateChanged(object? sender, EventArgs e)
    {
        if (!Vault.IsUnlocked) ForgetFileSecrets();
        LockStateChanged?.Invoke(this, EventArgs.Empty);
        if (Vault.IsUnlocked)
        {
            _ = Task.Run(ResumeQueuedSends);
            _ = Task.Run(() => ResumeFileWork(null));
        }
    }

    /// <summary>Al desbloquear, continúan los envíos que quedaron en cola mientras estaba bloqueado.</summary>
    private void ResumeQueuedSends()
    {
        try
        {
            var contacts = Db.Read(c => c.Query(
                "SELECT DISTINCT v.contact_id FROM messages m JOIN conversations v ON v.id = m.conversation_id WHERE m.direction = 0 AND m.status = 0;",
                r => r.GetInt64(0)));
            foreach (var id in contacts) Kick(id);
        }
        catch (Exception ex)
        {
            _log.Warn("security", "No se pudieron reanudar los envíos", ex);
        }
    }

    /// <summary>Cifra los valores guardados en claro por versiones anteriores y purga sus restos del archivo.</summary>
    private int EncryptLegacyData()
    {
        int count = Db.Write((c, tx) =>
        {
            int n = 0;
            foreach (var (id, body) in c.Query("SELECT id, body FROM messages WHERE body IS NOT NULL AND body NOT LIKE 'e1:%';", r => (r.GetInt64(0), r.GetString(1)), tx))
                n += c.Exec("UPDATE messages SET body = $b WHERE id = $id;", tx, ("$b", Vault.Seal(body)), ("$id", id));
            foreach (var (id, draft) in c.Query("SELECT id, draft FROM conversations WHERE draft IS NOT NULL AND draft NOT LIKE 'e1:%';", r => (r.GetInt64(0), r.GetString(1)), tx))
                n += c.Exec("UPDATE conversations SET draft = $d WHERE id = $id;", tx, ("$d", Vault.Seal(draft)), ("$id", id));
            foreach (var (id, dn, pc) in c.Query("SELECT id, display_name, machine_name FROM contacts WHERE display_name NOT LIKE 'e1:%' OR machine_name NOT LIKE 'e1:%';",
                         r => (r.GetInt64(0), r.GetString(1), r.GetString(2)), tx))
                n += c.Exec("UPDATE contacts SET display_name = $d, machine_name = $p WHERE id = $id;", tx,
                    ("$d", Vault.IsSealed(dn) ? dn : Vault.Seal(dn)), ("$p", Vault.IsSealed(pc) ? pc : Vault.Seal(pc)), ("$id", id));
            foreach (var (id, name) in c.Query("SELECT id, name FROM sticker_library WHERE name NOT LIKE 'e1:%';", r => (r.GetInt64(0), r.GetString(1)), tx))
                n += c.Exec("UPDATE sticker_library SET name = $n WHERE id = $id;", tx, ("$n", Vault.Seal(name)), ("$id", id));
            return n;
        });
        count += Stickers.EncryptLegacyFiles();
        if (count > 0) Compact();
        return count;
    }

    /// <summary>Reescribe el archivo para que no queden páginas antiguas con texto sin cifrar.</summary>
    private void Compact()
    {
        Db.Read(c =>
        {
            c.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            c.Exec("VACUUM;");
            c.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            return 0;
        });
    }

    // ======================= Descifrado tolerante para la interfaz =======================

    /// <summary>Descifra; si está bloqueado o el valor es ilegible devuelve un marcador en lugar de fallar.</summary>
    private string? TryOpen(string? value)
    {
        if (value is null || !Vault.IsSealed(value)) return value;
        try { return Vault.Open(value); }
        catch (VaultLockedException) { return null; }
        catch (CryptographicException) { return "(no disponible)"; }
        catch (FormatException) { return "(no disponible)"; }
    }

    private string RevealName(string value) => TryOpen(value) ?? "(bloqueado)";

    private Contact? Reveal(Contact? c) =>
        c is null ? null : c with { DisplayName = RevealName(c.DisplayName), MachineName = RevealName(c.MachineName) };

    private ChatMessage? Reveal(ChatMessage? m) => m is null ? null : AttachFile(m with { Text = TryOpen(m.Text) });

    private string? SealName(string value) => Vault.Exists ? Vault.Seal(value) : value;
}
