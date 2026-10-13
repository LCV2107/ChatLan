using Microsoft.Data.Sqlite;
using MiniChat.Core.Model;

namespace MiniChat.Core.Persistence;

// Repositorios SQL. Cada método recibe la conexión y (opcionalmente) la transacción para que los
// servicios puedan componer varias operaciones de forma atómica. No conocen el formato de red.

public static class ProfileRepo
{
    public static string? GetDisplayName(SqliteConnection c, SqliteTransaction? tx = null) =>
        c.Scalar("SELECT display_name FROM profile WHERE id = 1;", tx) as string;

    public static void SetDisplayName(SqliteConnection c, SqliteTransaction tx, string name) =>
        c.Exec("INSERT INTO profile (id, display_name, created_at) VALUES (1, $n, $t) ON CONFLICT(id) DO UPDATE SET display_name = $n;",
            tx, ("$n", name), ("$t", Time.Now()));

    public static (string KeyName, string Fingerprint)? GetIdentity(SqliteConnection c, SqliteTransaction? tx = null) =>
        c.Query("SELECT key_name, fingerprint FROM identity WHERE id = 1;", r => (r.GetString(0), r.GetString(1)), tx)
         .Cast<(string, string)?>().FirstOrDefault();

    public static void SaveIdentity(SqliteConnection c, SqliteTransaction tx, string keyName, string fingerprint) =>
        c.Exec("INSERT INTO identity (id, key_name, fingerprint, created_at) VALUES (1, $k, $f, $t) " +
               "ON CONFLICT(id) DO UPDATE SET key_name = $k, fingerprint = $f, created_at = $t;",
            tx, ("$k", keyName), ("$f", fingerprint), ("$t", Time.Now()));
}

public static class SettingsRepo
{
    public static string? Get(SqliteConnection c, string key, SqliteTransaction? tx = null) =>
        c.Scalar("SELECT value FROM settings WHERE key = $k;", tx, ("$k", key)) as string;

    public static Dictionary<string, string> GetAll(SqliteConnection c) =>
        c.Query("SELECT key, value FROM settings;", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);

    public static void Set(SqliteConnection c, SqliteTransaction tx, string key, string value) =>
        c.Exec("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;", tx, ("$k", key), ("$v", value));
}

public static class ContactsRepo
{
    private const string Select =
        "SELECT c.id, c.fingerprint, c.display_name, c.machine_name, c.last_address, c.last_port, " +
        "COALESCE(r.state, 0), COALESCE(r.blocked, 0), COALESCE(r.changed_at, c.first_seen_at), c.first_seen_at, c.last_seen_at " +
        "FROM contacts c LEFT JOIN relationships r ON r.contact_id = c.id ";

