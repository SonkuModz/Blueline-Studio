namespace Blueline.Core;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset When, LogLevel Level, string Message, string? Detail);

/// <summary>Records operations and errors, in memory for the UI and to a dated file for later diagnosis.</summary>
public sealed class SessionLog
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];

    public SessionLog(string? folder = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueline", "logs");
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }

    public string FilePath { get; }
    public event Action<LogEntry>? Added;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Info(string message) => Add(LogLevel.Info, message, null);
    public void Warning(string message) => Add(LogLevel.Warning, message, null);
    public void Error(string message, Exception? exception = null) => Add(LogLevel.Error, message, exception?.ToString());

    private void Add(LogLevel level, string message, string? detail)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message, detail);
        lock (_gate)
        {
            _entries.Add(entry);
            File.AppendAllText(FilePath, $"{entry.When:HH:mm:ss} {level,-7} {message}{(detail == null ? "" : Environment.NewLine + detail)}{Environment.NewLine}");
        }
        Added?.Invoke(entry);
    }
}
