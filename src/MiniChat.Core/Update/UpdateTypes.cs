namespace MiniChat.Core.Update;

/// <summary>Canal de actualización. Estable = releases normales; Experimental = pre-releases (pruebas).</summary>
public enum UpdateChannel { Stable, Experimental }

public static class UpdateChannels
{
    public static string Id(this UpdateChannel c) => c == UpdateChannel.Experimental ? "experimental" : "stable";
    public static UpdateChannel Parse(string? s) => string.Equals(s, "experimental", StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Experimental : UpdateChannel.Stable;
    public static string DisplayName(this UpdateChannel c) => c == UpdateChannel.Experimental ? "Experimental (pruebas)" : "Estable";
}

/// <summary>Contenido del manifiesto firmado (<c>stable.json</c> / <c>experimental.json</c>).</summary>
public sealed record UpdateManifest(int Schema, string Version, string Channel, string Url, string Sha256, long Size, string? Notes);

/// <summary>Una actualización disponible, ya verificada (firma y campos).</summary>
public sealed record UpdateInfo(string Version, string Notes, string Url, string Sha256, long Size, UpdateChannel Channel);

public enum UpdateStatus
{
    /// <summary>La compilación no tiene repositorio ni clave pública configurados.</summary>
    Disabled,
    UpToDate,
    Available,
    /// <summary>No se pudo contactar con GitHub (sin Internet o sin acceso). No es un error de la app.</summary>
    NoNetwork,
    /// <summary>El manifiesto no superó la verificación de firma o tiene datos inválidos.</summary>
    Invalid,
    Failed,
}

public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Update, string Message)
{
    public bool HasUpdate => Status == UpdateStatus.Available && Update is not null;
}

/// <summary>Configuración de las actualizaciones. Editar una sola vez para activar la función.</summary>
public static class UpdateConfig
{
    /// <summary>Repositorio público «dueño/repo». Cambiar por el real (p. ej. «luiscv/minichat-lan»).</summary>
    public const string Repository = "OWNER/REPO";

    /// <summary>Rama donde viven los manifiestos de la carpeta <c>updates/</c>.</summary>
    public const string Branch = "main";

    /// <summary>
    /// Clave pública ECDSA P-256 (SubjectPublicKeyInfo, Base64) que firma los manifiestos.
    /// Se obtiene una vez con:  pwsh tools\firmar-release.ps1 -Init
    /// Sin esta clave, las actualizaciones quedan desactivadas (no se descarga ni ejecuta nada).
    /// </summary>
    public const string TrustedPublicKeyBase64 = "";

    public static string ManifestBaseUrl => $"https://raw.githubusercontent.com/{Repository}/{Branch}/updates/";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TrustedPublicKeyBase64) && !Repository.Contains("OWNER/REPO", StringComparison.Ordinal);
}