    private static Contact Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Str(4), (int?)r.Long(5),
        (Relation)r.GetInt32(6), r.GetInt32(7) != 0, Time.From(r.GetInt64(8)), Time.From(r.GetInt64(9)), Time.From(r.Long(10)));

    public static Contact? Get(SqliteConnection c, long id, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE c.id = $id;", Map, tx, ("$id", id)).FirstOrDefault();

    public static Contact? GetByFingerprint(SqliteConnection c, string fp, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE c.fingerprint = $fp;", Map, tx, ("$fp", fp)).FirstOrDefault();

    public static List<Contact> List(SqliteConnection c) => c.Query(Select + "ORDER BY c.display_name COLLATE NOCASE;", Map);

    /// <summary>Crea el contacto si no existe y actualiza nombre, PC y última dirección (un cambio de IP no crea otro contacto).</summary>
    public static Contact Upsert(SqliteConnection c, SqliteTransaction tx, string fp, string displayName, string machineName, string? address, int? port)
    {
        long now = Time.Now();
        c.Exec("""
            INSERT INTO contacts (fingerprint, display_name, machine_name, last_address, last_port, first_seen_at, last_seen_at)
            VALUES ($fp, $dn, $pc, $a, $p, $t, $t)
            ON CONFLICT(fingerprint) DO UPDATE SET
                display_name = $dn, machine_name = $pc,
                last_address = COALESCE($a, last_address), last_port = COALESCE($p, last_port), last_seen_at = $t;
            """, tx, ("$fp", fp), ("$dn", displayName), ("$pc", machineName), ("$a", address), ("$p", port), ("$t", now));
        var contact = GetByFingerprint(c, fp, tx)!;
        c.Exec("INSERT OR IGNORE INTO relationships (contact_id, state, blocked, changed_at) VALUES ($id, 0, 0, $t);",
            tx, ("$id", contact.Id), ("$t", now));
        return GetByFingerprint(c, fp, tx)!;
    }

    public static void SetRelation(SqliteConnection c, SqliteTransaction tx, long contactId, Relation relation) =>
        c.Exec("UPDATE relationships SET state = $s, changed_at = $t WHERE contact_id = $id;",
            tx, ("$s", (int)relation), ("$t", Time.Now()), ("$id", contactId));

    public static void SetBlocked(SqliteConnection c, SqliteTransaction tx, long contactId, bool blocked) =>
        c.Exec("UPDATE relationships SET blocked = $b, changed_at = $t WHERE contact_id = $id;",
            tx, ("$b", blocked ? 1 : 0), ("$t", Time.Now()), ("$id", contactId));
}

public static class RequestsRepo
{
    private const string Select = "SELECT id, contact_id, direction, status, created_at, resolved_at, decision_delivered, last_delivery_attempt FROM requests ";

    private static ChatRequest Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), (RequestDirection)r.GetInt32(2), (RequestStatus)r.GetInt32(3),
        Time.From(r.GetInt64(4)), Time.From(r.Long(5)), r.GetInt32(6) != 0, Time.From(r.Long(7)));

    public static ChatRequest? Get(SqliteConnection c, string id, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE id = $id;", Map, tx, ("$id", id)).FirstOrDefault();

    public static ChatRequest? GetPending(SqliteConnection c, long contactId, RequestDirection dir, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE contact_id = $c AND direction = $d AND status = 0 ORDER BY created_at DESC LIMIT 1;",
            Map, tx, ("$c", contactId), ("$d", (int)dir)).FirstOrDefault();

    public static ChatRequest? GetLatest(SqliteConnection c, long contactId, RequestDirection dir, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE contact_id = $c AND direction = $d ORDER BY created_at DESC LIMIT 1;",
            Map, tx, ("$c", contactId), ("$d", (int)dir)).FirstOrDefault();

    public static void Insert(SqliteConnection c, SqliteTransaction tx, string id, long contactId, RequestDirection dir) =>
        c.Exec("INSERT INTO requests (id, contact_id, direction, status, created_at) VALUES ($id, $c, $d, 0, $t) ON CONFLICT(id) DO NOTHING;",
            tx, ("$id", id), ("$c", contactId), ("$d", (int)dir), ("$t", Time.Now()));

    public static void Resolve(SqliteConnection c, SqliteTransaction tx, string id, RequestStatus status, bool decisionDelivered = true) =>
        c.Exec("UPDATE requests SET status = $s, resolved_at = $t, decision_delivered = $dd WHERE id = $id;",
            tx, ("$s", (int)status), ("$t", Time.Now()), ("$dd", decisionDelivered ? 1 : 0), ("$id", id));

    /// <summary>Cierra todas las solicitudes pendientes del contacto (en ambas direcciones).</summary>
    public static void CancelAllPending(SqliteConnection c, SqliteTransaction tx, long contactId) =>
        c.Exec("UPDATE requests SET status = 3, resolved_at = $t, decision_delivered = 1 WHERE contact_id = $c AND status = 0;",
            tx, ("$t", Time.Now()), ("$c", contactId));

    public static void MarkDelivered(SqliteConnection c, SqliteTransaction tx, string id) =>
        c.Exec("UPDATE requests SET decision_delivered = 1 WHERE id = $id;", tx, ("$id", id));

    public static void RecordDeliveryAttempt(SqliteConnection c, SqliteTransaction tx, string id) =>
        c.Exec("UPDATE requests SET last_delivery_attempt = $t WHERE id = $id;", tx, ("$t", Time.Now()), ("$id", id));

    public static List<ChatRequest> UndeliveredDecisions(SqliteConnection c) =>
        c.Query(Select + "WHERE direction = 1 AND status IN (1, 2) AND decision_delivered = 0;", Map);

    public static List<ChatRequest> Pending(SqliteConnection c, RequestDirection dir) =>
        c.Query(Select + "WHERE direction = $d AND status = 0;", Map, null, ("$d", (int)dir));

    public static void LogIncoming(SqliteConnection c, SqliteTransaction tx, string fp) =>
        c.Exec("INSERT INTO request_log (fingerprint, at) VALUES ($f, $t);", tx, ("$f", fp), ("$t", Time.Now()));

    public static int CountIncoming(SqliteConnection c, SqliteTransaction tx, string? fp, DateTimeOffset since) =>
        Convert.ToInt32(fp is null
            ? c.Scalar("SELECT COUNT(*) FROM request_log WHERE at >= $s;", tx, ("$s", since.ToUnixTimeMilliseconds()))
            : c.Scalar("SELECT COUNT(*) FROM request_log WHERE fingerprint = $f AND at >= $s;", tx, ("$f", fp), ("$s", since.ToUnixTimeMilliseconds())));

    public static void PruneLog(SqliteConnection c, SqliteTransaction tx, DateTimeOffset before) =>
        c.Exec("DELETE FROM request_log WHERE at < $b;", tx, ("$b", before.ToUnixTimeMilliseconds()));
}

