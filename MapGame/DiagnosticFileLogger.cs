using System.Text.Json;

namespace MapGame;

public sealed class DiagnosticFileLogger : ILoggerProvider
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private readonly object _syncRoot = new();
    private StreamWriter _writer;
    private int _part;
    private long _writtenBytes;
    private readonly string _basePath;

    public DiagnosticFileLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        _basePath = Path.Combine(directory, $"MapGame-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        _writer = OpenWriter();
    }

    private StreamWriter OpenWriter() => new(new FileStream(
        $"{_basePath}-{_part}.jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
    {
        AutoFlush = true
    };

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level = level.ToString(),
            category,
            message,
            exception = exception?.ToString()
        });
        lock (_syncRoot)
        {
            if (_writtenBytes + System.Text.Encoding.UTF8.GetByteCount(line) > MaximumFileBytes)
            {
                _writer.Dispose();
                _part++;
                _writer = OpenWriter();
                _writtenBytes = 0;
                if (_part >= 3)
                {
                    File.Delete($"{_basePath}-{_part - 3}.jsonl");
                }
            }
            _writer.WriteLine(line);
            _writtenBytes += System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _writer.Dispose();
        }
    }

    private sealed class FileLogger(DiagnosticFileLogger provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
