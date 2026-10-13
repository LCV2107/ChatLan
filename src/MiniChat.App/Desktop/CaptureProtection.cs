using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using static MiniChat.App.Desktop.NativeMethods;

namespace MiniChat.App.Desktop;

/// <summary>
/// Opción «Ocultar MiniChat en capturas de pantalla» (SetWindowDisplayAffinity, API documentada).
/// Con WDA_EXCLUDEFROMCAPTURE la ventana no aparece en capturas, grabaciones ni en herramientas que capturan
/// la pantalla; en Windows anteriores a 10 2004 se usa WDA_MONITOR (aparece en negro).
/// No impide fotografiar la pantalla ni protege frente a un administrador del equipo.
/// </summary>
public static class CaptureProtection
{
    public static bool Enabled { get; private set; }

    public static void Apply(Window window, bool enabled)
    {
        Enabled = enabled;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) SetAffinity(hwnd, enabled);
    }

    public static void ApplyToPopup(Popup popup)
    {
        if (popup.Child is null) return;
        if (PresentationSource.FromVisual(popup.Child) is HwndSource source) SetAffinity(source.Handle, Enabled);
    }

    private static void SetAffinity(IntPtr hwnd, bool enabled)
    {
        if (!enabled) { SetWindowDisplayAffinity(hwnd, WDA_NONE); return; }
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }
}