public static class MessagesRepo
{
    private const string Select =
        "SELECT id, conversation_id, uid, direction, kind, body, sticker_hash, status, seq, sender_time, local_time, error FROM messages ";

    private static ChatMessage Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), (MessageDirection)r.GetInt32(3), (MessageKind)r.GetInt32(4),
        r.Str(5), r.Str(6), (MessageStatus)r.GetInt32(7), r.GetInt64(8), Time.From(r.GetInt64(9)), Time.From(r.GetInt64(10)), r.Str(11));

    public static Conversation GetOrCreateConversation(SqliteConnection c, SqliteTransaction tx, long contactId)
    {
        c.Exec("INSERT OR IGNORE INTO conversations (contact_id, created_at) VALUES ($c, $t);", tx, ("$c", contactId), ("$t", Time.Now()));
        return GetConversation(c, contactId, tx)!;
    }

    public static Conversation? GetConversation(SqliteConnection c, long contactId, SqliteTransaction? tx = null) =>
        c.Query("SELECT id, contact_id, unread_count, draft FROM conversations WHERE contact_id = $c;",
            r => new Conversation(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.Str(3)), tx, ("$c", contactId)).FirstOrDefault();

    public static List<Conversation> ListConversations(SqliteConnection c) =>
        c.Query("SELECT id, contact_id, unread_count, draft FROM conversations;", r => new Conversation(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.Str(3)));

    public static ChatMessage InsertOutgoing(SqliteConnection c, SqliteTransaction tx, long conversationId, MessageKind kind, string? text, string? stickerHash, string? uid = null)
    {
        long seq = (long)c.Scalar("UPDATE conversations SET next_seq = next_seq + 1 WHERE id = $id RETURNING next_seq - 1;", tx, ("$id", conversationId))!;
        long now = Time.Now();
        uid ??= Guid.NewGuid().ToString("N");
        long id = (long)c.Scalar("""
            INSERT INTO messages (conversation_id, uid, direction, kind, body, sticker_hash, status, seq, sender_time, local_time, status_changed_at)
            VALUES ($cv, $u, 0, $k, $b, $sh, 0, $seq, $t, $t, $t) RETURNING id;
            """, tx, ("$cv", conversationId), ("$u", uid), ("$k", (int)kind), ("$b", text), ("$sh", stickerHash), ("$seq", seq), ("$t", now))!;
        return Get(c, id, tx)!;
    }

    /// <summary>Inserta un mensaje entrante. Si ya existía (mismo uid: reintento del emisor) devuelve inserted=false.</summary>
    public static (bool Inserted, ChatMessage Message) InsertIncoming(SqliteConnection c, SqliteTransaction tx, long conversationId,
        string uid, MessageKind kind, string? text, string? stickerHash, long seq, DateTimeOffset senderTime)
    {
        long now = Time.Now();
        var id = c.Scalar("""
            INSERT INTO messages (conversation_id, uid, direction, kind, body, sticker_hash, status, seq, sender_time, local_time, status_changed_at)
            VALUES ($cv, $u, 1, $k, $b, $sh, 5, $seq, $st, $t, $t)
            ON CONFLICT(conversation_id, direction, uid) DO NOTHING RETURNING id;
            """, tx, ("$cv", conversationId), ("$u", uid), ("$k", (int)kind), ("$b", text), ("$sh", stickerHash),
            ("$seq", seq), ("$st", senderTime.ToUnixTimeMilliseconds()), ("$t", now));
        if (id is long newId)
        {
            c.Exec("UPDATE conversations SET unread_count = unread_count + 1 WHERE id = $id;", tx, ("$id", conversationId));
            return (true, Get(c, newId, tx)!);
        }
        var existing = c.Query(Select + "WHERE conversation_id = $cv AND direction = 1 AND uid = $u;", Map, tx, ("$cv", conversationId), ("$u", uid)).First();
        return (false, existing);
    }

    public static ChatMessage? Get(SqliteConnection c, long id, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE id = $id;", Map, tx, ("$id", id)).FirstOrDefault();

    public static void SetStatus(SqliteConnection c, SqliteTransaction tx, long id, MessageStatus status, string? error) =>
        c.Exec("UPDATE messages SET status = $s, error = $e, status_changed_at = $t WHERE id = $id;",
            tx, ("$s", (int)status), ("$e", error), ("$t", Time.Now()), ("$id", id));

    /// <summary>Página de mensajes en orden local estable (id ascendente), anteriores a <paramref name="beforeId"/>.</summary>
    public static List<ChatMessage> Page(SqliteConnection c, long conversationId, long? beforeId, int limit)
    {
        var rows = c.Query(Select + "WHERE conversation_id = $cv AND id < $b ORDER BY id DESC LIMIT $l;", Map, null,
            ("$cv", conversationId), ("$b", beforeId ?? long.MaxValue), ("$l", limit));
        rows.Reverse();
        return rows;
    }

    public static List<ChatMessage> After(SqliteConnection c, long conversationId, long afterId, int limit) =>
        c.Query(Select + "WHERE conversation_id = $cv AND id > $a ORDER BY id ASC LIMIT $l;", Map, null,
            ("$cv", conversationId), ("$a", afterId), ("$l", limit));

    /// <summary>Mensajes salientes aún no entregados, en orden.</summary>
    public static List<ChatMessage> Undelivered(SqliteConnection c, long conversationId, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE conversation_id = $cv AND direction = 0 AND status IN (0, 1, 3, 4) ORDER BY id ASC;", Map, tx, ("$cv", conversationId));

    /// <summary>Al arrancar: lo que quedó "enviando" al cerrarse la app pasa a "no confirmado".</summary>
    public static int RecoverInterruptedSends(SqliteConnection c, SqliteTransaction tx) =>
        c.Exec("UPDATE messages SET status = 3, error = 'La aplicación se cerró antes de recibir la confirmación.', status_changed_at = $t WHERE status = 1;",
            tx, ("$t", Time.Now()));

    public static void ResetUnread(SqliteConnection c, SqliteTransaction tx, long conversationId) =>
        c.Exec("UPDATE conversations SET unread_count = 0 WHERE id = $id;", tx, ("$id", conversationId));

    public static void SetDraft(SqliteConnection c, SqliteTransaction tx, long conversationId, string? draft) =>
        c.Exec("UPDATE conversations SET draft = $d WHERE id = $id;", tx, ("$d", string.IsNullOrEmpty(draft) ? null : draft), ("$id", conversationId));

    public static int DeleteAll(SqliteConnection c, SqliteTransaction tx, long conversationId)
    {
        c.Exec("UPDATE conversations SET unread_count = 0 WHERE id = $id;", tx, ("$id", conversationId));
        return c.Exec("DELETE FROM messages WHERE conversation_id = $id;", tx, ("$id", conversationId));
    }

    public static int Count(SqliteConnection c, long conversationId) =>
        Convert.ToInt32(c.Scalar("SELECT COUNT(*) FROM messages WHERE conversation_id = $id;", null, ("$id", conversationId)));
}

public static class StickersRepo
{
    public static bool InsertBlobIfMissing(SqliteConnection c, SqliteTransaction tx, StickerBlob blob) =>
        c.Exec("INSERT OR IGNORE INTO sticker_blobs (hash, width, height, size, created_at) VALUES ($h, $w, $hh, $s, $t);",
            tx, ("$h", blob.Hash), ("$w", blob.Width), ("$hh", blob.Height), ("$s", blob.Size), ("$t", Time.Now())) > 0;

    public static StickerBlob? GetBlob(SqliteConnection c, string hash, SqliteTransaction? tx = null) =>
        c.Query("SELECT hash, width, height, size FROM sticker_blobs WHERE hash = $h;",
            r => new StickerBlob(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3)), tx, ("$h", hash)).FirstOrDefault();

    /// <summary>Añade a la biblioteca; devuelve false si ya estaba (evita duplicados).</summary>
    public static bool AddToLibrary(SqliteConnection c, SqliteTransaction tx, string hash, string name) =>
        c.Exec("INSERT OR IGNORE INTO sticker_library (hash, name, added_at) VALUES ($h, $n, $t);", tx, ("$h", hash), ("$n", name), ("$t", Time.Now())) > 0;

    private const string LibrarySelect =
        "SELECT l.id, l.hash, l.name, l.favorite, l.added_at, l.last_used_at, b.width, b.height FROM sticker_library l JOIN sticker_blobs b ON b.hash = l.hash ";

    private static LibrarySticker Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, Time.From(r.GetInt64(4)), Time.From(r.Long(5)), r.GetInt32(6), r.GetInt32(7));

    public static List<LibrarySticker> Library(SqliteConnection c) =>
        c.Query(LibrarySelect + "ORDER BY l.favorite DESC, l.added_at DESC;", Map);

    public static List<LibrarySticker> Recent(SqliteConnection c, int limit) =>
        c.Query(LibrarySelect + "WHERE l.last_used_at IS NOT NULL ORDER BY l.last_used_at DESC LIMIT $l;", Map, null, ("$l", limit));

    public static void SetFavorite(SqliteConnection c, SqliteTransaction tx, long id, bool favorite) =>
        c.Exec("UPDATE sticker_library SET favorite = $f WHERE id = $id;", tx, ("$f", favorite ? 1 : 0), ("$id", id));

    public static void MarkUsed(SqliteConnection c, SqliteTransaction tx, string hash) =>
        c.Exec("UPDATE sticker_library SET last_used_at = $t, use_count = use_count + 1 WHERE hash = $h;", tx, ("$t", Time.Now()), ("$h", hash));

    public static void RemoveFromLibrary(SqliteConnection c, SqliteTransaction tx, long id) =>
        c.Exec("DELETE FROM sticker_library WHERE id = $id;", tx, ("$id", id));

    /// <summary>Blobs que ya no usa ni la biblioteca ni ningún mensaje.</summary>
    public static List<string> Unreferenced(SqliteConnection c, SqliteTransaction tx) =>
        c.Query("""
            SELECT b.hash FROM sticker_blobs b
            WHERE NOT EXISTS (SELECT 1 FROM sticker_library l WHERE l.hash = b.hash)
              AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.sticker_hash = b.hash);
            """, r => r.GetString(0), tx);

    public static void DeleteBlob(SqliteConnection c, SqliteTransaction tx, string hash) =>
        c.Exec("DELETE FROM sticker_blobs WHERE hash = $h;", tx, ("$h", hash));

    public static HashSet<string> AllBlobHashes(SqliteConnection c) =>
        c.Query("SELECT hash FROM sticker_blobs;", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
}

