// MiniChat.Peer — par de MiniChat LAN sin interfaz gráfica.
// Usa exactamente el mismo motor que la aplicación (MiniChat.Core). Sirve para:
//  - pruebas automatizadas entre procesos distintos en una sola PC (con --allow-loopback),
//  - diagnóstico en una red real (sin --allow-loopback aplica la política de red normal).
//
// Uso:
//   MiniChat.Peer --data-dir <carpeta> --name <nombre> [--port N] [--discovery-port N]
//                 --pin <PIN> [--allow-loopback] [--auto-accept] [--echo]
//   MiniChat.Peer crash-writer --data-dir <carpeta>      (escribe sin parar; para pruebas de cierre abrupto)
//   MiniChat.Peer import-stickers --data-dir <carpeta> <archivos...>   (importa a la biblioteca sin abrir la red)
//
// Órdenes por la entrada estándar (una por línea):
//   whoami | peers | request <huella> | accept <huella> | reject <huella> | block <huella> | unblock <huella>
//   connect <ip> [puerto] | send <huella> <texto> | sticker <huella> <archivo> | retry <huella>
//   history <huella> | networks | trust <clave-red> | file <huella> <archivo> | accept-file <fid> | quit
// Cada evento se emite como una línea JSON en la salida estándar.
using System.Text.Json;
using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;
using MiniChat.Core.Network;
using MiniChat.Core.Persistence;

// La entrada/salida es UTF-8 aunque la consola use otra página de códigos (OEM).
Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0 && args[0] == "crash-writer") return CrashWriter(args);
if (args.Length > 0 && args[0] == "import-stickers") return await ImportStickers(args);

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);

var dataDir = Arg("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniChatLAN", "profiles", "peer");
var options = new MiniChatOptions
{
    AllowLoopback = Flag("--allow-loopback"),
    TcpPort = int.TryParse(Arg("--port"), out var p) ? p : new MiniChatOptions().TcpPort,
    DiscoveryPort = int.TryParse(Arg("--discovery-port"), out var d) ? d : new MiniChatOptions().DiscoveryPort,
};
if (options.AllowLoopback)
    options = options with { AnnounceInterval = TimeSpan.FromMilliseconds(500), PresenceTimeout = TimeSpan.FromSeconds(4), PinIterations = 1000 };

var output = Console.Out;
var outLock = new object();
void Emit(object e)
{
    lock (outLock) { output.WriteLine(JsonSerializer.Serialize(e)); output.Flush(); }
}

INetworkInventory inventory = options.AllowLoopback ? new LoopbackOnlyInventory() : new WindowsNetworkInventory();
ChatEngine engine;
try
{
    engine = ChatEngine.Open(new AppPaths(dataDir), options, inventory, new RollingFileLog(Path.Combine(dataDir, "logs")), Arg("--machine"));
}
catch (DatabaseUnavailableException ex)
{
    Emit(new { ev = "fatal", message = ex.Message });
    return 2;
}
if (Arg("--name") is { } name) await engine.SetDisplayNameAsync(name);
// PIN del historial cifrado: --pin o variable MINICHAT_PIN (solo para pruebas y diagnóstico).
var pin = Arg("--pin") ?? Environment.GetEnvironmentVariable("MINICHAT_PIN");
if (string.IsNullOrEmpty(pin)) { Emit(new { ev = "fatal", message = "Indique el PIN con --pin o MINICHAT_PIN." }); return 2; }
var pinResult = await (engine.NeedsPinSetup ? engine.SetupPinAsync(pin) : engine.UnlockAsync(pin));
if (!pinResult.Success) { Emit(new { ev = "fatal", message = pinResult.Message }); return 2; }
if (!engine.HasDisplayName) await engine.SetDisplayNameAsync(Environment.UserName);
bool autoAccept = Flag("--auto-accept"), echo = Flag("--echo");

