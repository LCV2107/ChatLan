using System.Collections.ObjectModel;
using System.Diagnostics;
using MiniChat.App.Desktop;
using MiniChat.App.Infrastructure;
using MiniChat.Core.Engine;
using MiniChat.Core.Network;

namespace MiniChat.App.ViewModels;

public sealed class NetworkItem
{
    public NetworkItem(InterfaceDecision d, Action<string, bool?> setTrust)
    {
        Decision = d;
        TrustCommand = new RelayCommand(() => setTrust(d.Interface.TrustKey, true));
        DenyCommand = new RelayCommand(() => setTrust(d.Interface.TrustKey, false));
        DefaultCommand = new RelayCommand(() => setTrust(d.Interface.TrustKey, null));
    }

    public InterfaceDecision Decision { get; }
    public string Title => $"{Decision.Interface.Name} — {Decision.Interface.Address}";
    public string Detail => $"Red {Decision.Interface.SubnetText} · Perfil {CategoryText}" + (string.IsNullOrEmpty(Decision.Interface.NetworkName) ? "" : $" · «{Decision.Interface.NetworkName}»");
    public string CategoryText => Decision.Interface.Category switch
    {
        NetworkCategory.Private => "privado",
        NetworkCategory.Domain => "dominio",
        NetworkCategory.Public => "público",
        _ => "desconocido",
    };
    public bool Allowed => Decision.Allowed;
    public string StateText => (Decision.Allowed ? "En uso: " : "No se usa: ") + Decision.Reason;
    public bool IsLoopback => System.Net.IPAddress.IsLoopback(Decision.Interface.Address);
    public bool CanTrust => !Decision.Allowed && !IsLoopback;
    public bool CanDeny => Decision.Allowed && !IsLoopback;
    public bool IsUserChoice => Decision.Reason.Contains("usuario");
    public RelayCommand TrustCommand { get; }
    public RelayCommand DenyCommand { get; }
    public RelayCommand DefaultCommand { get; }
}

/// <summary>Un resultado del diagnóstico de red, preparado para mostrarse.</summary>
public sealed class FindingItem
{
    public FindingItem(NetworkFinding f) => Finding = f;
    public NetworkFinding Finding { get; }
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string? Fix => Finding.Fix;
    public bool HasFix => !string.IsNullOrEmpty(Finding.Fix);
    public string Glyph => Finding.Severity switch
    {
        FindingSeverity.Error => "\uEA39",
        FindingSeverity.Warning => "\uE7BA",
        FindingSeverity.Ok => "\uE73E",
        _ => "\uE946",
    };
    public string Color => Finding.Severity switch
    {
        FindingSeverity.Error => "#F2777A",
        FindingSeverity.Warning => "#F2B35B",
        FindingSeverity.Ok => "#4CC27A",
        _ => "#B0B4BB",
    };
}

/// <summary>Subred de otra área en la lista autorizada.</summary>
public sealed class RemoteSubnetItem
{
    public RemoteSubnetItem(RemoteSubnetView v, Func<string, Task> remove)
    {
        View = v;
        RemoveCommand = new AsyncCommand(() => remove(v.Subnet));
    }

    public RemoteSubnetView View { get; }
    public string Title => View.Subnet;
    public string Detail =>
        (View.PeersOnline == 1 ? "1 compañero visible" : $"{View.PeersOnline} compañeros visibles")
        + (View.Route is null ? " · no se llega por una red autorizada" : $" · se llega por {View.Route}")
        + (View.FromAdministrator ? " · definida por el administrador" : "");
    public bool HasRoute => View.Route is not null;
    public bool CanRemove => !View.FromAdministrator;
    public AsyncCommand RemoveCommand { get; }
}

/// <summary>Subred encontrada al explorar la oficina.</summary>
public sealed class CandidateItem
{
    public CandidateItem(SubnetCandidate c, Func<string, Task> add)
    {
        Candidate = c;
        AddCommand = new AsyncCommand(() => add(c.Subnet));
    }