public static class VaultRepo
{
    public static Security.VaultRecord? Get(SqliteConnection c) =>
        c.Query("SELECT version, salt, iterations, protected_secret, public_key, wrapped_private_key FROM vault WHERE id = 1;",
            r => new Security.VaultRecord(r.GetInt32(0), (byte[])r[1], r.GetInt32(2), (byte[])r[3], (byte[])r[4], (byte[])r[5])).FirstOrDefault();

    public static void Save(SqliteConnection c, SqliteTransaction tx, Security.VaultRecord v) =>
        c.Exec("""
            INSERT INTO vault (id, version, salt, iterations, protected_secret, public_key, wrapped_private_key, created_at)
            VALUES (1, $v, $s, $i, $p, $k, $w, $t)
            ON CONFLICT(id) DO UPDATE SET version = $v, salt = $s, iterations = $i, protected_secret = $p, public_key = $k, wrapped_private_key = $w;
            """, tx, ("$v", v.Version), ("$s", v.Salt), ("$i", v.Iterations), ("$p", v.ProtectedSecret), ("$k", v.PublicKey), ("$w", v.WrappedPrivateKey), ("$t", Time.Now()));

    public static void Delete(SqliteConnection c, SqliteTransaction tx) => c.Exec("DELETE FROM vault;", tx);
}

