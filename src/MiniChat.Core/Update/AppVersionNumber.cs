using System.Globalization;

namespace MiniChat.Core.Update;

/// <summary>
/// Versión estilo SemVer suficiente para MiniChat: «1.2.3» y «1.2.3-beta.4». Compara el núcleo numérico y,
/// a igualdad, trata una pre-release como MENOR que la versión final (1.2.3-beta &lt; 1.2.3), como SemVer.
/// El metadato de compilación (lo que sigue a «+») se ignora.
/// </summary>
public sealed class AppVersionNumber : IComparable<AppVersionNumber>, IEquatable<AppVersionNumber>
{
    public IReadOnlyList<int> Core { get; }
    public IReadOnlyList<string> PreRelease { get; }
    public string Text { get; }

    private AppVersionNumber(IReadOnlyList<int> core, IReadOnlyList<string> pre, string text)
    {
        Core = core;
        PreRelease = pre;
        Text = text;
    }

    public bool IsPreRelease => PreRelease.Count > 0;

    public static bool TryParse(string? text, out AppVersionNumber? version)
    {
        version = null;
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 64) return false;
        var noMeta = text.Split('+', 2)[0];
        var parts = noMeta.Split('-', 2);
        var coreText = parts[0];
        var pre = parts.Length == 2 ? parts[1] : "";
        if (parts.Length == 2 && pre.Length == 0) return false;   // «1.0.0-» sin etiqueta no es válido

        var segments = coreText.Split('.');
        if (segments.Length is 0 or > 4) return false;
        var core = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0) return false;
            core[i] = n;
        }
        var preParts = pre.Length == 0 ? Array.Empty<string>() : pre.Split('.');
        if (preParts.Any(string.IsNullOrEmpty)) return false;
        version = new AppVersionNumber(core, preParts, text);
        return true;
    }

    public static AppVersionNumber Parse(string text) =>
        TryParse(text, out var v) ? v! : throw new FormatException($"Versión no válida: {text}");

    public int CompareTo(AppVersionNumber? other)
    {
        if (other is null) return 1;
        int len = Math.Max(Core.Count, other.Count());
        for (int i = 0; i < len; i++)
        {
            int a = i < Core.Count ? Core[i] : 0;
            int b = i < other.Core.Count ? other.Core[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        // Núcleo igual: sin pre-release es mayor; si ambas tienen, se comparan identificadores.
        if (PreRelease.Count == 0 && other.PreRelease.Count == 0) return 0;
        if (PreRelease.Count == 0) return 1;
        if (other.PreRelease.Count == 0) return -1;
        return ComparePreRelease(other);
    }

    private int Count() => Core.Count;

    private int ComparePreRelease(AppVersionNumber other)
    {
        int len = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (int i = 0; i < len; i++)
        {
            string x = PreRelease[i], y = other.PreRelease[i];
            bool xn = int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xi);
            bool yn = int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yi);
            if (xn && yn) { if (xi != yi) return xi.CompareTo(yi); continue; }
            if (xn != yn) return xn ? -1 : 1;          // numérico < alfanumérico
            int c = string.CompareOrdinal(x, y);
            if (c != 0) return c;
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool Equals(AppVersionNumber? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as AppVersionNumber);
    public override int GetHashCode() => Core.Aggregate(17, (h, n) => h * 31 + n);
    public override string ToString() => Text;

    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out var a) && TryParse(current, out var b) && a!.CompareTo(b!) > 0;
}
