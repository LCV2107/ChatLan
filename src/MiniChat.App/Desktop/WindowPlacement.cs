using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using static MiniChat.App.Desktop.NativeMethods;

namespace MiniChat.App.Desktop;

public sealed record PlacementData(double Left, double Top, double Width, double Height, bool Collapsed, double ExpandedHeight);

/// <summary>
/// Guarda y restaura posición/tamaño, y garantiza que la ventana quede dentro de un área visible cuando
/// cambian monitores, resolución o escalado. Trabaja en píxeles físicos con la API de monitores de Windows,
/// así funciona igual con cualquier combinación de DPI por monitor.
/// </summary>
public static class WindowPlacement
{
    public static string Serialize(PlacementData p) => JsonSerializer.Serialize(p);

    public static PlacementData? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<PlacementData>(json);
            if (p is null || double.IsNaN(p.Left) || double.IsNaN(p.Top) || p.Width < 100 || p.Height < 30) return null;
            return p;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Si la barra superior de la ventana no cae dentro del área de trabajo de algún monitor, la mueve al
    /// monitor más cercano; si es más grande que ese monitor, la reduce.
    /// </summary>
    public static void EnsureVisible(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;

        // Franja superior (zona de arrastre) de 32 px: debe ser visible en algún monitor.
        var header = new RECT { Left = r.Left + 16, Top = r.Top, Right = Math.Max(r.Left + 17, r.Right - 16), Bottom = r.Top + 32 };
        var monitor = MonitorFromRect(ref header, MONITOR_DEFAULTTONULL);
        bool headerVisible = false;
        var nearest = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor != IntPtr.Zero ? monitor : nearest, ref info)) return;
        var work = info.rcWork;
        if (monitor != IntPtr.Zero)
            headerVisible = header.Left >= work.Left - 8 && header.Right <= work.Right + 8 && header.Top >= work.Top - 8 && header.Bottom <= work.Bottom + 8;

        int w = Math.Min(r.Width, work.Width);
        int h = Math.Min(r.Height, work.Height);
        if (headerVisible && w == r.Width && h == r.Height) return;

        int left = Math.Clamp(r.Left, work.Left, work.Right - w);
        int top = Math.Clamp(r.Top, work.Top, work.Bottom - h);
        SetWindowPos(hwnd, IntPtr.Zero, left, top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
