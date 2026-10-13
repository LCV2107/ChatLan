using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using MiniChat.App.Desktop;
using MiniChat.App.ViewModels;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Engine;

namespace MiniChat.App;

public partial class MainWindow : Window
{
    private const string KeyPlacement = "ui.placement";
    private const double HeaderHeight = 36;
    private readonly MainViewModel _vm;
    private readonly ChatEngine _engine;
    private readonly ILog _log;
    private readonly DesktopHost _desktop = new();
    private readonly DispatcherTimer _saveTimer;
    private double _expandedHeight = 540;
    private bool _attached;

    public MainWindow(MainViewModel vm, ChatEngine engine, ILog log)
    {
        _vm = vm;
        _engine = engine;
        _log = log;
        InitializeComponent();
        DataContext = vm;

        var placement = WindowPlacement.Deserialize(engine.GetSetting(KeyPlacement));
        if (placement is not null)
        {
            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            _expandedHeight = placement.ExpandedHeight > 100 ? placement.ExpandedHeight : 540;
            Height = placement.Collapsed ? HeaderHeight : placement.Height;
            if (placement.Collapsed) ApplyCollapsed(true, save: false);
        }
        else
        {
            // Primera vez: esquina superior derecha del área de trabajo principal.
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Top + 24;
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePlacement(); };
        LocationChanged += (_, _) => RestartSaveTimer();
        SizeChanged += (_, _) => RestartSaveTimer();

        SourceInitialized += (_, _) =>
        {
            AttachDesktop();
            CaptureProtection.Apply(this, _vm.Settings.HideFromCapture);
        };
        _vm.CaptureProtectionChanged += (_, enabled) => CaptureProtection.Apply(this, enabled);
        // Actividad del usuario en la ventana: reinicia la cuenta del bloqueo automático.
        PreviewKeyDown += (_, _) => _vm.NotifyActivity();
        PreviewMouseDown += (_, _) => _vm.NotifyActivity();
        PreviewMouseWheel += (_, _) => _vm.NotifyActivity();
        Loaded += (_, _) => WindowPlacement.EnsureVisible(this);
        _desktop.Log = t => _log.Info("desktop", t);
        _desktop.StatusChanged += (_, _) => Dispatcher.BeginInvoke(() => _vm.SetDesktopStatus(_desktop.Status));
        _vm.DesktopModeRequested += (_, mode) =>
        {
            _desktop.Apply(mode);
            WindowPlacement.EnsureVisible(this);
        };
        // Cambios de monitores, resolución o escalado: la ventana vuelve a un área visible.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _desktop.Dispose();
        };
    }

    private void AttachDesktop()
    {
        if (_attached) return;
        _attached = true;
        _desktop.Attach(this, _vm.DesktopMode);
        _vm.SetDesktopStatus(_desktop.Status);
    }

    /// <summary>Crea la ventana sin mostrarla (arranque con Windows en la bandeja).</summary>
    public void PrepareHidden()
    {
        new WindowInteropHelper(this).EnsureHandle();
        AttachDesktop();
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowPlacement.EnsureVisible(this);
        // El usuario lo pidió explícitamente (icono de bandeja): aquí sí se activa la ventana.
        Activate();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => WindowPlacement.EnsureVisible(this), DispatcherPriority.Background);

    private void OnHideClick(object sender, RoutedEventArgs e) => Close(); // Closing lo convierte en ocultar

    private void OnCollapseClick(object sender, RoutedEventArgs e) => ApplyCollapsed(!_vm.Collapsed, save: true);

    private void ApplyCollapsed(bool collapsed, bool save)
    {
        if (collapsed)
        {
            if (!_vm.Collapsed && ActualHeight > HeaderHeight + 50) _expandedHeight = ActualHeight;
            NavBar.Visibility = Visibility.Collapsed;
            ContentArea.Visibility = Visibility.Collapsed;
            MinHeight = MaxHeight = HeaderHeight;
            Height = HeaderHeight;
            CollapseGlyph.Text = "";
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MinHeight = 200;
            Height = Math.Max(_expandedHeight, 300);
            ContentArea.Visibility = Visibility.Visible;
            NavBar.Visibility = _vm.ShowMain ? Visibility.Visible : Visibility.Collapsed;
            CollapseGlyph.Text = "";
            Dispatcher.BeginInvoke(() => WindowPlacement.EnsureVisible(this), DispatcherPriority.Background);
        }
        _vm.Collapsed = collapsed;
        _vm.SetWindowVisible(IsVisible);
        if (save) RestartSaveTimer();
    }

    private void RestartSaveTimer()
    {
        if (!IsLoaded) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SavePlacement()
    {
        try
        {
            if (WindowState != WindowState.Normal || double.IsNaN(Left)) return;
            var p = new PlacementData(Left, Top, Width, _vm.Collapsed ? HeaderHeight : Height, _vm.Collapsed, _vm.Collapsed ? _expandedHeight : ActualHeight);
            _engine.SetSetting(KeyPlacement, WindowPlacement.Serialize(p));
        }
        catch (Exception ex)
        {
            _log.Warn("ui", "No se pudo guardar la posición de la ventana", ex);
        }
    }
}
