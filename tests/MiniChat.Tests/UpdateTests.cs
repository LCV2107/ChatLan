using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniChat.Core.Update;

namespace MiniChat.Tests;

public class UpdateTests
{
    // ---------------- Comparación de versiones ----------------

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.0-beta.1", true)]   // final > pre-release
    [InlineData("1.0.0-beta.1", "1.0.0", false)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", true)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", true)]  // numérico, no alfabético
    [InlineData("1.0.0.5", "1.0.0", true)]        // cuarto campo
    public void VersionComparison(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, AppVersionNumber.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.2.3.4")]
    public void VersionParse_Accepts(string v) => Assert.True(AppVersionNumber.TryParse(v, out _));

    [Theory]
    [InlineData("")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0.0.0")]
    public void VersionParse_Rejects(string v) => Assert.False(AppVersionNumber.TryParse(v, out _));

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/a.msi", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://raw.githubusercontent.com/o/r/main/a.msi", true)]
    [InlineData("http://github.com/o/r/a.msi", false)]        // sin TLS
    [InlineData("https://evil.com/github.com/a.msi", false)]
    [InlineData("https://github.com.attacker.net/a.msi", false)]
    public void InstallerUrl_OnlyGitHubHttps(string url, bool ok) => Assert.Equal(ok, UpdateService.IsAllowedUrl(url));

    // ---------------- Servicio con firma real y HTTP simulado ----------------

    private const string Base = "https://raw.githubusercontent.com/test/repo/main/updates/";
    private const string MsiUrl = "https://github.com/test/repo/releases/download/v1.0.1/MiniChatLAN-1.0.1-x64.msi";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class MapHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Map { get; } = new();
        public bool FailNetwork { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailNetwork) throw new HttpRequestException("sin red");
            var url = request.RequestUri!.AbsoluteUri;
            if (Map.TryGetValue(url, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (MapHandler handler, string pubKey, ECDsa key) NewHandler()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (new MapHandler(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static byte[] SignedManifest(ECDsa key, UpdateManifest m, out byte[] signature)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m, JsonOpts);
        signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return bytes;
    }

    private static void PutManifest(MapHandler h, ECDsa key, UpdateManifest m, string channel = "stable")
    {
        var bytes = SignedManifest(key, m, out var sig);
        h.Map[Base + channel + ".json"] = bytes;
        h.Map[Base + channel + ".json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(sig));
    }

    private static UpdateManifest Manifest(string version = "1.0.1", string sha = null!, long size = 2048, string url = MsiUrl, string channel = "stable") =>
        new(1, version, channel, url, sha ?? new string('a', 64), size, "Correcciones.");

    [Fact]
    public async Task NotConfigured_IsDisabled()
    {
        using var svc = new UpdateService(Base, "", handler: new MapHandler());
        Assert.False(svc.IsConfigured);
        var r = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.Disabled, r.Status);
    }

    [Fact]
    public async Task Check_FindsNewerVersion_WhenSignatureValid()
    {
        var (h, pub, key) = NewHandler();
        using (key)
        {
            PutManifest(h, key, Manifest("1.0.1"));
            using var svc = new UpdateService(Base, pub, handler: h);
            var r = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.Available, r.Status);
            Assert.Equal("1.0.1", r.Update!.Version);

            var same = await svc.CheckAsync(UpdateChannel.Stable, "1.0.1", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.UpToDate, same.Status);

            var older = await svc.CheckAsync(UpdateChannel.Stable, "2.0.0", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.UpToDate, older.Status);
        }
    }

    [Fact]
    public async Task Check_RejectsTamperedManifest()
    {
        var (h, pub, key) = NewHandler();
        using (key)
        {
            PutManifest(h, key, Manifest("1.0.1"));
            // Manipular un byte del JSON tras firmarlo: la firma deja de ser válida.
            var json = h.Map[Base + "stable.json"];
            json[^3] ^= 0xFF;
            using var svc = new UpdateService(Base, pub, handler: h);
            var r = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.Invalid, r.Status);
        }
    }

    [Fact]
    public async Task Check_RejectsManifestSignedByAnotherKey()
    {
        var (h, _, key) = NewHandler();
        using (key)
        {
            PutManifest(h, key, Manifest("1.0.1"));
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var svc = new UpdateService(Base, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), handler: h);
            var r = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.Invalid, r.Status);
        }
    }

    [Fact]
    public async Task Check_RejectsNonGitHubInstallerUrl_EvenIfWellSigned()
    {
        var (h, pub, key) = NewHandler();
        using (key)
        {
            PutManifest(h, key, Manifest("1.0.1", url: "https://evil.example.com/app.msi"));
            using var svc = new UpdateService(Base, pub, handler: h);
            var r = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.Invalid, r.Status);
        }
    }

    [Fact]
    public async Task Check_NoNetwork_IsReportedGracefully()
    {
        var (h, pub, key) = NewHandler();
        using (key)
        {
            h.FailNetwork = true;
            using var svc = new UpdateService(Base, pub, handler: h);
            var r = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", TestContext.Current.CancellationToken);
            Assert.Equal(UpdateStatus.NoNetwork, r.Status);
        }
    }

    [Fact]
    public async Task Download_VerifiesSha256_AndRejectsCorruptOrWrongSize()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, pub, key) = NewHandler();
        var dir = Path.Combine(Path.GetTempPath(), "MiniChatTests", "upd-" + Guid.NewGuid().ToString("N")[..8]);
        using (key)
        {
            var payload = RandomNumberGenerator.GetBytes(4096);
            var sha = Convert.ToHexString(SHA256.HashData(payload));
            h.Map[MsiUrl] = payload;
            PutManifest(h, key, Manifest("1.0.1", sha: sha, size: payload.Length));
            using var svc = new UpdateService(Base, pub, handler: h);

            var check = await svc.CheckAsync(UpdateChannel.Stable, "1.0.0", ct);
            var info = check.Update!;
            var path = await svc.DownloadInstallerAsync(info, dir, null, ct);
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path, ct));

            // SHA que no cuadra -> se rechaza y no queda archivo final.
            var badSha = info with { Sha256 = new string('B', 64) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DownloadInstallerAsync(badSha, dir, null, ct));

            // Tamaño declarado distinto -> se rechaza.
            var badSize = info with { Size = 10 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DownloadInstallerAsync(badSize, dir, null, ct));
        }
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void ChannelIds_RoundTrip()
    {
        Assert.Equal("experimental", UpdateChannel.Experimental.Id());
        Assert.Equal(UpdateChannel.Experimental, UpdateChannels.Parse("experimental"));
        Assert.Equal(UpdateChannel.Stable, UpdateChannels.Parse(null));
    }
}
