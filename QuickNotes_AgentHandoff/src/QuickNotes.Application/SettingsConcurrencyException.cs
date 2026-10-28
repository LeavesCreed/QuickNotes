using System.Globalization;

namespace QuickNotes.Application;

public sealed class SettingsConcurrencyException : Exception
{
    public SettingsConcurrencyException(long expectedVersion, long? currentVersion)
        : base(
            $"Application settings have a concurrency conflict: expected version {expectedVersion}, current version {FormatVersion(currentVersion)}.")
    {
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    public string ErrorCode => "settings_concurrency_conflict";
    public long ExpectedVersion { get; }
    public long? CurrentVersion { get; }

    private static string FormatVersion(long? version) =>
        version?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
}
