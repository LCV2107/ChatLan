using Microsoft.Data.Sqlite;
using MiniChat.Core.Diagnostics;

namespace MiniChat.Core.Persistence;

/// <summary>
/// Migraciones de esquema. Reglas para versiones futuras:
///  - Añadir una entrada nueva al final; nunca editar una migración publicada.
///  - Solo cambios aditivos o con copia de datos; nunca DROP de datos de usuario sin migrarlos.
///  - Antes de una migración que modifique datos se copia el archivo a backups\ (las solo aditivas no lo necesitan).
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    {
        // ---------------- v1 ----------------
        """
        CREATE TABLE profile (
            id              INTEGER PRIMARY KEY CHECK (id = 1),
            display_name    TEXT NOT NULL,
            created_at      INTEGER NOT NULL
        );
        CREATE TABLE identity (
            id              INTEGER PRIMARY KEY CHECK (id = 1),
            key_name        TEXT NOT NULL,
            fingerprint     TEXT NOT NULL,
            created_at      INTEGER NOT NULL
        );
        CREATE TABLE settings (
            key             TEXT PRIMARY KEY,
            value           TEXT NOT NULL
        );
        CREATE TABLE contacts (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            fingerprint     TEXT NOT NULL UNIQUE,
            display_name    TEXT NOT NULL,
            machine_name    TEXT NOT NULL,
            last_address    TEXT,
            last_port       INTEGER,
            first_seen_at   INTEGER NOT NULL,
            last_seen_at    INTEGER
        );
        CREATE TABLE relationships (
            contact_id      INTEGER PRIMARY KEY REFERENCES contacts(id),
            state           INTEGER NOT NULL,
            blocked         INTEGER NOT NULL DEFAULT 0,
            changed_at      INTEGER NOT NULL
        );
        CREATE TABLE requests (
            id                      TEXT PRIMARY KEY,
            contact_id              INTEGER NOT NULL REFERENCES contacts(id),
            direction               INTEGER NOT NULL,
            status                  INTEGER NOT NULL,
            created_at              INTEGER NOT NULL,
            resolved_at             INTEGER,
            decision_delivered      INTEGER NOT NULL DEFAULT 1,
            last_delivery_attempt   INTEGER
        );
        CREATE INDEX ix_requests_contact ON requests(contact_id, created_at);
        CREATE TABLE request_log (
            fingerprint     TEXT NOT NULL,
            at              INTEGER NOT NULL
        );
        CREATE INDEX ix_request_log ON request_log(fingerprint, at);
        CREATE TABLE conversations (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id      INTEGER NOT NULL UNIQUE REFERENCES contacts(id),
            next_seq        INTEGER NOT NULL DEFAULT 1,
            unread_count    INTEGER NOT NULL DEFAULT 0,
            draft           TEXT,
            created_at      INTEGER NOT NULL
        );
        CREATE TABLE sticker_blobs (
            hash            TEXT PRIMARY KEY,
            width           INTEGER NOT NULL,
            height          INTEGER NOT NULL,
            size            INTEGER NOT NULL,
            created_at      INTEGER NOT NULL
        );
        CREATE TABLE messages (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id     INTEGER NOT NULL REFERENCES conversations(id),
            uid                 TEXT NOT NULL,
            direction           INTEGER NOT NULL,
            kind                INTEGER NOT NULL,
            body                TEXT,
            sticker_hash        TEXT REFERENCES sticker_blobs(hash),
            status              INTEGER NOT NULL,
            seq                 INTEGER NOT NULL,
            sender_time         INTEGER NOT NULL,
            local_time          INTEGER NOT NULL,
            status_changed_at   INTEGER NOT NULL,
            error               TEXT,
            UNIQUE (conversation_id, direction, uid)
        );
        CREATE INDEX ix_messages_conversation ON messages(conversation_id, id);
        CREATE INDEX ix_messages_status ON messages(status) WHERE status IN (0, 1, 3, 4);
        CREATE TABLE sticker_library (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            hash            TEXT NOT NULL UNIQUE REFERENCES sticker_blobs(hash),
            name            TEXT NOT NULL,
            favorite        INTEGER NOT NULL DEFAULT 0,
            added_at        INTEGER NOT NULL,
            last_used_at    INTEGER,
            use_count       INTEGER NOT NULL DEFAULT 0
        );
        """,
        // ---------------- v2: cifrado del historial (solo añade una tabla) ----------------
        """
        CREATE TABLE vault (
            id                  INTEGER PRIMARY KEY CHECK (id = 1),
            version             INTEGER NOT NULL,
            salt                BLOB NOT NULL,
            iterations          INTEGER NOT NULL,
            protected_secret    BLOB NOT NULL,
            public_key          BLOB NOT NULL,
            wrapped_private_key BLOB NOT NULL,
            created_at          INTEGER NOT NULL
        );
        """,
        // ---------------- v3: documentos adjuntos (solo añade una tabla) ----------------
        """
        CREATE TABLE files (
            fid             TEXT PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations(id),
            direction       INTEGER NOT NULL,
            name            TEXT NOT NULL,
            size            INTEGER NOT NULL,
            sha256          TEXT NOT NULL,
            state           INTEGER NOT NULL,
            transferred     INTEGER NOT NULL DEFAULT 0,
            file_key        TEXT,
            peer_notified   INTEGER NOT NULL DEFAULT 1,
            error           TEXT,
            created_at      INTEGER NOT NULL,
            updated_at      INTEGER NOT NULL
        );
        CREATE INDEX ix_files_conversation ON files(conversation_id);
        """,
    };

    /// <summary>
    /// Versiones cuya migración solo añade estructuras. No requieren copia de seguridad previa (y así no
    /// quedan copias sin cifrar del historial al pasar a la versión con cifrado).
    /// </summary>
    private static readonly HashSet<long> AdditiveOnly = new() { 2, 3 };

    public static int LatestVersion => Steps.Length;

    public static void Apply(SqliteConnection conn, string dbPath, string backupDirectory, bool existed, ILog log)
    {
        long current = (long)(conn.Scalar("PRAGMA user_version;") ?? 0L);
        if (current > LatestVersion)
            throw new DatabaseUnavailableException(
                $"Los datos fueron creados por una versión más reciente de MiniChat (esquema {current}; esta versión entiende hasta {LatestVersion}). " +
                "No se han modificado. Instale la versión más reciente.", corrupt: false);
        if (current == LatestVersion) return;

        bool needsBackup = false;
        for (long v = current + 1; v <= LatestVersion; v++) needsBackup |= !AdditiveOnly.Contains(v);
        if (existed && current > 0 && needsBackup)
        {
            Directory.CreateDirectory(backupDirectory);
            var backup = System.IO.Path.Combine(backupDirectory, $"minichat-v{current}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            // VACUUM INTO produce una copia coherente aunque haya WAL pendiente.
            conn.Exec("VACUUM INTO $p;", null, ("$p", backup));
            log.Info("db", $"Copia de seguridad antes de migrar: {System.IO.Path.GetFileName(backup)}");
        }

        for (long v = current; v < LatestVersion; v++)
        {
            using var tx = conn.BeginTransaction();
            conn.Exec(Steps[v], tx);
            conn.Exec($"PRAGMA user_version = {v + 1};", tx);
            tx.Commit();
            log.Info("db", $"Esquema migrado a v{v + 1}");
        }
    }
}
