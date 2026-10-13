using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using MiniChat.Core.Persistence;

namespace MiniChat.Tests;

/// <summary>
/// Pruebas entre PROCESOS distintos en una sola PC (dos MiniChat.Peer.exe con perfiles separados,
/// comunicándose por loopback con TLS y multicast reales). No sustituyen a la prueba entre dos PCs físicas.
/// </summary>
[Collection("engine")]
public class MultiProcessTests
{
    internal static string PeerExe
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            var cfg = dir.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            var root = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", ".."));
            var exe = Path.Combine(root, "tools", "MiniChat.Peer", "bin", cfg, "net10.0-windows", "win-x64", "MiniChat.Peer.exe");
            Assert.True(File.Exists(exe), "No se encontró " + exe);
            return exe;
        }
    }

    internal sealed class PeerProcess : IAsyncDisposable
    {
        private readonly Process _p;
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public string Dir { get; }
        public string Fingerprint { get; private set; } = "";

        private PeerProcess(Process p, string dir)
        {
            _p = p;
            Dir = dir;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
                {
                    try { _events.Writer.TryWrite(JsonDocument.Parse(line).RootElement.Clone()); } catch (JsonException) { }
                }
                _events.Writer.TryComplete();
            });
        }

        public static async Task<PeerProcess> StartAsync(string name, int discoveryPort, string dir, params string[] extra)
        {
            var psi = new ProcessStartInfo(PeerExe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "--data-dir", dir, "--name", name, "--port", TestNet.NextPort().ToString(), "--discovery-port", discoveryPort.ToString(), "--allow-loopback", "--machine", "PC-" + name, "--pin", TestPeer.Pin }.Concat(extra))
                psi.ArgumentList.Add(a);
            var pp = new PeerProcess(Process.Start(psi)!, dir);
            var ready = await pp.WaitAsync(e => e.GetProperty("ev").GetString() == "ready");
            pp.Fingerprint = ready.GetProperty("fp").GetString()!;
            Assert.Equal(JsonValueKind.Null, ready.GetProperty("bindError").ValueKind);
            return pp;
        }

        public async Task SendAsync(string command)
        {
            await _p.StandardInput.WriteLineAsync(command);
            await _p.StandardInput.FlushAsync();
        }

        public async Task<JsonElement> WaitAsync(Func<JsonElement, bool> match, int timeoutMs = 15000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var seen = new List<string>();
            try
            {
                while (true)
                {
                    var e = await _events.Reader.ReadAsync(cts.Token);
                    if (match(e)) return e;
                    seen.Add(e.GetRawText());
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Evento no recibido. Eventos vistos:\n" + string.Join("\n", seen.TakeLast(15)));
            }
        }

        public async Task<JsonElement> CommandAsync(string command, string op)
        {
            await SendAsync(command);
            return await WaitAsync(e => e.GetProperty("ev").GetString() == "result" && e.GetProperty("op").GetString() == op);
        }

        public void Kill() => _p.Kill(entireProcessTree: true);

        public async ValueTask DisposeAsync()
        {
            if (!_p.HasExited)
            {
                try { await SendAsync("quit"); await _p.WaitForExitAsync(new CancellationTokenSource(5000).Token); }
                catch { _p.Kill(true); }
            }
            _p.Dispose();
        }
    }

    private static string NewDir(string name) => Path.Combine(Path.GetTempPath(), "MiniChatTests", "proc-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);

    private static void Cleanup(params string[] dirs)
    {
        SqliteConnection.ClearAllPools();
        foreach (var d in dirs)
        {
            // Borra también la clave CNG del perfil de prueba.
            try
            {
                var db = Database.Open(Path.Combine(d, "minichat.db"), Path.Combine(d, "backups"));
                var id = db.Read(c => ProfileRepo.GetIdentity(c));
                if (id is not null) Core.Identity.LocalIdentity.Delete(id.Value.KeyName);
            }
            catch { }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(d, true); } catch { }
        }
    }

    [Fact]
    public async Task TwoProcesses_Discover_Pair_ExchangeTextAndSticker_KillAndRecover()
    {
        int disc = TestNet.NextPort();
        string dirA = NewDir("ana"), dirB = NewDir("bea");
        try
        {
            var ana = await PeerProcess.StartAsync("Ana", disc, dirA);
            var bea = await PeerProcess.StartAsync("Bea", disc, dirB, "--auto-accept", "--echo");
            try
            {
                // Descubrimiento entre procesos
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    await ana.SendAsync("peers");
                    var peers = await ana.WaitAsync(e => e.GetProperty("ev").GetString() == "peers");
                    if (peers.GetProperty("list").EnumerateArray().Any(p => p.GetProperty("Fingerprint").GetString() == bea.Fingerprint && p.GetProperty("Online").GetBoolean())) break;
                    Assert.True(sw.ElapsedMilliseconds < 10000, "Ana no descubrió a Bea");
                    await Task.Delay(200);
                }

                var req = await ana.CommandAsync($"request {bea.Fingerprint}", "request");
                Assert.True(req.GetProperty("r").GetProperty("Success").GetBoolean());
                await ana.WaitAsync(e => e.GetProperty("ev").GetString() == "contact" && e.GetProperty("relation").GetString() == "Authorized");

                // Texto en ambos sentidos (Bea responde con eco)
                var send = await ana.CommandAsync($"send {bea.Fingerprint[..12]} Hola desde otro proceso ñ 🚀", "send");
                Assert.True(send.GetProperty("Success").GetBoolean());
                await ana.WaitAsync(e => e.GetProperty("ev").GetString() == "message" && e.GetProperty("dir").GetString() == "Incoming"
                                         && e.GetProperty("text").GetString() == "eco: Hola desde otro proceso ñ 🚀");

                // Sticker en ambos sentidos
                var png = Path.Combine(dirA, "s.png");
                Directory.CreateDirectory(dirA);
                await File.WriteAllBytesAsync(png, StickerTests.MakePng(120, 60, transparent: true));
                var st = await ana.CommandAsync($"sticker {bea.Fingerprint[..12]} {png}", "sticker");
                Assert.True(st.GetProperty("Success").GetBoolean());
                var hash = st.GetProperty("hash").GetString();
                await ana.WaitAsync(e => e.GetProperty("ev").GetString() == "message" && e.GetProperty("dir").GetString() == "Incoming"
                                         && e.GetProperty("kind").GetString() == "Sticker" && e.GetProperty("sticker").GetString() == hash);

                // Cierre abrupto de Bea (sin "bye") y envío mientras está caída
                bea.Kill();
                var offline = await ana.CommandAsync($"send {bea.Fingerprint[..12]} mientras estabas caída", "send");
                var failed = await ana.WaitAsync(e => e.GetProperty("ev").GetString() == "message" && e.GetProperty("text").GetString() == "mientras estabas caída"
                                                      && e.GetProperty("status").GetString() == "Failed");
                Assert.False(string.IsNullOrEmpty(failed.GetProperty("error").GetString()));

                // Bea vuelve (mismo perfil): historial intacto, reintento manual entrega el mensaje
                await bea.DisposeAsync();
                bea = await PeerProcess.StartAsync("Bea", disc, dirB, "--auto-accept", "--echo");
                await Task.Delay(1500);
                var retry = await ana.CommandAsync($"retry {bea.Fingerprint[..12]}", "retry");
                await ana.WaitAsync(e => e.GetProperty("ev").GetString() == "message" && e.GetProperty("text").GetString() == "mientras estabas caída"
                                         && e.GetProperty("status").GetString() == "Delivered");
                var history = await bea.CommandAsync($"history {ana.Fingerprint[..12]}", "history");
                var texts = history.GetProperty("r").EnumerateArray().Where(m => m.GetProperty("dir").GetString() == "Incoming")
                    .Select(m => m.GetProperty("Text").GetString() ?? "[sticker]").ToList();
                Assert.Equal(new[] { "Hola desde otro proceso ñ 🚀", "[sticker]", "mientras estabas caída" }, texts);
            }
            finally
            {
                await ana.DisposeAsync();
                await bea.DisposeAsync();
            }
        }
        finally
        {
            Cleanup(dirA, dirB);
        }
    }

    [Fact]
    public async Task AbruptKill_DuringWrites_LeavesConsistentDatabase()
    {
        string dir = NewDir("crash");
        try
        {
            for (int round = 0; round < 3; round++)
            {
                var psi = new ProcessStartInfo(PeerExe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("crash-writer");
                psi.ArgumentList.Add("--data-dir");
                psi.ArgumentList.Add(dir);
                using var p = Process.Start(psi)!;
                Assert.Equal("writing", await p.StandardOutput.ReadLineAsync());
                await Task.Delay(400 + round * 300);
                p.Kill(); // equivalente a un cierre inesperado: sin limpieza
                await p.WaitForExitAsync();

                SqliteConnection.ClearAllPools();
                var db = Database.Open(Path.Combine(dir, "minichat.db"), Path.Combine(dir, "backups")); // quick_check incluido
                long count = db.Read(c => (long)c.Scalar("SELECT COUNT(*) FROM messages;")!);
                Assert.True(count > 0);
                Assert.Equal(0, count % 3); // ninguna transacción quedó a medias
                Assert.Equal("ok", db.Read(c => (string)c.Scalar("PRAGMA integrity_check;")!));
                SqliteConnection.ClearAllPools();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