    public SubnetCandidate Candidate { get; }
    public string Title => Candidate.Subnet + (Candidate.IsOwn ? "  (su red)" : "");
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Candidate.Gateway is not null) parts.Add($"router {Candidate.Gateway} respondió en {Candidate.RoundTripMs} ms");
            else if (!Candidate.IsOwn) parts.Add("su router no respondió a ping");
            if (Candidate.PeersOnline > 0) parts.Add($"{Candidate.PeersOnline} con MiniChat");
            if (Candidate.IsAuthorized && !Candidate.IsOwn) parts.Add("ya autorizada");
            return parts.Count == 0 ? "Se usa directamente" : string.Join(" · ", parts);
        }
    }
    public bool CanAdd => !Candidate.IsOwn && !Candidate.IsAuthorized;
    public AsyncCommand AddCommand { get; }
}

public sealed class SettingsViewModel : ObservableObject
{
    public const string KeyLockMode = "sec.lockMode";          // "idle" | "never"
    public const string KeyLockMinutes = "sec.lockMinutes";
    public const string KeyLockOnWindowsLock = "sec.lockOnWindowsLock";
    public const string KeyHideFromCapture = "sec.hideFromCapture";
    public static readonly int[] LockMinuteOptions = { 1, 5, 10, 15, 30, 60 };

    public const string KeyNotify = "ui.notify";
    public const string KeySound = "ui.sound";
    public const string KeyPreview = "ui.preview";
    public const string KeyDesktopMode = "ui.desktopMode";
    public const string KeyTheme = "ui.theme";

    private readonly ChatEngine _engine;
    private readonly MainViewModel _main;
    private readonly string? _profile;
    private string _displayName;
    private string? _nameMessage;
    private bool _nameError;
    private string? _dataMessage;

