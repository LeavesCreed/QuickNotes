using QuickNotes.Domain;

namespace QuickNotes.Application;

public enum DeviceSettingsReadResultStatus
{
    Found,
    NotFound,
    Invalid
}

public sealed record DeviceSettingsReadResult
{
    public required DeviceSettingsReadResultStatus Status { get; init; }
    public DeviceSettings? Settings { get; init; }
    public string? ErrorCode { get; init; }

    public static DeviceSettingsReadResult Found(DeviceSettings settings) => new()
    {
        Status = DeviceSettingsReadResultStatus.Found,
        Settings = settings
    };

    public static DeviceSettingsReadResult NotFound() => new()
    {
        Status = DeviceSettingsReadResultStatus.NotFound
    };

    public static DeviceSettingsReadResult Invalid(string errorCode) => new()
    {
        Status = DeviceSettingsReadResultStatus.Invalid,
        ErrorCode = errorCode
    };
}
