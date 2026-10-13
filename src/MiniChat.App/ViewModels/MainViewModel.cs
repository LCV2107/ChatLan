using System.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MiniChat.App.Desktop;
using MiniChat.App.Infrastructure;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Engine;
using MiniChat.Core.Security;

namespace MiniChat.App.ViewModels;

public enum Page { People, Chat, Settings }

public sealed class MainViewModel : ObservableObject
{
    private readonly ChatEngine _engine;
    private readonly ILog _log;
    private readonly DispatcherTimer _autoLockTimer;
    private Page _page = Page.People;
    private bool _windowVisible = true;
    private bool _collapsed;
    private int _totalUnread;
    private string? _globalError;
    private string _onboardingName = "";
    private string? _onboardingError;
    private string? _lockError;
    private bool _busy;
    private bool _pendingPeopleRefresh;
    private DateTime _lastActivity = DateTime.UtcNow;

    public MainViewModel(ChatEngine engine, ILog log, string? profile, DesktopMode desktopMode)
    {
        _engine = engine;
        _log = log;
        DesktopMode = desktopMode;
        People = new PeopleViewModel(engine, this);
        Chat = new ChatViewModel(engine, this);
        Updates = new UpdateViewModel(engine, this, log);
        Settings = new SettingsViewModel(engine, this, profile);
        ShowPeopleCommand = new RelayCommand(ShowPeople);
        ShowChatCommand = new RelayCommand(() => CurrentPage = Page.Chat);
        ShowSettingsCommand = new RelayCommand(ShowSettings);
        DismissErrorCommand = new RelayCommand(() => GlobalError = null);
        LockNowCommand = new RelayCommand(LockNow, () => ShowMain);
        _onboardingName = engine.HasDisplayName ? engine.DisplayName : Environment.UserName;

        engine.PeopleChanged += (_, _) => Ui.Post(OnPeopleChanged);
        engine.ContactChanged += (_, id) => Ui.Post(async () => { OnPeopleChanged(); if (!_engine.IsLocked) await Chat.OnContactChangedAsync(id); });
        engine.MessageChanged += (_, e) => Ui.Post(() =>
        {
            if (!_engine.IsLocked) Chat.OnMessageChanged(e);
            if (e.IsNew && e.Message.Direction == MiniChat.Core.Model.MessageDirection.Incoming) OnUnreadPossiblyChanged();
        });
        engine.UnreadChanged += (_, _) => Ui.Post(OnUnreadPossiblyChanged);
        engine.FileChanged += (_, e) => Ui.Post(() => { if (!_engine.IsLocked) Chat.OnFileChanged(e); });
        engine.Notice += (_, n) => Ui.Post(() => OnNotice(n));
        engine.LockStateChanged += (_, _) => Ui.Post(async () => await OnLockStateChangedAsync());

        // Bloqueo automático: comprobación cada 15 s (no afecta a las ventanas; solo mira el tiempo sin uso).
        _autoLockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _autoLockTimer.Tick += (_, _) => CheckAutoLock();
        _autoLockTimer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public PeopleViewModel People { get; }
    public ChatViewModel Chat { get; }
    public UpdateViewModel Updates { get; }
    public SettingsViewModel Settings { get; }

    // ======================= Actualizaciones =======================

    public bool UpdateAvailable => Updates.UpdateAvailable;
    public string UpdateTooltip => $"Hay una versión nueva ({Updates.AvailableVersion}). Ve a Ajustes para instalarla.";

    public void OnUpdateStateChanged()
    {
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateTooltip));
    }

    /// <summary>Aviso discreto de versión nueva (al comprobar automáticamente). Nunca roba el foco.</summary>
    public void NotifyUpdateAvailable(string version)
    {
        OnUpdateStateChanged();
        if (Settings.NotificationsEnabled)
            BalloonRequested?.Invoke(this, ("Actualización disponible", $"Versión {version}. Abre MiniChat › Ajustes para instalarla."));
    }

    /// <summary>Aviso de que la app se abrió ya actualizada (botón o despliegue de TI).</summary>
    public void NotifyUpdateCompleted(string message)
    {
        if (Settings.NotificationsEnabled)
            BalloonRequested?.Invoke(this, ("MiniChat LAN", message));
    }

    public string MyName => _engine.DisplayName;
    public string Subtitle => $"{_engine.DisplayName} · {_engine.MachineName}";

    // ======================= Estados de la ventana =======================

    /// <summary>Falta el nombre visible o el PIN (primera ejecución o perfil de una versión anterior).</summary>
    public bool Onboarding => !_engine.HasDisplayName || _engine.NeedsPinSetup;
    /// <summary>Perfil anterior con nombre pero sin PIN: solo se pide crear el PIN.</summary>
    public bool OnboardingPinOnly => _engine.HasDisplayName && _engine.NeedsPinSetup;
    public bool Locked => !Onboarding && _engine.IsLocked;
    public bool ShowMain => !Onboarding && !_engine.IsLocked;

    private void RaiseStates()
    {
        foreach (var p in new[] { nameof(Onboarding), nameof(OnboardingPinOnly), nameof(Locked), nameof(ShowMain), nameof(Subtitle) })
            OnPropertyChanged(p);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public string OnboardingName { get => _onboardingName; set => Set(ref _onboardingName, value); }
    public string? OnboardingError { get => _onboardingError; set => Set(ref _onboardingError, value); }
    public string? LockError { get => _lockError; set { Set(ref _lockError, value); OnPropertyChanged(nameof(HasLockError)); } }
    public bool HasLockError => !string.IsNullOrEmpty(_lockError);
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public string MachineName => _engine.MachineName;
    public int MinPinLength => Vault.MinPinLength;

    public Page CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsPeople));
            OnPropertyChanged(nameof(IsChat));
            OnPropertyChanged(nameof(IsSettings));
            if (value == Page.Chat) _ = Chat.MarkReadAsync();
            if (value == Page.Settings) Settings.LoadNetworks();
            if (value == Page.People && _pendingPeopleRefresh) { _pendingPeopleRefresh = false; People.QueueRefresh(); }
        }
    }

    public bool IsPeople { get => _page == Page.People; set { if (value) CurrentPage = Page.People; } }
    public bool IsChat { get => _page == Page.Chat; set { if (value) CurrentPage = Page.Chat; } }
    public bool IsSettings { get => _page == Page.Settings; set { if (value) CurrentPage = Page.Settings; } }
    public bool IsChatVisible => _windowVisible && !_collapsed && _page == Page.Chat && ShowMain;

    public bool Collapsed { get => _collapsed; set { Set(ref _collapsed, value); OnPropertyChanged(nameof(IsChatVisible)); } }

    public int TotalUnread { get => _totalUnread; private set { Set(ref _totalUnread, value); OnPropertyChanged(nameof(HasUnread)); OnPropertyChanged(nameof(UnreadBadge)); } }
    public bool HasUnread => _totalUnread > 0;
    public string UnreadBadge => _totalUnread > 99 ? "99+" : _totalUnread.ToString();
    public int IncomingRequests => People.IncomingCount;
    public bool HasIncomingRequests => People.IncomingCount > 0;

    public string? GlobalError { get => _globalError; set { Set(ref _globalError, value); OnPropertyChanged(nameof(HasGlobalError)); } }
    public bool HasGlobalError => !string.IsNullOrEmpty(_globalError);

    public DesktopMode DesktopMode { get; private set; }
    public string DesktopStatus { get; private set; } = "";
    public event EventHandler<DesktopMode>? DesktopModeRequested;
    public event EventHandler? TrayStateChanged;
    /// <summary>Petición de aviso discreto (globo de la bandeja). La ventana nunca se activa por un mensaje.</summary>
    public event EventHandler<(string Title, string Text)>? BalloonRequested;
    /// <summary>Cambió la opción de ocultar la ventana en capturas de pantalla.</summary>
    public event EventHandler<bool>? CaptureProtectionChanged;

    public RelayCommand ShowPeopleCommand { get; }
    public RelayCommand ShowChatCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand LockNowCommand { get; }

    public bool Muted => !Settings.NotificationsEnabled;

    public void ShowPeople() => CurrentPage = Page.People;
    public void ShowSettings() => CurrentPage = Page.Settings;

    public async void OpenChat(long contactId)
    {
        CurrentPage = Page.Chat;
        try { await Chat.OpenAsync(contactId); }
        catch (Exception ex) { ReportUnexpected(ex); }
    }

    public void SetDesktopMode(DesktopMode mode)
    {
        DesktopMode = mode;
        _engine.SetSetting(SettingsViewModel.KeyDesktopMode, mode.ToString());
        DesktopModeRequested?.Invoke(this, mode);
    }

    public void SetDesktopStatus(string status)
    {
        DesktopStatus = status;
        Settings.RaiseDesktopStatus();
    }

    public void SetCaptureProtection(bool enabled) => CaptureProtectionChanged?.Invoke(this, enabled);

    // ======================= PIN y bloqueo =======================

    /// <summary>Primera ejecución: nombre + PIN. Perfil anterior: solo PIN (y se cifra lo existente).</summary>
    public async Task FinishOnboardingAsync(string pin, string confirm)
    {
        OnboardingError = null;
        if (!_engine.HasDisplayName)
        {
            var name = await _engine.SetDisplayNameAsync(OnboardingName);
            if (!name.Success) { OnboardingError = name.Message; return; }
        }
        if (pin != confirm) { OnboardingError = "Los dos PIN no coinciden."; return; }
        if (Vault.ValidatePin(pin) is { } error) { OnboardingError = error; return; }
        Busy = true;
        try
        {
            var r = await _engine.SetupPinAsync(pin);
            if (!r.Success) { OnboardingError = r.Message; return; }
            _engine.StartNetworking();
        }
        catch (Exception ex)
        {
            ReportUnexpected(ex);
        }
        finally
        {
            Busy = false;
        }
        _lastActivity = DateTime.UtcNow;
        Settings.DisplayName = _engine.DisplayName;
        OnDisplayNameChanged();
        RaiseStates();
        await People.RefreshAsync();
    }

    public async Task UnlockAsync(string pin)
    {
        LockError = null;
        Busy = true;
        try
        {
            var r = await _engine.UnlockAsync(pin);
            if (!r.Success) LockError = r.Message;
            else _lastActivity = DateTime.UtcNow;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>PIN olvidado: se borra el historial cifrado de este equipo y se crea un PIN nuevo.</summary>
    public async Task ResetForgottenPinAsync(string pin, string confirm)
    {
        LockError = null;
        if (pin != confirm) { LockError = "Los dos PIN no coinciden."; return; }
        Busy = true;
        try
        {
            var r = await _engine.ResetForgottenPinAsync(pin);
            if (!r.Success) LockError = r.Message;
            else GlobalError = r.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public void LockNow()
    {
        if (ShowMain) _engine.Lock();
    }

    /// <summary>La ventana informa de actividad del usuario (teclado o ratón) para el bloqueo por inactividad.</summary>
    public void NotifyActivity() => _lastActivity = DateTime.UtcNow;

    private void CheckAutoLock()
    {
        if (!ShowMain || !Settings.LockOnIdle) return;
        if (DateTime.UtcNow - _lastActivity >= TimeSpan.FromMinutes(Settings.LockMinutes))
        {
            _log.Info("security", "Bloqueo automático por inactividad");
            _engine.Lock();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && Settings.LockOnWindowsLock)
            Ui.Post(LockNow);
    }

    private async Task OnLockStateChangedAsync()
    {
        if (_engine.IsLocked)
        {
            // Fuera de la memoria y de la pantalla todo lo descifrado.
            await Chat.LockAsync();
            People.Clear();
            StickerImageCache.Clear();
            Settings.ClearSensitive();
            GC.Collect();
        }
        else
        {
            LockError = null;
            await People.RefreshAsync();
            await Chat.ResumeAsync();
        }
        RaiseStates();
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ======================= Ventana y avisos =======================

    /// <summary>Visibilidad de la ventana: si está oculta se evita refrescar listas (menos trabajo en reposo).</summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        OnPropertyChanged(nameof(IsChatVisible));
        if (visible && ShowMain)
        {
            if (_pendingPeopleRefresh && _page == Page.People) { _pendingPeopleRefresh = false; People.QueueRefresh(); }
            Chat.RefreshAvailability();
            if (IsChatVisible) _ = Chat.MarkReadAsync();
        }
    }

    /// <summary>La lista de personas solo se reconstruye si está a la vista; si no, se marca como pendiente.</summary>
    private void OnPeopleChanged()
    {
        if (!ShowMain) { _pendingPeopleRefresh = true; QueueUnreadRecount(); return; }
        Chat.RefreshAvailability();
        if (!_windowVisible || _collapsed || _page != Page.People)
        {
            _pendingPeopleRefresh = true;
            QueueUnreadRecount();
            return;
        }
        People.QueueRefresh();
    }

    private void OnUnreadPossiblyChanged()
    {
        _pendingPeopleRefresh = true;
        if (ShowMain && _windowVisible && !_collapsed && _page == Page.People) { _pendingPeopleRefresh = false; People.QueueRefresh(); }
        else QueueUnreadRecount();
    }

    private bool _recountQueued;

    /// <summary>Recalcula el total de no leídos (agrupando ráfagas de mensajes en una sola consulta).</summary>
    private void QueueUnreadRecount()
    {
        if (_recountQueued) return;
        _recountQueued = true;
        _ = Task.Delay(400).ContinueWith(_ => Ui.Post(async () => { _recountQueued = false; await RecountUnreadAsync(); }));
    }

    public void OnPeopleRefreshed(IReadOnlyList<PersonItem> items)
    {
        TotalUnread = items.Sum(i => i.Unread);
        OnPropertyChanged(nameof(IncomingRequests));
        OnPropertyChanged(nameof(HasIncomingRequests));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RecountUnreadAsync()
    {
        // Los contadores no están cifrados: se pueden mostrar aunque la app esté bloqueada.
        TotalUnread = await Task.Run(() => _engine.Db.Read(c => MiniChat.Core.Persistence.MessagesRepo.ListConversations(c).Sum(v => v.UnreadCount)));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNotice(EngineNotice n)
    {
        if (n.Kind == NoticeKind.NewMessage && IsChatVisible && Chat.ContactId == n.ContactId)
        {
            _ = Chat.MarkReadAsync();
            return; // ya está viendo esa conversación
        }
        if (!Settings.NotificationsEnabled) return;
        if (n.Kind == NoticeKind.IdentityMismatch && ShowMain) { GlobalError = $"El equipo de {n.DisplayName} presenta otra identidad. No se envió nada; revisa la conversación."; return; }
        // Sin robar el foco: solo indicador en bandeja/encabezado, sonido opcional y globo si la ventana no está a la vista.
        if (Settings.SoundEnabled) SystemSounds.Asterisk.Play();
        if (_windowVisible && !_collapsed && ShowMain) return;
        (string, string) balloon;
        if (_engine.IsLocked)
        {
            // Bloqueado: el aviso no revela quién ni qué.
            balloon = n.Kind is NoticeKind.NewMessage or NoticeKind.FileOffered
                ? ("MiniChat LAN", "Tiene mensajes nuevos.")
                : ("MiniChat LAN", "Hay novedades en sus solicitudes de chat.");
        }
        else
        {
            balloon = n.Kind switch
            {
                NoticeKind.IncomingRequest => ("Solicitud de chat", $"{n.DisplayName} quiere chatear contigo."),
                NoticeKind.RequestAccepted => ("Solicitud aceptada", $"{n.DisplayName} aceptó chatear."),
                NoticeKind.RequestRejected => ("Solicitud rechazada", $"{n.DisplayName} rechazó la solicitud."),
                NoticeKind.FileOffered => ($"{n.DisplayName} quiere enviarte un archivo", Settings.ShowPreview ? n.Preview ?? "" : "Ábrelo en MiniChat para aceptarlo o rechazarlo."),
                _ => ($"Mensaje de {n.DisplayName}", Settings.ShowPreview ? n.Preview ?? "" : "Nuevo mensaje"),
            };
        }
        BalloonRequested?.Invoke(this, balloon);
    }

    public void OnMuteChanged()
    {
        OnPropertyChanged(nameof(Muted));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnDisplayNameChanged()
    {
        OnPropertyChanged(nameof(MyName));
        OnPropertyChanged(nameof(Subtitle));
    }

    public void ReportUnexpected(Exception ex)
    {
        _log.Error("ui", "Error inesperado", ex);
        GlobalError = "Se produjo un error inesperado: " + ex.Message + " (detalles en el registro de diagnóstico).";
    }

    public Task SaveStateAsync() => _engine.IsLocked ? Task.CompletedTask : Chat.SaveDraftAsync();

    public void Shutdown()
    {
        _autoLockTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Updates.Dispose();
    }
}
