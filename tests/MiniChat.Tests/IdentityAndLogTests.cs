using MiniChat.Core.Diagnostics;
using MiniChat.Core.Identity;

namespace MiniChat.Tests;

public class IdentityAndLogTests
{
    [Fact]
    public void Identity_IsPersistentInCng_AndKeyIsNotExportable()
    {
        var name = "MiniChatLAN-test-" + Guid.NewGuid().ToString("N");
        try
        {
            string fp;
            using (var first = LocalIdentity.OpenOrCreate(name))
            {
                Assert.True(first.WasCreated);
                fp = first.Fingerprint;
                Assert.True(IdentityCodes.IsValidFingerprint(fp));
            }
            using (var again = LocalIdentity.OpenOrCreate(name))
            {
                Assert.False(again.WasCreated);
                Assert.Equal(fp, again.Fingerprint); // misma identidad tras "reiniciar"
            }
            using var key = System.Security.Cryptography.CngKey.Open(name, System.Security.Cryptography.CngProvider.MicrosoftSoftwareKeyStorageProvider);
            Assert.Equal(System.Security.Cryptography.CngExportPolicies.None, key.ExportPolicy);
        }
        finally
        {
            LocalIdentity.Delete(name);
        }
    }

    [Fact]
    public void PairingCode_IsSymmetric_AndDependsOnBothKeys()
    {
        string a = new('A', 64), b = new('B', 64), c = new('C', 64);
        Assert.Equal(IdentityCodes.PairingCode(a, b), IdentityCodes.PairingCode(b, a));
        Assert.NotEqual(IdentityCodes.PairingCode(a, b), IdentityCodes.PairingCode(a, c));
        Assert.Matches(@"^\d{4}( \d{4}){5}$", IdentityCodes.PairingCode(a, b));
        Assert.Equal("AAAA-AAAA-AAAA-AAAA-AAAA", IdentityCodes.DisplayCode(a));
    }

    [Fact]
    public void RollingLog_RotatesAndKeepsAtMostNFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "log-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var log = new RollingFileLog(dir, maxBytes: 2000, maxFiles: 3))
            {
                for (int i = 0; i < 400; i++) log.Info("test", "línea de diagnóstico número " + i);
            }
            var files = Directory.GetFiles(dir, "*.log");
            Assert.InRange(files.Length, 2, 3);
            Assert.All(files, f => Assert.True(new FileInfo(f).Length < 4000));
            Assert.Contains("número 399", File.ReadAllText(Path.Combine(dir, "minichat.log")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class DataFolderPermissionTests
{
    [Fact]
    public void DataFolder_IsRestrictedToCurrentUserAndSystem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "acl-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new MiniChat.Core.Config.AppPaths(dir);
        paths.EnsureCreated();
        try
        {
            Assert.Null(paths.RestrictToCurrentUser());
            var rules = new DirectoryInfo(dir).GetAccessControl()
                .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            Assert.All(rules, r => Assert.False(r.IsInherited));
            Assert.Contains(rules, r => r.IdentityReference.Equals(me));
            Assert.DoesNotContain(rules, r => r.IdentityReference.Value is "S-1-5-32-545" or "S-1-1-0" or "S-1-5-11"); // Usuarios, Todos, Usuarios autenticados
            Assert.Equal(2, rules.Select(r => r.IdentityReference.Value).Distinct().Count());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
