using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface IDeviceSettingsStore
{
    Task<DeviceSettingsReadResult> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(DeviceSettings settings, CancellationToken cancellationToken = default);
}
