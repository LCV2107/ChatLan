using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MiniChat.App.Desktop;

/// <summary>Inicio opcional con Windows mediante HKCU\...\Run (no requiere permisos de administrador).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName(string? profile) => string.IsNullOrEmpty(profile) || profile == "default" ? "MiniChatLAN" : "MiniChatLAN-" + profile;

    public static bool IsEnabled(string? profile)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName(profile)) is string;
    }

    public static void Set(bool enabled, string? profile)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Ruta del ejecutable desconocida.");
            var args = "--minimized" + (string.IsNullOrEmpty(profile) || profile == "default" ? "" : $" --profile \"{profile}\"");
            key.SetValue(ValueName(profile), $"\"{exe}\" {args}");
        }
        else
        {
            key.DeleteValue(ValueName(profile), throwOnMissingValue: false);
        }
    }
}

/// <summary>
/// Evita instancias accidentales múltiples del MISMO perfil (distintos perfiles pueden convivir).
/// Una segunda instancia pide a la primera que se muestre y termina.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, Action onShowRequested)
    {
        _mutex = mutex;
        _showSignal = signal;
        _registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static SingleInstance? TryAcquire(string dataDirectory, Action onShowRequested)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToLowerInvariant())))[..24];
        var mutex = new Mutex(initiallyOwned: true, @"Local\MiniChatLAN." + id, out bool created);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MiniChatLAN.Show." + id);
        if (!created)
        {
            signal.Set();
            signal.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, signal, onShowRequested);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showSignal.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
