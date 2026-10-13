using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using MiniChat.App.Desktop;
using MiniChat.App.ViewModels;
using MiniChat.Core.Config;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Engine;
using MiniChat.Core.Network;
using MiniChat.Core.Persistence;

namespace MiniChat.App;

/// <summary>
/// Arranque de la aplicación. Argumentos:
///   --profile &lt;nombre&gt;     perfil de datos distinto (varios perfiles pueden convivir; el mismo perfil no)
///   --minimized             arrancar oculto en la bandeja (lo usa el inicio con Windows)
///   --desktop-mode &lt;modo&gt;  DesktopWidget | CompactWindow (fuerza el modo en esta ejecución)
///   --data-dir &lt;carpeta&gt;   carpeta de datos explícita (desarrollo/pruebas)
///   --tcp-port N, --discovery-port N   solo para ejecutar dos perfiles en la misma PC durante pruebas
/// </summary>
public partial class App : Application
{
    private RollingFileLog? _log;
    private SingleInstance? _single;
    private ChatEngine? _engine;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;
    private string? _profile;

    public static new App? Current => Application.Current as App;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

        _profile = Arg("--profile");
        AppPaths paths;
        try
        {
            paths = Arg("--data-dir") is { } dir ? new AppPaths(dir) : AppPaths.ForProfile(_profile);
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo preparar la carpeta de datos: " + ex.Message, "MiniChat LAN", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _single = SingleInstance.TryAcquire(paths.DataDirectory, () => Dispatcher.BeginInvoke(ShowWindow));
        if (_single is null)
        {
            // Ya hay una instancia con este perfil: se le pidió que se muestre.
            Shutdown(0);
            return;
        }

        // Solo este usuario de Windows (y SYSTEM) puede abrir la carpeta de datos.
        var aclError = paths.RestrictToCurrentUser();
        _log = new RollingFileLog(paths.LogDirectory);
        if (aclError is not null) _log.Warn("app", "No se pudieron restringir los permisos de la carpeta de datos: " + aclError);
        _log.Info("app", $"Inicio {ChatEngine.AppVersion} en {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ev) => { _log.Error("app", "Excepción no observada en tarea", ev.Exception); ev.SetObserved(); };

        try
        {
            var options = new MiniChatOptions();
            if (int.TryParse(Arg("--tcp-port"), out var tcp) && tcp is > 0 and < 65536) options = options with { TcpPort = tcp };
            if (int.TryParse(Arg("--discovery-port"), out var udp) && udp is > 0 and < 65536) options = options with { DiscoveryPort = udp };
            _engine = ChatEngine.Open(paths, options, new WindowsNetworkInventory(_log), _log);
        }
        catch (DatabaseUnavailableException ex)
        {
            _log.Error("app", "Datos no disponibles", ex);
            var answer = MessageBox.Show(
                ex.Message + "\n\nMiniChat no ha borrado ni modificado nada. Puedes copiar la carpeta de datos como respaldo y pedir ayuda.\n\n¿Abrir la carpeta de datos?",
                "MiniChat LAN — no se pueden leer los datos", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { paths.DataDirectory } });
            ExitNow(2);
            return;
        }
        catch (CryptographicException ex)
        {
            _log.Error("app", "No se pudo abrir la clave de identidad", ex);
            MessageBox.Show("No se pudo acceder a la clave de identidad en el almacén de claves de Windows: " + ex.Message,
                "MiniChat LAN", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitNow(3);
            return;
        }

        var mode = Enum.TryParse<DesktopMode>(Arg("--desktop-mode"), true, out var forced) ? forced
            : Enum.TryParse<DesktopMode>(_engine.GetSetting(SettingsViewModel.KeyDesktopMode), out var saved) ? saved
            : DesktopMode.DesktopWidget;

        _vm = new MainViewModel(_engine, _log, _profile, mode);
        // La red se abre aunque esté bloqueado (para recibir), pero solo si ya existe el PIN.
        if (_engine.HasDisplayName && !_engine.NeedsPinSetup)
        {
            try { _engine.StartNetworking(); }
            catch (Exception ex) { _vm.ReportUnexpected(ex); }
        }

        _window = new MainWindow(_vm, _engine, _log);
        _window.Closing += (_, ce) =>
        {
            // Alt+F4 o cerrar = ocultar. Salir de verdad solo desde la bandeja.
            if (_exiting) return;
            ce.Cancel = true;
            HideWindow();
        };
        _window.IsVisibleChanged += (_, _) => _vm.SetWindowVisible(_window.IsVisible);

        _tray = new TrayIcon("MiniChat LAN");
        _tray.ToggleRequested += (_, _) => { if (_window.IsVisible) HideWindow(); else ShowWindow(); };
        _tray.PeopleRequested += (_, _) => { ShowWindow(); _vm.ShowPeople(); };
        _tray.SettingsRequested += (_, _) => { ShowWindow(); _vm.ShowSettings(); };
        _tray.ExitRequested += async (_, _) => await ExitAsync();
        _tray.MuteToggled += (_, _) => _vm.Settings.NotificationsEnabled = !_vm.Settings.NotificationsEnabled;
        _tray.MenuOpening += (_, _) => _tray.SetMenuState(_window.IsVisible, _vm.Muted);
        _tray.BalloonClicked += (_, _) => ShowWindow();
        _vm.TrayStateChanged += (_, _) => UpdateTray();
        _vm.BalloonRequested += (_, b) => _tray.ShowBalloon(b.Title, b.Text);
        _vm.Updates.InstallRequested += (_, path) => _ = InstallUpdateAsync(path);

        if (args.Contains("--minimized") && _engine.HasDisplayName && !_engine.NeedsPinSetup)
        {
            _window.PrepareHidden();
            _vm.SetWindowVisible(false);
        }
        else
        {
            _window.Show();
        }
        _ = _vm.People.RefreshAsync();

        // Si la app se abrió ya actualizada (por el botón o por un despliegue de TI), avisarlo una vez.
        if (_vm.Updates.ConsumeUpdateCompletedMessage() is { } doneMsg) _vm.NotifyUpdateCompleted(doneMsg);
        // Comprobación opcional al arrancar: no bloquea, falla en silencio si no hay Internet.
        if (_vm.Updates.AutoCheck) _ = _vm.Updates.CheckAsync(quiet: true);
    }

    /// <summary>Lanza el MSI verificado (con permiso de administrador) y cierra la app para que pueda reemplazarse.</summary>
    private async Task InstallUpdateAsync(string msiPath)
    {
        try
        {
            var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(msiPath);
            psi.ArgumentList.Add("/qb");   // interfaz básica: barra de progreso estándar de Windows, sin preguntas
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _vm?.ReportUnexpected(new InvalidOperationException("Se canceló el permiso de administrador; la actualización no se instaló. La app sigue con la versión actual."));
            return;
        }
        catch (Exception ex)
        {
            _log?.Error("update", "No se pudo iniciar el instalador", ex);
            _vm?.ReportUnexpected(ex);
            return;
        }
        _tray?.ShowBalloon("Instalando actualización", "MiniChat se cerrará para instalar la nueva versión.");
        await ExitAsync();
    }

    private void UpdateTray()
    {
        if (_tray is null || _vm is null) return;
        bool attention = _vm.TotalUnread > 0 || _vm.HasIncomingRequests;
        var tip = _vm.TotalUnread > 0 ? $"MiniChat LAN — {_vm.TotalUnread} sin leer"
            : _vm.HasIncomingRequests ? "MiniChat LAN — solicitud pendiente" : "MiniChat LAN";
        _tray.SetUnread(attention, tip);
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.ShowFromTray();
    }

    private void HideWindow()
    {
        if (_window is null || _engine is null) return;
        _ = _vm?.SaveStateAsync();
        _window.Hide();
        _ = TrimMemoryWhileHiddenAsync();
        if (_engine.GetSetting("ui.hideTipShown") != "1")
        {
            _engine.SetSetting("ui.hideTipShown", "1");
            _tray?.ShowBalloon("MiniChat sigue activo", "Puedes recibir mensajes. Para salir del todo usa «Salir de MiniChat» en el menú de este icono.");
        }
    }

    /// <summary>
    /// Oculto en la bandeja no hace falta conservar miniaturas decodificadas: se liberan y se devuelve
    /// la memoria al sistema. Se recargan bajo demanda al volver a mostrar la ventana.
    /// </summary>
    private async Task TrimMemoryWhileHiddenAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_window is null || _window.IsVisible) return;
        StickerImageCache.Clear();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _log?.Info("app", "Salida solicitada por el usuario");
        try
        {
            if (_vm is not null) { await _vm.SaveStateAsync(); _vm.Shutdown(); }
            _window?.SavePlacement();
        }
        catch (Exception ex)
        {
            _log?.Warn("app", "No se pudo guardar el estado al salir", ex);
        }
        _window?.Close();
        _tray?.Dispose();
        if (_engine is not null)
        {
            try { await _engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { _log?.Warn("app", "Cierre de red incompleto", ex); }
        }
        ExitNow(0);
    }

    private void ExitNow(int code)
    {
        _single?.Dispose();
        _log?.Dispose();
        Shutdown(code);
    }

    public void ReportUnexpected(Exception ex)
    {
        if (_vm is not null) _vm.ReportUnexpected(ex);
        else _log?.Error("app", "Error inesperado", ex);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Un error en la interfaz no debe cerrar la aplicación ni perder datos: se registra y se informa.
        _log?.Error("app", "Excepción no controlada en la interfaz", e.Exception);
        e.Handled = true;
        _vm?.ReportUnexpected(e.Exception);
    }
}
