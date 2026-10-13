using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiniChat.Core.Diagnostics;

namespace MiniChat.Core.Update;

[JsonSerializable(typeof(UpdateManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>
/// Comprobación y descarga de actualizaciones desde GitHub (repo público). NO pasa por la red del chat
/// (<c>NetworkPolicy</c>/<c>PeerClient</c>): es un cliente HTTPS aparte que solo habla con GitHub y es
/// opcional —si no hay Internet, falla en silencio y la app sigue funcionando—.
///
/// Confianza: se descarga un manifiesto firmado (<c>&lt;canal&gt;.json</c> + <c>.sig</c>) y se verifica su firma
/// ECDSA P-256 con la clave pública incrustada antes de mirar nada. Así, aunque se comprometa el repositorio,
/// nadie puede ofrecer una actualización sin la clave privada de firma (que nunca está en GitHub ni en la app).
/// El instalador descargado se verifica además por SHA-256 y tamaño antes de ejecutarse.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly byte[]? _publicKey;
    private readonly ILog _log;

    public const int MaxManifestBytes = 8 * 1024;
    public const long MaxInstallerBytes = 400L * 1024 * 1024;

    public UpdateService(string baseUrl, string trustedPublicKeyBase64, ILog? log = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        _log = log ?? NullLog.Instance;
        _publicKey = TryDecodeKey(trustedPublicKeyBase64);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MiniChatLAN-Updater");
        _http.MaxResponseContentBufferSize = MaxInstallerBytes;
    }

    public bool IsConfigured => _publicKey is not null && Uri.TryCreate(_baseUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static byte[]? TryDecodeKey(string b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(b64.Trim());
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(bytes, out _);   // valida que es una clave P-256 utilizable
            return bytes;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Comprueba si hay una versión más nueva que <paramref name="currentVersion"/> en el canal dado.</summary>
    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, string currentVersion, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new(UpdateStatus.Disabled, null, "Las actualizaciones no están configuradas en esta compilación.");

        byte[] manifestBytes, signature;
        try
        {
            manifestBytes = await GetBytesAsync(channel.Id() + ".json", MaxManifestBytes, ct).ConfigureAwait(false);
            signature = await GetBytesAsync(channel.Id() + ".json.sig", 1024, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.Debug("update", $"No se pudo consultar actualizaciones: {ex.GetType().Name}");
            return new(UpdateStatus.NoNetwork, null, "No se pudo contactar con GitHub (¿sin Internet?). La app funciona igual.");
        }

        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signature).Trim()); }
        catch (FormatException) { return new(UpdateStatus.Invalid, null, "La firma de la actualización está dañada."); }

        if (!VerifySignature(manifestBytes, signatureBytes))
        {
            _log.Warn("update", "La firma del manifiesto de actualización no es válida; se ignora.");
            return new(UpdateStatus.Invalid, null, "La actualización no superó la verificación de firma y se ignoró por seguridad.");
        }

        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize(manifestBytes, UpdateJson.Default.UpdateManifest); }
        catch (JsonException) { return new(UpdateStatus.Invalid, null, "El manifiesto de actualización está dañado."); }

        if (m is null || m.Schema != 1 || string.IsNullOrWhiteSpace(m.Version) || string.IsNullOrWhiteSpace(m.Sha256))
            return new(UpdateStatus.Invalid, null, "El manifiesto de actualización no es válido.");
        if (!AppVersionNumber.TryParse(m.Version, out _))
            return new(UpdateStatus.Invalid, null, $"La versión del manifiesto no es válida: {m.Version}.");
        if (!string.Equals(m.Channel, channel.Id(), StringComparison.OrdinalIgnoreCase))
            return new(UpdateStatus.Invalid, null, "El manifiesto no corresponde al canal solicitado.");
        if (!IsAllowedUrl(m.Url))
            return new(UpdateStatus.Invalid, null, "La dirección de descarga no es de GitHub; se rechazó por seguridad.");
        if (!IsHex64(m.Sha256) || m.Size is <= 0 or > MaxInstallerBytes)
            return new(UpdateStatus.Invalid, null, "El manifiesto declara un instalador no válido.");

        if (!AppVersionNumber.IsNewer(m.Version, currentVersion))
            return new(UpdateStatus.UpToDate, null, $"Ya tienes la versión más reciente del canal {channel.DisplayName()} ({currentVersion}).");

        var info = new UpdateInfo(m.Version, (m.Notes ?? "").Trim(), m.Url, m.Sha256.ToUpperInvariant(), m.Size, channel);
        return new(UpdateStatus.Available, info, $"Hay una versión nueva: {m.Version}.");
    }

    /// <summary>
    /// Descarga el instalador a <paramref name="destinationDir"/> y verifica su SHA-256 y tamaño. Devuelve la
    /// ruta del archivo verificado, listo para ejecutar. Lanza si la verificación falla (nada se deja «a medias»).
    /// </summary>
    public async Task<string> DownloadInstallerAsync(UpdateInfo info, string destinationDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var finalPath = Path.Combine(destinationDir, $"MiniChatLAN-{Sanitize(info.Version)}-x64.msi");
        var partPath = finalPath + ".part";

        using (var resp = await _http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            if (total is > MaxInstallerBytes) throw new InvalidOperationException("El instalador supera el tamaño máximo permitido.");

            await using var http = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sha = SHA256.Create();
            var buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await http.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > info.Size + 1024) throw new InvalidOperationException("El instalador es más grande de lo declarado.");
                sha.TransformBlock(buffer, 0, n, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                if (total is > 0) progress?.Report(Math.Min(1.0, (double)read / total.Value));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            if (read != info.Size)
                throw new InvalidOperationException($"El instalador no tiene el tamaño esperado ({read} ≠ {info.Size}).");
            var hash = Convert.ToHexString(sha.Hash!);
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La huella SHA-256 del instalador no coincide: se descartó por seguridad.");
        }

        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partPath, finalPath);
        _log.Info("update", $"Instalador {info.Version} descargado y verificado.");
        return finalPath;
    }

    private async Task<byte[]> GetBytesAsync(string file, int maxBytes, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_baseUrl + file, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is > 0 and var len && len > maxBytes)
            throw new IOException($"{file} es demasiado grande ({len}).");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > maxBytes) throw new IOException($"{file} es demasiado grande.");
        return bytes;
    }

    private bool VerifySignature(byte[] manifest, byte[] signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(_publicKey, out _);
            return ec.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static bool IsAllowedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex64(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);
    private static string Sanitize(string v) => new(v.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());

    public void Dispose() => _http.Dispose();
}
