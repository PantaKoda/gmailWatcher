using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Infrastructure.Logging;

/// <summary>
/// Minimal daily rolling file logger. Keeps the newest <see cref="RetainedFiles"/> files.
/// Callers must never log tokens, device codes or private repository content.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetainedFiles = 7;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.ToString().ToUpperInvariant()[..4])
            .Append(' ').Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            try
            {
                var writer = GetWriter();
                if (writer.BaseStream.Length < MaxFileBytes)
                {
                    writer.WriteLine(line);
                }
            }
            catch (IOException)
            {
                // Logging must never take the application down.
            }
        }
    }

    private StreamWriter GetWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is null || today != _currentDay)
        {
            _writer?.Dispose();
            _currentDay = today;
            var path = Path.Combine(_directory, $"gmailwatcher-{today:yyyyMMdd}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            PruneOldFiles();
        }

        return _writer;
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (var old in Directory.GetFiles(_directory, "gmailwatcher-*.log").OrderDescending().Skip(RetainedFiles))
            {
                File.Delete(old);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
