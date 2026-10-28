using System.Text.Json;
using System.Text.Json.Serialization;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Settings;

public sealed class FileDeviceSettingsStore : IDeviceSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDeviceSettingsStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Device settings path must not be empty.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async Task<DeviceSettingsReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return DeviceSettingsReadResult.NotFound();
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return DeviceSettingsReadResult.Invalid("device_settings_io");
            }

            DeviceSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<DeviceSettings>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                return DeviceSettingsReadResult.Invalid("device_settings_json_invalid");
            }
            catch (NotSupportedException)
            {
                return DeviceSettingsReadResult.Invalid("device_settings_unsupported");
            }

            if (settings is null)
            {
                return DeviceSettingsReadResult.Invalid("device_settings_empty");
            }

            if (settings.FormatVersion != DeviceSettings.CurrentFormatVersion)
            {
                return DeviceSettingsReadResult.Invalid("device_settings_version_unsupported");
            }

            try
            {
                settings.Validate();
            }
            catch (ArgumentException)
            {
                return DeviceSettingsReadResult.Invalid("device_settings_invalid");
            }

            return DeviceSettingsReadResult.Found(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        DeviceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
