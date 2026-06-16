using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SyncDb;

/// <summary>
/// Minimal file logging provider: appends one formatted line per entry to a file,
/// mirroring the console output. Thread-safe via a single lock — enough for a
/// single-process batch job. There is no rotation; point <c>LOG_FILE</c> at a
/// fresh path per run, or rotate the file externally.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel)
    {
        _minLevel = minLevel;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // Append so a resumed run keeps its history in one file.
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    private void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var line = $"{ts} {Short(logLevel)} {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }
            provider.Write(line);
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
