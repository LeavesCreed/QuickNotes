using System.Text;

namespace QuickNotes.Desktop.Diagnostics;

public sealed class DiagnosticLog
{
    private readonly string _logDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    public DiagnosticLog(string logDirectory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("Log directory must not be empty.", nameof(logDirectory));
        }

        _logDirectory = Path.GetFullPath(logDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string LogDirectory => _logDirectory;

    public bool TryWrite(
        string level,
        string eventName,
        string message,
        string? errorCode = null)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            throw new ArgumentException("Log level must not be empty.", nameof(level));
        }

        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name must not be empty.", nameof(eventName));
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var path = Path.Combine(_logDirectory, $"quicknotes-{now:yyyyMMdd}.log");
            var line = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{now:O} | {level} | {eventName} | {errorCode ?? "-"} | {Sanitize(message)}{Environment.NewLine}");
            lock (_sync)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }

            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}