    public SettingsViewModel(ChatEngine engine, MainViewModel main, string? profile)
    {
        _engine = engine;
        _main = main;
        _profile = profile;
        _displayName = engine.DisplayName;
        SaveNameCommand = new AsyncCommand(SaveNameAsync);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_engine.Paths.DataDirectory));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(_engine.Paths.LogDirectory));
        CleanStickersCommand = new AsyncCommand(async () =>
        {
            var n = await _engine.Stickers.CollectGarbageAsync();
            DataMessage = n == 0 ? "No había imágenes sin uso." : $"Se liberaron {n} archivo(s) de imágenes que ya no se usaban.";
        });
        RefreshNetworksCommand = new RelayCommand(() => { _engine.RefreshNetwork(); LoadNetworks(); });
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync);
        RefreshOfflineCommand = new RelayCommand(RefreshOffline);
        AddSubnetCommand = new AsyncCommand(() => AddSubnetAsync(NewSubnet));
        ScanOfficeCommand = new AsyncCommand(ScanOfficeAsync);
        ProbeIpCommand = new AsyncCommand(ProbeIpAsync);
        AddSuggestedCommand = new AsyncCommand(async () => { if (SuggestedSubnet is { } s) await AddSubnetAsync(s); });
        LoadNetworks();
        _sweepTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sweepTimer.Tick += (_, _) => RefreshSweepStatus();
        _sweepTimer.Start();
    }

    // ======================= Otras subredes de la oficina =======================

    private readonly System.Windows.Threading.DispatcherTimer _sweepTimer;
    private string _newSubnet = "";
    private string? _subnetMessage;
    private bool _subnetError;
    private string _sweepText = "";
    private bool _scanning;
    private int _scanProgress;
    private string? _scanSummary;
    private string _probeIp = "";
    private bool _probing;
    private FindingItem? _probeVerdict;
    private string? _suggestedSubnet;

    public ObservableCollection<RemoteSubnetItem> RemoteSubnets { get; } = new();
    public bool HasRemoteSubnets => RemoteSubnets.Count > 0;
    public string NewSubnet { get => _newSubnet; set => Set(ref _newSubnet, value); }
    public string? SubnetMessage { get => _subnetMessage; set { Set(ref _subnetMessage, value); OnPropertyChanged(nameof(HasSubnetMessage)); } }
    public bool SubnetError { get => _subnetError; set => Set(ref _subnetError, value); }
    public bool HasSubnetMessage => !string.IsNullOrEmpty(_subnetMessage);
    public AsyncCommand AddSubnetCommand { get; }

    public bool RemoteSweepEnabled
    {
        get => _engine.RemoteSweepEnabled;
        set { _engine.RemoteSweepEnabled = value; OnPropertyChanged(); RefreshSweepStatus(); }
    }

    public string SweepText { get => _sweepText; private set => Set(ref _sweepText, value); }

    private async Task AddSubnetAsync(string text)
    {
        var r = await _engine.AddRemoteSubnetAsync(text);
        SubnetError = !r.Success;
        SubnetMessage = r.Message;
        if (!r.Success) return;
        NewSubnet = "";
        LoadRemoteSubnets();
        var added = _engine.GetRemoteSubnets().Select(v => v.Subnet).ToHashSet();
        for (int i = 0; i < Candidates.Count; i++)
            if (added.Contains(Candidates[i].Candidate.Subnet) && !Candidates[i].Candidate.IsAuthorized)
                Candidates[i] = new CandidateItem(Candidates[i].Candidate with { IsAuthorized = true }, AddSubnetAsync);
        if (SuggestedSubnet is { } s && added.Contains(s)) SuggestedSubnet = null;
        _main.People.QueueRefresh();
    }

    private async Task RemoveSubnetAsync(string text)
    {
        var r = await _engine.RemoveRemoteSubnetAsync(text);
        SubnetError = !r.Success;
        SubnetMessage = r.Message;
        LoadRemoteSubnets();
        _main.People.QueueRefresh();
    }

    public void LoadRemoteSubnets()
    {
        RemoteSubnets.Clear();
        foreach (var v in _engine.GetRemoteSubnets()) RemoteSubnets.Add(new RemoteSubnetItem(v, RemoveSubnetAsync));
        OnPropertyChanged(nameof(HasRemoteSubnets));
        RefreshSweepStatus();
    }

    private void RefreshSweepStatus()
    {
        if (RemoteSubnets.Count == 0) { SweepText = ""; return; }
        var st = _engine.RemoteSweepStatus;
        if (!_engine.RemoteSweepEnabled) SweepText = "Búsqueda automática desactivada: solo se mantiene a los compañeros ya encontrados.";
        else if (st is null) SweepText = "La red no está iniciada.";
        else if (st.Running) SweepText = $"Buscando… {st.Sent:N0} de {st.Total:N0} direcciones ({(st.Total == 0 ? 0 : st.Sent * 100 / st.Total)} %).";
        else if (st.LastFinished is { } t) SweepText = $"Última búsqueda completa: {t.ToLocalTime():HH:mm}. Se repite cada {(int)_engine.Options.RemoteSweepInterval.TotalMinutes} min.";
        else SweepText = "Búsqueda pendiente.";
        // Contadores de compañeros: solo se reconstruye la lista si algo cambió.
        var fresh = _engine.GetRemoteSubnets();
        if (fresh.Count != RemoteSubnets.Count || fresh.Where((v, i) => v != RemoteSubnets[i].View).Any())
        {
            RemoteSubnets.Clear();
            foreach (var v in fresh) RemoteSubnets.Add(new RemoteSubnetItem(v, RemoveSubnetAsync));
            OnPropertyChanged(nameof(HasRemoteSubnets));
        }
    }

    // ---- Explorar la oficina ----

    public ObservableCollection<CandidateItem> Candidates { get; } = new();
    public bool Scanning { get => _scanning; private set { Set(ref _scanning, value); } }
    public int ScanProgress { get => _scanProgress; private set => Set(ref _scanProgress, value); }
    public string? ScanSummary { get => _scanSummary; private set { Set(ref _scanSummary, value); OnPropertyChanged(nameof(HasScanSummary)); } }
    public bool HasScanSummary => !string.IsNullOrEmpty(_scanSummary);
    public AsyncCommand ScanOfficeCommand { get; }

    private async Task ScanOfficeAsync()
    {
        Scanning = true;
        ScanProgress = 0;
        ScanSummary = null;
        Candidates.Clear();
        try
        {
            var result = await _engine.ScanOfficeSubnetsAsync(new Progress<int>(p => ScanProgress = p));
            foreach (var c in result.Subnets) Candidates.Add(new CandidateItem(c, AddSubnetAsync));
            ScanSummary = result.Summary;
        }
        finally
        {
            Scanning = false;
        }
    }

    // ---- Probar una IP ----

    public string ProbeIp { get => _probeIp; set => Set(ref _probeIp, value); }
    public bool Probing { get => _probing; private set { Set(ref _probing, value); } }
    public ObservableCollection<FindingItem> ProbeSteps { get; } = new();
    public FindingItem? ProbeVerdict { get => _probeVerdict; private set { Set(ref _probeVerdict, value); OnPropertyChanged(nameof(HasProbeVerdict)); } }
    public bool HasProbeVerdict => _probeVerdict is not null;
    public string? SuggestedSubnet { get => _suggestedSubnet; private set { Set(ref _suggestedSubnet, value); OnPropertyChanged(nameof(HasSuggestedSubnet)); OnPropertyChanged(nameof(SuggestedText)); } }
    public bool HasSuggestedSubnet => _suggestedSubnet is not null;
    public string SuggestedText => $"Añadir {_suggestedSubnet} a la lista";
    public AsyncCommand ProbeIpCommand { get; }
    public AsyncCommand AddSuggestedCommand { get; }

    private async Task ProbeIpAsync()
    {
        Probing = true;
        ProbeSteps.Clear();
        ProbeVerdict = null;
        SuggestedSubnet = null;
        try
        {
            var r = await _engine.ProbeAddressAsync(ProbeIp);
            foreach (var step in r.Steps) ProbeSteps.Add(new FindingItem(step));
            ProbeVerdict = new FindingItem(r.Verdict);
            SuggestedSubnet = r.SuggestedSubnet;
        }
        finally
        {
            Probing = false;
        }
    }

    // ======================= Seguridad =======================

    public bool LockOnIdle
    {
        get => _engine.GetSetting(KeyLockMode) != "never";
        set { if (value) { _engine.SetSetting(KeyLockMode, "idle"); OnPropertyChanged(); OnPropertyChanged(nameof(LockNever)); } }
    }

    public bool LockNever
    {
        get => _engine.GetSetting(KeyLockMode) == "never";
        set { if (value) { _engine.SetSetting(KeyLockMode, "never"); OnPropertyChanged(); OnPropertyChanged(nameof(LockOnIdle)); } }
    }

    public int[] MinuteOptions => LockMinuteOptions;

    public int LockMinutes
    {
        get => int.TryParse(_engine.GetSetting(KeyLockMinutes), out var m) && LockMinuteOptions.Contains(m) ? m : 15;
        set { _engine.SetSetting(KeyLockMinutes, value.ToString()); OnPropertyChanged(); }
    }

    public bool LockOnWindowsLock
    {
        get => _engine.GetSetting(KeyLockOnWindowsLock) != "0";
        set { _engine.SetSetting(KeyLockOnWindowsLock, value ? "1" : "0"); OnPropertyChanged(); }
    }

    public bool HideFromCapture
    {
        get => _engine.GetSetting(KeyHideFromCapture) == "1";
        set { _engine.SetSetting(KeyHideFromCapture, value ? "1" : "0"); OnPropertyChanged(); _main.SetCaptureProtection(value); }
    }

    // ======================= Apariencia (tema claro/oscuro) =======================

    private AppTheme Theme => ThemeManager.Parse(_engine.GetSetting(KeyTheme));
    public bool ThemeSystem { get => Theme == AppTheme.System; set { if (value) SetTheme(AppTheme.System); } }
    public bool ThemeLight { get => Theme == AppTheme.Light; set { if (value) SetTheme(AppTheme.Light); } }
    public bool ThemeDark { get => Theme == AppTheme.Dark; set { if (value) SetTheme(AppTheme.Dark); } }

    private void SetTheme(AppTheme t)
    {
        _engine.SetSetting(KeyTheme, ThemeManager.Id(t));
        ThemeManager.Apply(t);
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }

    private string? _pinMessage;
    private bool _pinError;
    public string? PinMessage { get => _pinMessage; set { Set(ref _pinMessage, value); OnPropertyChanged(nameof(HasPinMessage)); } }
    public bool PinError { get => _pinError; set => Set(ref _pinError, value); }
    public bool HasPinMessage => !string.IsNullOrEmpty(_pinMessage);

    public async Task ChangePinAsync(string current, string next, string confirm)
    {
        if (next != confirm) { PinError = true; PinMessage = "Los dos PIN nuevos no coinciden."; return; }
        PinMessage = "Cambiando…";
        PinError = false;
        var r = await _engine.ChangePinAsync(current, next);
        PinError = !r.Success;
        PinMessage = r.Message;
    }

    /// <summary>Al bloquear, se quitan de la pantalla los resultados que pudieran contener datos.</summary>
    public void ClearSensitive()
    {
        PinMessage = null;
        Findings.Clear();
        OnPropertyChanged(nameof(HasFindings));
    }

    // ======================= Diagnóstico de red =======================

    public ObservableCollection<FindingItem> Findings { get; } = new();
    public bool HasFindings => Findings.Count > 0;
    private bool _diagnosing;
    public bool Diagnosing { get => _diagnosing; private set => Set(ref _diagnosing, value); }
    public AsyncCommand DiagnoseCommand { get; }

    private async Task DiagnoseAsync()
    {
        Diagnosing = true;
        try
        {
            var findings = await _engine.DiagnoseNetworkAsync();
            Findings.Clear();
            foreach (var f in findings) Findings.Add(new FindingItem(f));
            OnPropertyChanged(nameof(HasFindings));
            LoadNetworks();
            RefreshOffline();
        }
        finally
        {
            Diagnosing = false;
        }
    }

    // ======================= Prueba sin Internet =======================

    private string _offlineInternet = "";
    private string _offlineLan = "";
    private string _offlinePeers = "";
    private string _offlineBlocked = "";
    private bool _offlineCutActive;
    public string OfflineInternet { get => _offlineInternet; private set => Set(ref _offlineInternet, value); }
    public string OfflineLan { get => _offlineLan; private set => Set(ref _offlineLan, value); }
    public string OfflinePeers { get => _offlinePeers; private set => Set(ref _offlinePeers, value); }
    public string OfflineBlocked { get => _offlineBlocked; private set => Set(ref _offlineBlocked, value); }
    public bool OfflineCutActive { get => _offlineCutActive; private set => Set(ref _offlineCutActive, value); }
    public RelayCommand RefreshOfflineCommand { get; }

    public void RefreshOffline()
    {
        var s = _engine.GetOfflineStatus();
        OfflineInternet = s.InternetAvailable switch
        {
            true => "Internet: disponible en este equipo (según Windows)",
            false => "Internet: NO disponible en este equipo (según Windows)",
            _ => "Internet: estado desconocido",
        };
        OfflineLan = s.AuthorizedSubnets.Count == 0 ? "Red local: ninguna red autorizada" : "Red local en uso: " + string.Join(", ", s.AuthorizedSubnets);
        OfflinePeers = $"Compañeros visibles ahora: {s.PeersOnline} · contactos disponibles: {s.ContactsOnline}";
        OfflineBlocked = $"Intentos de salir de la red local bloqueados en esta sesión: {s.BlockedOutsideLan}";
        OfflineCutActive = s.InternetCutForMiniChat;
    }

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string? NameMessage { get => _nameMessage; set { Set(ref _nameMessage, value); OnPropertyChanged(nameof(HasNameMessage)); } }
    public bool NameError { get => _nameError; set => Set(ref _nameError, value); }
    public bool HasNameMessage => !string.IsNullOrEmpty(_nameMessage);

    /// <summary>Actualizaciones (la tarjeta ACTUALIZACIONES se enlaza aquí; el estado vive en MainViewModel).</summary>
    public UpdateViewModel Updates => _main.Updates;

    public string MachineName => _engine.MachineName;
    public string IdentityCode => _engine.Identity.DisplayCode;
    public string Fingerprint => _engine.Identity.Fingerprint;
    public string Ports => $"TCP {_engine.Options.TcpPort} (conversaciones, TLS) · UDP {_engine.Options.DiscoveryPort} (descubrimiento, grupo {_engine.Options.DiscoveryGroup})";
    public string DataFolder => _engine.Paths.DataDirectory;
    public string Version => "MiniChat LAN " + ChatEngine.AppVersion;
    public string? ServerError => _engine.Server?.LastBindError ?? _engine.Discovery?.LastError;
    public bool HasServerError => !string.IsNullOrEmpty(ServerError);
    public bool IdentityRegenerated => _engine.IdentityRegenerated;

    public ObservableCollection<NetworkItem> Networks { get; } = new();
    public string LocalIps => string.Join(", ", _engine.Policy.Decisions.Where(d => !System.Net.IPAddress.IsLoopback(d.Interface.Address)).Select(d => d.Interface.Address.ToString()).Distinct());

    public bool NotificationsEnabled
    {
        get => _engine.GetSetting(KeyNotify) != "0";
        set { _engine.SetSetting(KeyNotify, value ? "1" : "0"); OnPropertyChanged(); _main.OnMuteChanged(); }
    }

    public bool SoundEnabled
    {
        get => _engine.GetSetting(KeySound) == "1";
        set { _engine.SetSetting(KeySound, value ? "1" : "0"); OnPropertyChanged(); }
    }

    public bool ShowPreview
    {
        get => _engine.GetSetting(KeyPreview) == "1";
        set { _engine.SetSetting(KeyPreview, value ? "1" : "0"); OnPropertyChanged(); }
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled(_profile);
        set
        {
            try { StartupRegistration.Set(value, _profile); }
            catch (Exception ex) { DataMessage = "No se pudo cambiar el inicio con Windows: " + ex.Message; }
            OnPropertyChanged();
        }
    }

    public bool DesktopWidgetMode
    {
        get => _main.DesktopMode == DesktopMode.DesktopWidget;
        set
        {
            if (!value) return;
            _main.SetDesktopMode(DesktopMode.DesktopWidget);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompactMode));
        }
    }

    public bool CompactMode
    {
        get => _main.DesktopMode == DesktopMode.CompactWindow;
        set
        {
            if (!value) return;
            _main.SetDesktopMode(DesktopMode.CompactWindow);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DesktopWidgetMode));
        }
    }

    public string DesktopStatus => _main.DesktopStatus;
    public void RaiseDesktopStatus() => OnPropertyChanged(nameof(DesktopStatus));

    public string? DataMessage { get => _dataMessage; set { Set(ref _dataMessage, value); OnPropertyChanged(nameof(HasDataMessage)); } }
    public bool HasDataMessage => !string.IsNullOrEmpty(_dataMessage);

    public AsyncCommand SaveNameCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public AsyncCommand CleanStickersCommand { get; }
    public RelayCommand RefreshNetworksCommand { get; }

    public string SecuritySummary =>
        "Sus mensajes, borradores, nombres de contactos y stickers se guardan cifrados (AES-256-GCM). " +
        "Para leerlos hacen falta su PIN y su cuenta de Windows a la vez. Bloqueado, MiniChat sigue recibiendo " +
        "mensajes, pero nadie puede verlos sin el PIN.";

    public string SecurityLimits =>
        "Límites: no oculta quién habla con quién ni cuándo (metadatos), no protege frente a programas espía " +
        "instalados en el equipo mientras está desbloqueado, y un PIN corto es más fácil de adivinar. " +
        "Si olvida el PIN, el historial no se puede recuperar.";

    public void LoadNetworks()
    {
        Networks.Clear();
        foreach (var d in _engine.Policy.Decisions.Where(d => !System.Net.IPAddress.IsLoopback(d.Interface.Address)))
            Networks.Add(new NetworkItem(d, SetTrust));
        LoadRemoteSubnets();
        OnPropertyChanged(nameof(LocalIps));
        OnPropertyChanged(nameof(ServerError));
        OnPropertyChanged(nameof(HasServerError));
    }

    private void SetTrust(string key, bool? trusted)
    {
        _engine.SetNetworkTrust(key, trusted);
        LoadNetworks();
        _main.People.QueueRefresh();
    }

    private async Task SaveNameAsync()
    {
        var r = await _engine.SetDisplayNameAsync(DisplayName);
        NameError = !r.Success;
        NameMessage = r.Success ? "Guardado. Tus compañeros verán el nombre nuevo en unos segundos." : r.Message;
        if (r.Success) { DisplayName = _engine.DisplayName; _main.OnDisplayNameChanged(); }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            DataMessage = "No se pudo abrir la carpeta: " + ex.Message;
        }
    }
}