public static class FilesRepo
{
    private const string Select = "SELECT fid, conversation_id, direction, name, size, sha256, state, transferred, error FROM files ";

    private static FileTransfer Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), (MessageDirection)r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
        (FileState)r.GetInt32(6), r.GetInt64(7), r.Str(8));

    public static FileTransfer? Get(SqliteConnection c, string fid, SqliteTransaction? tx = null) =>
        c.Query(Select + "WHERE fid = $f;", Map, tx, ("$f", fid)).FirstOrDefault();

    public static List<FileTransfer> ForConversation(SqliteConnection c, long conversationId) =>
        c.Query(Select + "WHERE conversation_id = $c;", Map, null, ("$c", conversationId));

    public static List<FileTransfer> InState(SqliteConnection c, MessageDirection dir, FileState state) =>
        c.Query(Select + "WHERE direction = $d AND state = $s;", Map, null, ("$d", (int)dir), ("$s", (int)state));

    /// <summary>Inserta si no existe (reintentos del emisor). Devuelve true si se insertó.</summary>
    public static bool Insert(SqliteConnection c, SqliteTransaction tx, string fid, long conversationId, MessageDirection dir,
        string sealedName, long size, string sha, FileState state, string? sealedKey) =>
        c.Exec("""
            INSERT INTO files (fid, conversation_id, direction, name, size, sha256, state, transferred, file_key, created_at, updated_at)
            VALUES ($f, $c, $d, $n, $s, $h, $st, 0, $k, $t, $t) ON CONFLICT(fid) DO NOTHING;
            """, tx, ("$f", fid), ("$c", conversationId), ("$d", (int)dir), ("$n", sealedName), ("$s", size), ("$h", sha),
            ("$st", (int)state), ("$k", sealedKey), ("$t", Time.Now())) > 0;

    public static void SetState(SqliteConnection c, SqliteTransaction tx, string fid, FileState state, string? error, bool? peerNotified = null) =>
        c.Exec("UPDATE files SET state = $s, error = $e, updated_at = $t" + (peerNotified is null ? "" : ", peer_notified = $p") + " WHERE fid = $f;",
            tx, ("$s", (int)state), ("$e", error), ("$t", Time.Now()), ("$p", peerNotified == true ? 1 : 0), ("$f", fid));

    public static void SetProgress(SqliteConnection c, SqliteTransaction tx, string fid, long transferred) =>
        c.Exec("UPDATE files SET transferred = $x, updated_at = $t WHERE fid = $f;", tx, ("$x", transferred), ("$t", Time.Now()), ("$f", fid));

    public static void SetKey(SqliteConnection c, SqliteTransaction tx, string fid, string sealedKey) =>
        c.Exec("UPDATE files SET file_key = $k WHERE fid = $f;", tx, ("$k", sealedKey), ("$f", fid));

    public static string? GetKey(SqliteConnection c, string fid) => c.Scalar("SELECT file_key FROM files WHERE fid = $f;", null, ("$f", fid)) as string;

    public static void MarkNotified(SqliteConnection c, SqliteTransaction tx, string fid) =>
        c.Exec("UPDATE files SET peer_notified = 1 WHERE fid = $f;", tx, ("$f", fid));

    /// <summary>Decisiones del receptor (recibido/rechazado) que aún no llegaron al emisor.</summary>
    public static List<FileTransfer> PendingNotifications(SqliteConnection c) =>
        c.Query(Select + "WHERE direction = 1 AND peer_notified = 0;", Map);

    public static List<string> DeleteForConversation(SqliteConnection c, SqliteTransaction tx, long conversationId)
    {
        var ids = c.Query("SELECT fid FROM files WHERE conversation_id = $c;", r => r.GetString(0), tx, ("$c", conversationId));
        c.Exec("DELETE FROM files WHERE conversation_id = $c;", tx, ("$c", conversationId));
        return ids;
    }
}
