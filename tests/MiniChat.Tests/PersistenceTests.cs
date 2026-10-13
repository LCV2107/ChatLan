using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MiniChat.Core.Model;
using MiniChat.Core.Persistence;

namespace MiniChat.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "db-" + Guid.NewGuid().ToString("N")[..8]);

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, "minichat.db");
    private string Backups => Path.Combine(_dir, "backups");

    [Fact]
    public void NewDatabase_IsCreatedAtLatestSchema_WithWal()
    {
        var db = Database.Open(DbPath, Backups);
        Assert.Equal((long)Migrations.LatestVersion, db.Read(c => (long)c.Scalar("PRAGMA user_version;")!));
        Assert.Equal("wal", db.Read(c => (string)c.Scalar("PRAGMA journal_mode;")!));
    }

    [Fact]
    public void CorruptFile_IsReported_AndLeftUntouched()
    {
        var garbage = RandomNumberGenerator.GetBytes(64 * 1024);
        File.WriteAllBytes(DbPath, garbage);
        var ex = Assert.Throws<DatabaseUnavailableException>(() => Database.Open(DbPath, Backups));
        Assert.True(ex.IsCorrupt);
        SqliteConnection.ClearAllPools();
        Assert.Equal(garbage, File.ReadAllBytes(DbPath)); // no se borró ni se modificó
    }

    [Fact]
    public void DamagedPages_AreDetected_WithoutDeletingData()
    {
        var db = Database.Open(DbPath, Backups);
        db.Write((c, tx) =>
        {
            for (int i = 0; i < 300; i++)
                c.Exec("INSERT INTO settings (key, value) VALUES ($k, $v);", tx, ("$k", "k" + i), ("$v", new string('x', 500)));
        });
        db.Read(c => c.Exec("PRAGMA wal_checkpoint(TRUNCATE);"));
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(DbPath);
        for (int i = 4096 * 3; i < 4096 * 3 + 2000; i++) bytes[i] = 0xAB; // daña una página interior
        File.WriteAllBytes(DbPath, bytes);
        var ex = Assert.Throws<DatabaseUnavailableException>(() => Database.Open(DbPath, Backups));
        Assert.True(ex.IsCorrupt);
        SqliteConnection.ClearAllPools();
        Assert.Equal(bytes, File.ReadAllBytes(DbPath));
    }

    [Fact]
    public void DatabaseFromNewerVersion_IsRefused_NotDowngraded()
    {
        var db = Database.Open(DbPath, Backups);
        db.Read(c => c.Exec("PRAGMA user_version = 99;"));
        SqliteConnection.ClearAllPools();
        var ex = Assert.Throws<DatabaseUnavailableException>(() => Database.Open(DbPath, Backups));
        Assert.Contains("más reciente", ex.Message);
        Assert.False(ex.IsCorrupt);
    }

    [Fact]
    public void FailedTransaction_LeavesNoPartialState()
    {
        var db = Database.Open(DbPath, Backups);
        Assert.ThrowsAny<Exception>(() => db.Write((c, tx) =>
        {
            var contact = ContactsRepo.Upsert(c, tx, new string('A', 64), "Ana", "PC", null, null);
            MessagesRepo.GetOrCreateConversation(c, tx, contact.Id);
            throw new IOException("fallo simulado a mitad");
        }));
        Assert.Empty(db.Read(ContactsRepo.List));
        Assert.Empty(db.Read(MessagesRepo.ListConversations));
    }

    [Fact]
    public void SameIdentity_NewAddress_UpdatesContact_DoesNotDuplicate()
    {
        var db = Database.Open(DbPath, Backups);
        var fp = new string('B', 64);
        var first = db.Write((c, tx) => ContactsRepo.Upsert(c, tx, fp, "Bea", "PC-Bea", "192.168.1.20", 47821));
        var second = db.Write((c, tx) => ContactsRepo.Upsert(c, tx, fp, "Bea", "PC-Bea", "192.168.1.77", 47821));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("192.168.1.77", second.LastAddress);
        // Mismo nombre de PC y visible, otra huella → contacto distinto
        var other = db.Write((c, tx) => ContactsRepo.Upsert(c, tx, new string('C', 64), "Bea", "PC-Bea", "192.168.1.77", 47821));
        Assert.NotEqual(first.Id, other.Id);
    }
}
