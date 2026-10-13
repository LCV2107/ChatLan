using System.Windows;
using System.Windows.Interop;
using static MiniChat.App.Desktop.NativeMethods;

namespace MiniChat.App.Desktop;

public enum DesktopMode
{
    /// <summary>Widget asociado al escritorio (propietario = ventana del shell). Predeterminado.</summary>
    DesktopWidget,
    /// <summary>Ventana compacta normal, sin integración con el escritorio.</summary>
    CompactWindow,
}

/// <summary>
/// Aísla la integración con el escritorio de Windows. Ningún otro componente llama a Win32 para esto.
///
/// Cómo funciona el modo DesktopWidget:
///  - La ventana es de herramientas (sin botón de barra de tareas), NO es TopMost.
///  - Su propietario (GWLP_HWNDPARENT) es la ventana del shell (GetShellWindow, clase "Progman").
///    Windows mantiene las ventanas con propietario por encima de su propietario, así que cuando
///    Win+D eleva el escritorio, el widget sigue visible y utilizable. Las ventanas normales que se
///    activan lo cubren como a cualquier otra ventana.
///
/// Dependencias de comportamiento NO documentado oficialmente:
///  - Usar SetWindowLongPtr(GWLP_HWNDPARENT) para asignar un propietario de OTRO proceso. Microsoft
///    lo desaconseja; funciona en Windows 10/11 pero acopla las colas de entrada con Explorer
///    (si nuestro hilo de UI se bloquea, el escritorio puede dejar de responder y viceversa).
///    Por eso la aplicación no realiza trabajo bloqueante en el hilo de UI.
///  - Que Win+D eleve Progman y respete a sus ventanas con propietario (verificado con la sonda
///    tools/MiniChat.DesktopProbe en Windows 11 26200).
/// Si Explorer se reinicia, Windows no destruye nuestra ventana (verificado: el propietario pasa a 0);
/// al recibir "TaskbarCreated" se reasigna el nuevo shell.
/// </summary>
public sealed class DesktopHost : IDisposable
{
    private Window? _window;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private uint _taskbarCreatedMessage;
    private int _reattachGeneration;

    public DesktopMode Mode { get; private set; } = DesktopMode.DesktopWidget;

    /// <summary>True si el modo widget está realmente enganchado al escritorio.</summary>
    public bool IsAttachedToDesktop { get; private set; }

    /// <summary>Descripción legible del estado real de la integración.</summary>
    public string Status { get; private set; } = "Sin inicializar";

    public event EventHandler? StatusChanged;

    /// <summary>Sumidero opcional de diagnóstico (sin datos de usuario).</summary>
    public Action<string>? Log { get; set; }

    public void Attach(Window window, DesktopMode mode)
    {
        _window = window;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _source?.AddHook(WndProc);
        Apply(mode);
    }

    public void Apply(DesktopMode mode)
    {
        if (_window is null || _hwnd == IntPtr.Zero) return;
        Mode = mode;
        IsAttachedToDesktop = false;
        SetOwner(IntPtr.Zero);

        if (mode == DesktopMode.CompactWindow)
        {
            SetToolWindow(false);
            _window.ShowInTaskbar = true;
            SetStatus("Modo ventana compacta: se comporta como una ventana normal; Win+D la minimiza.");
            return;
        }

        _window.ShowInTaskbar = false;
        SetToolWindow(true);
        var shell = GetShellWindow();
        if (shell != IntPtr.Zero && ClassNameOf(shell) == "Progman" && SetOwner(shell))
        {
            IsAttachedToDesktop = true;
            SetStatus("Widget de escritorio activo.");
        }
        else
        {
            SetStatus("No se encontró el escritorio de Explorer. El widget funciona como ventana sin barra de tareas y no permanecerá visible con Win+D.");
        }
        Log?.Invoke($"desktop apply mode={mode} attached={IsAttachedToDesktop}");
    }

    private bool SetOwner(IntPtr owner)
    {
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, owner);
        return GetWindowLongPtr(_hwnd, GWLP_HWNDPARENT) == owner;
    }

    private void SetToolWindow(bool tool)
    {
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = tool ? (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW : ex & ~WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            Log?.Invoke("desktop TaskbarCreated (Explorer reiniciado)");
            _ = ReattachAfterExplorerRestartAsync();
        }
        return IntPtr.Zero;
    }

    private async Task ReattachAfterExplorerRestartAsync()
    {
        if (Mode != DesktopMode.DesktopWidget) return;
        int generation = ++_reattachGeneration;
        IsAttachedToDesktop = false;
        SetStatus("Explorer se reinició; recuperando la integración con el escritorio…");
        // Reintentos acotados (no es un bucle permanente): el shell tarda un poco en crear Progman.
        foreach (var delay in new[] { 300, 1000, 3000, 6000 })
        {
            await Task.Delay(delay);
            if (generation != _reattachGeneration || _window is null) return;
            var shell = GetShellWindow();
            if (shell != IntPtr.Zero && ClassNameOf(shell) == "Progman")
            {
                Apply(Mode);
                return;
            }
        }
        SetStatus("No se pudo recuperar la integración tras reiniciar Explorer. Cambie de modo en Ajustes para reintentar.");
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) SetOwner(IntPtr.Zero);
    }
}
