using Microsoft.Data.Sqlite;
using MiniChat.Core.Diagnostics;

namespace MiniChat.Core.Persistence;

/// <summary>
/// Acceso a SQLite. Decisiones:
///  - WAL + synchronous=FULL: cada transacción confirmada sobrevive a un cierre inesperado o un corte de
///    energía. Se usa FULL (no NORMAL) porque "entregado" promete que el receptor guardó el mensaje.
///  - Todas las escrituras pasan por transacciones y un candado del proceso.
///  - Migraciones numeradas con PRAGMA user_version y copia de seguridad previa del archivo.
///  - Si el archivo está dañado o es de una versión futura, NO se modifica ni se borra: se informa.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();
    private readonly ILog _log;

    private Database(string path, ILog log)
    {
        Path = path;
        _log = log;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Abre (o crea) la base de datos, comprueba su integridad y aplica migraciones.</summary>
    /// <exception cref="DatabaseUnavailableException">Archivo dañado, ilegible o de una versión más nueva.</exception>
    public static Database Open(string path, string backupDirectory, ILog? log = null)
    {
        log ??= NullLog.Instance;
        var db = new Database(path, log);
        bool existed = File.Exists(path) && new FileInfo(path).Length > 0;
        try
        {
            using var conn = db.OpenConnection();
            if (existed)
            {
                using var check = conn.CreateCommand();
                check.CommandText = "PRAGMA quick_check;";
                var result = check.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new DatabaseUnavailableException(
                        $"La base de datos parece dañada ({result}). No se ha modificado ni borrado nada. Archivo: {path}", corrupt: true);
            }
            using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }
            Migrations.Apply(conn, path, backupDirectory, existed, log);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            throw new DatabaseUnavailableException(
                $"El archivo de datos no es legible ({ex.Message}). No se ha modificado ni borrado nada. Archivo: {path}", corrupt: true, ex);
        }
        catch (SqliteException ex)
        {
            throw new DatabaseUnavailableException($"No se pudo abrir la base de datos: {ex.Message}", corrupt: false, ex);
        }
        return db;
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // secure_delete: lo borrado se sobrescribe con ceros en el archivo (no quedan restos legibles).
            cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000; PRAGMA secure_delete=ON;";
            cmd.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            // Sin esto, un archivo ilegible quedaría bloqueado por una conexión huérfana.
            conn.Dispose();
            SqliteConnection.ClearPool(conn);
            throw;
        }
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var conn = OpenConnection();
        return work(conn);
    }

    /// <summary>Ejecuta trabajo de escritura dentro de una transacción serializada.</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        lock (_writeGate)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var result = work(conn, tx);
            tx.Commit();
            return result;
        }
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> work) => Write<object?>((c, t) => { work(c, t); return null; });

    /// <summary>Libera los archivos (necesario antes de copiar/mover la base de datos).</summary>
    public static void ReleaseAll() => SqliteConnection.ClearAllPools();
}

public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string message, bool corrupt, Exception? inner = null) : base(message, inner) => IsCorrupt = corrupt;
    public bool IsCorrupt { get; }
}

internal static class Sql
{
    public static SqliteCommand Cmd(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
