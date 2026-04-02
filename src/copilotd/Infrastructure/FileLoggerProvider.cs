using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Rolling file logger that writes to $TEMP/copilotd/logs.
/// Rolls over daily and when files exceed 10 MB.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Debug)
    {
        _minLevel = minLevel;
        _logDir = Path.Combine(Path.GetTempPath(), "copilotd", "logs");
        Directory.CreateDirectory(_logDir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _logDir, _minLevel);

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly string _logDir;
    private readonly LogLevel _minLevel;
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    public FileLogger(string category, string logDir, LogLevel minLevel)
    {
        _category = category;
        _logDir = logDir;
        _minLevel = minLevel;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = logLevel.ToString().ToUpperInvariant()[..4];
        var line = $"[{timestamp}] [{level}] [{_category}] {message}";

        if (exception is not null)
            line += Environment.NewLine + exception;

        line += Environment.NewLine;

        try
        {
            var filePath = GetCurrentLogFile();
            File.AppendAllText(filePath, line);
        }
        catch
        {
            // Swallow logging failures
        }
    }

    private string GetCurrentLogFile()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var basePath = Path.Combine(_logDir, $"copilotd-{date}.log");

        // Check for rollover
        if (File.Exists(basePath))
        {
            var info = new FileInfo(basePath);
            if (info.Length >= MaxFileSize)
            {
                // Find next available rollover name
                for (var i = 1; ; i++)
                {
                    var rolledPath = Path.Combine(_logDir, $"copilotd-{date}-{i}.log");
                    if (!File.Exists(rolledPath) || new FileInfo(rolledPath).Length < MaxFileSize)
                        return rolledPath;
                }
            }
        }

        return basePath;
    }
}
