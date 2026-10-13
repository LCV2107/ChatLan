using System.Globalization;
using System.Text;

namespace MiniChat.Core.Files;

/// <summary>Nombres de documentos: validación en el protocolo, saneado al guardar y tipos de riesgo.</summary>
public static class FileNames
{
    public const int MaxNameLength = 150;

    /// <summary>
    /// Extensiones que pueden ejecutar código o cambiar la configuración del equipo al abrirse.
    /// Se permiten (decisión de producto), pero MiniChat advierte antes de abrirlas y nunca las abre solo.
    /// </summary>
    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller",
        ".lnk", ".url", ".reg", ".hta", ".cpl", ".jar", ".dll", ".sys", ".drv", ".ocx", ".inf", ".scf", ".chm",
        ".iso", ".img", ".vhd", ".vhdx", ".application", ".gadget", ".settingcontent-ms", ".library-ms",
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".sldm", ".mht", ".mhtml", ".svg",
        ".html", ".htm", ".xhtml", ".py", ".pyw", ".rb", ".pl", ".sh",
    };

    /// <summary>Caracteres de control bidireccional usados para disfrazar extensiones (p. ej. «factura‮fdp.exe»).</summary>
    private static bool IsBidiControl(char c) =>
        c is '‎' or '‏' or '؜' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    /// <summary>Validación estricta del nombre que llega por la red (sin rutas ni caracteres de control).</summary>
    public static bool IsAcceptableWireName(string? name) =>
        name is not null && name.Length is > 0 and <= 255 && name.Trim().Length > 0
        && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':')
        && name is not "." and not "..";

    /// <summary>Nombre seguro para guardar en Windows y para mostrar sin engaños.</summary>
    public static string Sanitize(string? name)
    {
        var sb = new StringBuilder();
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in (name ?? "").Normalize(NormalizationForm.FormC))
        {
            if (IsBidiControl(c) || char.IsControl(c)) continue;
            sb.Append(invalid.Contains(c) || c is ':' ? '_' : c);
        }
        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "archivo";

        var ext = Path.GetExtension(clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        if (ext.Length > 20) { stem = clean; ext = ""; }
        if (IsReserved(stem)) stem = "_" + stem;
        if (stem.Length + ext.Length > MaxNameLength) stem = stem[..Math.Max(1, MaxNameLength - ext.Length)];
        return stem + ext;
    }

    private static bool IsReserved(string stem)
    {
        var s = stem.Split('.')[0].ToUpperInvariant();
        return s is "CON" or "PRN" or "AUX" or "NUL"
            || (s.Length == 4 && (s.StartsWith("COM") || s.StartsWith("LPT")) && char.IsAsciiDigit(s[3]));
    }

    public static bool IsDangerous(string name) => Dangerous.Contains(Path.GetExtension(name));

    public static string FormatSize(long bytes)
    {
        var ci = CultureInfo.GetCultureInfo("es-ES");
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", ci) + " KB",
            < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", ci) + " MB",
            _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", ci) + " GB",
        };
    }
}