engine.Notice += async (_, n) =>
{
    Emit(new { ev = "notice", kind = n.Kind.ToString(), contact = n.ContactId, from = n.DisplayName });
    if (n.Kind == NoticeKind.IncomingRequest && autoAccept) Emit(new { ev = "accept", result = (await engine.AcceptAsync(n.ContactId)).Message });
};
engine.MessageChanged += async (_, m) =>
{
    var msg = m.Message;
    Emit(new { ev = "message", contact = m.ContactId, id = msg.Id, dir = msg.Direction.ToString(), kind = msg.Kind.ToString(), text = msg.Text, sticker = msg.StickerHash, status = msg.Status.ToString(), error = msg.Error });
    if (echo && m.IsNew && msg.Direction == MessageDirection.Incoming)
    {
        if (msg.Kind == MessageKind.Text) await engine.SendTextAsync(m.ContactId, "eco: " + msg.Text);
        else await engine.SendStickerAsync(m.ContactId, msg.StickerHash!);
    }
};
engine.FileChanged += (_, f) => Emit(new { ev = "file", fid = f.File.FileId, state = f.File.State.ToString(), transferred = f.File.Transferred, size = f.File.Size, error = f.File.Error });
engine.ContactChanged += async (_, id) =>
{
    var c = await engine.GetContactAsync(id);
    if (c is not null) Emit(new { ev = "contact", id, fp = c.Fingerprint, name = c.DisplayName, relation = c.Relation.ToString(), blocked = c.Blocked });
};

engine.StartNetworking();
Emit(new
{
    ev = "ready",
    fp = engine.Identity.Fingerprint,
    code = engine.Identity.DisplayCode,
    name = engine.DisplayName,
    machine = engine.MachineName,
    port = options.TcpPort,
    listening = engine.Server!.ListeningAddresses.Select(a => a.ToString()).ToArray(),
    bindError = engine.Server.LastBindError,
    networks = engine.Policy.Decisions.Select(x => new { x.Interface.Name, subnet = x.Interface.SubnetText, x.Allowed, x.Reason, key = x.Interface.TrustKey }).ToArray(),
});

async Task<long?> ContactOf(string fpPrefix)
{
    var people = await engine.GetPeopleAsync();
    return people.FirstOrDefault(x => x.Fingerprint.StartsWith(fpPrefix, StringComparison.OrdinalIgnoreCase))?.ContactId;
}

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;
    try
    {
        switch (parts[0])
        {
            case "quit":
                await engine.DisposeAsync();
                Emit(new { ev = "bye" });
                return 0;
            case "whoami":
                Emit(new { ev = "whoami", fp = engine.Identity.Fingerprint, name = engine.DisplayName });
                break;
            case "peers":
                Emit(new { ev = "peers", list = (await engine.GetPeopleAsync()).Select(x => new { x.Fingerprint, x.DisplayName, x.MachineName, x.Online, relation = x.Relation.ToString(), x.ContactId, endpoint = x.Endpoint?.ToString() }) });
                break;
            case "networks":
                engine.RefreshNetwork();
                Emit(new { ev = "networks", list = engine.Policy.Decisions.Select(x => new { x.Interface.Name, subnet = x.Interface.SubnetText, category = x.Interface.Category.ToString(), x.Allowed, x.Reason, key = x.Interface.TrustKey }) });
                break;
            case "trust":
                engine.SetNetworkTrust(parts[1] + (parts.Length > 2 ? " " + parts[2] : ""), true);
                Emit(new { ev = "trusted" });
                break;
            case "request":
                Emit(new { ev = "result", op = "request", r = await engine.SendChatRequestAsync(Full(parts[1], await engine.GetPeopleAsync())) });
                break;
            case "connect":
                var cr = await engine.ConnectManualAsync(parts[1], parts.Length > 2 ? int.Parse(parts[2]) : null);
                Emit(new { ev = "result", op = "connect", cr.Success, cr.Message, fp = cr.Value?.Fingerprint });
                break;
            case "accept" or "reject" or "block" or "unblock" or "retry" or "history":
                var id = await ContactOf(parts[1]);
                if (id is null) { Emit(new { ev = "error", message = "contacto desconocido" }); break; }
                object r = parts[0] switch
                {
                    "accept" => await engine.AcceptAsync(id.Value),
                    "reject" => await engine.RejectAsync(id.Value),
                    "block" => await engine.BlockAsync(id.Value),
                    "unblock" => await engine.UnblockAsync(id.Value),
                    "retry" => await engine.RetryAsync(id.Value),
                    _ => (await engine.GetMessagesAsync(id.Value, null, 500)).Select(m => new { m.Id, dir = m.Direction.ToString(), m.Text, m.StickerHash, status = m.Status.ToString() }),
                };
                Emit(new { ev = "result", op = parts[0], r });
                break;
            case "send":
                var sid = await ContactOf(parts[1]);
                var sr = sid is null ? null : await engine.SendTextAsync(sid.Value, parts.Length > 2 ? parts[2] : "");
                Emit(new { ev = "result", op = "send", sr?.Success, sr?.Message, id = sr?.Value?.Id });
                break;
            case "file":
                var fcid = await ContactOf(parts[1]);
                var fr = fcid is null ? null : await engine.SendFileAsync(fcid.Value, parts[2]);
                Emit(new { ev = "result", op = "file", fr?.Success, fr?.Message, fid = fr?.Value?.Uid });
                break;
            case "accept-file":
                var ar = await engine.AcceptFileAsync(parts[1]);
                Emit(new { ev = "result", op = "accept-file", ar.Success, ar.Message });
                break;
            case "sticker":
                var kid = await ContactOf(parts[1]);
                var imp = await engine.Stickers.ImportAsync(new[] { parts[2] });
                var kr = kid is null || imp[0].Hash is null ? null : await engine.SendStickerAsync(kid.Value, imp[0].Hash!);
                Emit(new { ev = "result", op = "sticker", import = imp[0].Message, kr?.Success, kr?.Message, hash = imp[0].Hash });
                break;
            default:
                Emit(new { ev = "error", message = "orden desconocida" });
                break;
        }
    }
    catch (Exception ex)
    {
        Emit(new { ev = "error", message = ex.Message });
    }
}
await engine.DisposeAsync();
return 0;

