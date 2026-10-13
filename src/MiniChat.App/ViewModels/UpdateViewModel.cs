using MiniChat.App.Infrastructure;
using MiniChat.Core.Diagnostics;
using MiniChat.Core.Engine;
using MiniChat.Core.Update;

namespace MiniChat.App.ViewModels;

/// <summary>
/// Actualizaciones desde GitHub: comprobación manual o al arrancar, descarga verificada y lanzamiento del
/// instalador. Nunca obliga a estar conectado: si no hay Internet, no pasa nada y la app sigue igual.
/// </summary>
public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    public const string KeyChannel = "update.channel";
    public const string KeyAutoCheck = "update.autoCheck";
    public const string KeyLastRunVersion = "update.lastRunVersion";

    private readonly ChatEngine _engine;
    private readonly MainViewModel _main;
    private readonly ILog _log;
    private readonly UpdateService _service;

    private bool _checking;
    private bool _downloading;
    private double _progress;
    private string? _status;
    private bool _statusError;
    private UpdateInfo? _available;

    public UpdateViewModel(ChatEngine engine, MainViewModel main, ILog log)
    {
        _engine = engine;
        _main = main;
        _log = log;
        _service = new UpdateService(UpdateConfig.ManifestBaseUrl, UpdateConfig.TrustedPublicKeyBase64, log);
        CheckCommand = new AsyncCommand(() => CheckAsync(quiet: false));
        InstallCommand = new AsyncCommand(_ => InstallAsync(), _ => _available is not null && !_downloading);
    }

    /// <summary>Se pide instalar: la app debe lanzar el MSI verificado y cerrarse. Lo maneja <c>App</c>.</summary>
    public event EventHandler<string>? InstallRequested;

    public bool Configured => _service.IsConfigured;
    public bool NotConfigured => !_service.IsConfigured;
    public string CurrentVersion => ChatEngine.AppVersionFull;

    public UpdateChannel Channel => UpdateChannels.Parse(_engine.GetSetting(KeyChannel));

    public bool ChannelStable
    {
        get => Channel == UpdateChannel.Stable;
        set { if (value) SetChannel(UpdateChannel.Stable); }
    }

    public bool ChannelExperimental
    {
        get => Channel == UpdateChannel.Experimental;
        set { if (value) SetChannel(UpdateChannel.Experimental); }
    }

    private void SetChannel(UpdateChannel c)
    {
        _engine.SetSetting(KeyChannel, c.Id());
        OnPropertyChanged(nameof(ChannelStable));
        OnPropertyChanged(nameof(ChannelExperimental));
        ClearAvailable();
        if (Configured) _ = CheckAsync(quiet: false);
    }

    public bool AutoCheck
    {
        get => _engine.GetSetting(KeyAutoCheck) != "0";
        set { _engine.SetSetting(KeyAutoCheck, value ? "1" : "0"); OnPropertyChanged(); }
    }

    public bool Checking { get => _checking; private set => Set(ref _checking, value); }
    public bool Downloading { get => _downloading; private set => Set(ref _downloading, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool StatusError { get => _statusError; private set => Set(ref _statusError, value); }

    public bool UpdateAvailable => _available is not null;
    public string AvailableVersion => _available?.Version ?? "";
    public string AvailableNotes => _available?.Notes ?? "";
    public bool HasNotes => !string.IsNullOrWhiteSpace(_available?.Notes);
    public string InstallButtonText => _available is null ? "Actualizar" : $"Actualizar a {_available.Version}";

    public AsyncCommand CheckCommand { get; }
    public AsyncCommand InstallCommand { get; }

    /// <summary>Comprueba si hay versión nueva. <paramref name="quiet"/>: solo avisa (globo) si encuentra algo.</summary>
    public async Task CheckAsync(bool quiet)
    {
        if (!Configured)
        {
            if (!quiet) { StatusError = false; Status = "Las actualizaciones no están configuradas en esta compilación."; }
            return;
        }
        Checking = true;
        if (!quiet) Status = "Buscando actualizaciones…";
        try
        {
            var r = await _service.CheckAsync(Channel, CurrentVersion);
            StatusError = r.Status is UpdateStatus.Invalid or UpdateStatus.Failed;
            if (r.HasUpdate)
            {
                SetAvailable(r.Update!);
                Status = $"Versión {r.Update!.Version} disponible.";
                if (quiet) _main.NotifyUpdateAvailable(r.Update!.Version);
            }
            else
            {
                ClearAvailable();
                if (!quiet || StatusError) Status = r.Message;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("update", "Fallo al comprobar actualizaciones", ex);
            StatusError = true;
            if (!quiet) Status = "No se pudo comprobar: " + ex.Message;
        }
        finally
        {
            Checking = false;
        }
    }

    private async Task InstallAsync()
    {
        if (_available is null) return;
        Downloading = true;
        Progress = 0;
        StatusError = false;
        Status = $"Descargando la versión {_available.Version}…";
        try
        {
            var dir = Path.Combine(_engine.Paths.DataDirectory, "updates");
            var path = await _service.DownloadInstallerAsync(_available, dir, new Progress<double>(p => Progress = p));
            // Marca de dónde venimos, para avisar «actualización completada» al reabrir ya actualizada.
            _engine.SetSetting(KeyLastRunVersion, CurrentVersion);
            Status = "Descarga verificada. Iniciando la instalación…";
            _log.Info("update", $"Lanzando instalador {_available.Version}");
            InstallRequested?.Invoke(this, path);
        }
        catch (Exception ex)
        {
            _log.Warn("update", "Fallo al descargar/instalar la actualización", ex);
            StatusError = true;
            Status = "No se pudo instalar la actualización: " + ex.Message + " La app sigue con la versión actual.";
        }
        finally
        {
            Downloading = false;
        }
    }

    private void SetAvailable(UpdateInfo info)
    {
        _available = info;
        RaiseAvailable();
    }

    private void ClearAvailable()
    {
        if (_available is null) return;
        _available = null;
        RaiseAvailable();
    }

    private void RaiseAvailable()
    {
        foreach (var p in new[] { nameof(UpdateAvailable), nameof(AvailableVersion), nameof(AvailableNotes), nameof(HasNotes), nameof(InstallButtonText) })
            OnPropertyChanged(p);
        _main.OnUpdateStateChanged();
    }

    /// <summary>
    /// Al arrancar: si la versión actual es más nueva que la de la última ejecución, devuelve el aviso de
    /// «actualización completada». Detecta tanto el botón como una instalación hecha por TI con el MSI.
    /// </summary>
    public string? ConsumeUpdateCompletedMessage()
    {
        var current = CurrentVersion;
        var previous = _engine.GetSetting(KeyLastRunVersion);
        _engine.SetSetting(KeyLastRunVersion, current);
        if (!string.IsNullOrWhiteSpace(previous) && AppVersionNumber.IsNewer(current, previous))
        {
            _log.Info("update", $"Actualización completada: {previous} -> {current}");
            return $"Actualización completada: ahora tienes la versión {current}.";
        }
        return null;
    }

    public void Dispose() => _service.Dispose();
}
