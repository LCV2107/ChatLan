using System.Text;

namespace MiniChat.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    void Write(LogLevel level, string component, string text, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Info(this ILog log, string component, string text) => log.Write(LogLevel.Info, component, text);
    public static void Warn(this ILog log, string component, string text, Exception? ex = null) => log.Write(LogLevel.Warn, component, text, ex);
    public static void Error(this ILog log, string component, string text, Exception? ex = null) => log.Write(LogLevel.Error, component, text, ex);
    public static void Debug(this ILog log, string component, string text) => log.Write(LogLevel.Debug, component, text);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string component, string text, Exception? ex = null) { }
}

/// <summary>
/// Registro de diagnóstico con rotación por tamaño. Política: nunca se escriben textos de mensajes,
/// contenido de stickers ni claves privadas; solo eventos técnicos e identificadores abreviados.
/// </summary>
public sealed class RollingFileLog : ILog, IDisposable
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public RollingFileLog(string directory, long maxBytes = 1024 * 1024, int maxFiles = 5, LogLevel minLevel = LogLevel.Info)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(1, maxFiles);
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
    }

    private string CurrentPath => Path.Combine(_directory, "minichat.log");

    public void Write(LogLevel level, string component, string text, Exception? ex = null)
    {
        if (level < _minLevel) return;
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ')
            .Append('[').Append(component).Append("] ")
            .Append(text);
        if (ex is not null) line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        lock (_gate)
        {
            try
            {
                _writer ??= OpenWriter();
                _writer.WriteLine(line.ToString());
                _writer.Flush();
                if (_writer.BaseStream.Length >= _maxBytes) Rotate();
            }
            catch (IOException)
            {
                // El registro nunca debe tumbar la aplicación.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private StreamWriter OpenWriter()
    {
        var fs = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(fs, new UTF8Encoding(false));
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        for (int i = _maxFiles - 1; i >= 1; i--)
        {
            var src = i == 1 ? CurrentPath : Path.Combine(_directory, $"minichat.{i - 1}.log");
            var dst = Path.Combine(_directory, $"minichat.{i}.log");
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        if (_maxFiles == 1 && File.Exists(CurrentPath)) File.Delete(CurrentPath);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public static class LogFormat
{
    /// <summary>Huella abreviada para registros (no identifica mensajes ni contenido).</summary>
    public static string Short(string? fingerprint) => string.IsNullOrEmpty(fingerprint) ? "-" : fingerprint[..Math.Min(8, fingerprint.Length)];
}