// Importa stickers a la biblioteca de un perfil sin abrir la red ni modificar mensajes
// (se puede usar con la aplicación abierta: SQLite en modo WAL admite varios procesos).
static async Task<int> ImportStickers(string[] args)
{
    var dir = args[Array.IndexOf(args, "--data-dir") + 1];
    var files = args.Skip(1).Where(a => a != "--data-dir" && a != dir).ToArray();
    var paths = new AppPaths(dir);
    var db = Database.Open(paths.DatabaseFile, paths.BackupDirectory);
    var vault = new MiniChat.Core.Security.Vault(db);
    if (!vault.Exists) { Console.WriteLine("El perfil no tiene PIN: abra MiniChat una vez antes de importar."); return 2; }
    // Importar solo cifra (clave pública): no hace falta el PIN.
    var service = new MiniChat.Core.Stickers.StickerService(db, paths, new MiniChatOptions(), NullLog.Instance, vault);
    foreach (var r in await service.ImportAsync(files))
        Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(r.SourcePath), r.Success, r.Message, r.AlreadyInLibrary }));
    return 0;
}

static string Full(string prefix, IReadOnlyList<PersonView> people) =>
    people.FirstOrDefault(p => p.Fingerprint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Fingerprint ?? prefix;

static int CrashWriter(string[] args)
{
    var dir = args[Array.IndexOf(args, "--data-dir") + 1];
    var paths = new AppPaths(dir);
    paths.EnsureCreated();
    var db = Database.Open(paths.DatabaseFile, paths.BackupDirectory);
    var contact = db.Write((c, tx) => ContactsRepo.Upsert(c, tx, new string('E', 64), "Eco", "PC-Eco", null, null));
    var conv = db.Write((c, tx) => MessagesRepo.GetOrCreateConversation(c, tx, contact.Id));
    Console.WriteLine("writing");
    Console.Out.Flush();
    for (long i = 0; ; i++)
    {
        // Cada transacción inserta 3 mensajes; tras un cierre abrupto debe haber un múltiplo de 3.
        db.Write((c, tx) =>
        {
            for (int k = 0; k < 3; k++) MessagesRepo.InsertOutgoing(c, tx, conv.Id, MessageKind.Text, $"lote {i} #{k}", null);
        });
    }
}

/// <summary>Solo loopback: pruebas entre procesos en una PC sin depender de la red real.</summary>
sealed class LoopbackOnlyInventory : INetworkInventory
{
    public IReadOnlyList<LocalInterface> GetInterfaces() => new[]
    {
        new LocalInterface("lo", "Loopback", "", System.Net.IPAddress.Loopback, 8, System.Net.NetworkInformation.NetworkInterfaceType.Loopback,
            NetworkCategory.Private, "lo", "", false),
    };
}
