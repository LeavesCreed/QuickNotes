using QuickNotes.Application;
using QuickNotes.Desktop.Settings;
using QuickNotes.Domain;
using Xunit;

namespace QuickNotes.Desktop.Tests;

public sealed class FileDeviceSettingsStoreTests
{
    [Fact]
    public async Task DeviceSettings_RoundTripsAtomically_AndRejectsUnknownVersion()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "device-settings.json");
        var store = new FileDeviceSettingsStore(path);
        var settings = new DeviceSettings
        {
            FormatVersion = DeviceSettings.CurrentFormatVersion,
            BackupDirectory = Path.Combine(temporary.RootPath, "backups"),
            StartupRegistrationEnabled = false,
            Hotkeys = HotkeySettings.Default,
            DisplayBindings = new Dictionary<Guid, string> { [Guid.NewGuid()] = "DISPLAY1" }
        };

        await store.WriteAsync(settings);
        var read = await store.ReadAsync();

        Assert.Equal(DeviceSettingsReadResultStatus.Found, read.Status);
        Assert.Equal(settings.BackupDirectory, read.Settings!.BackupDirectory);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task Read_WhenFileMissing_ReturnsNotFound()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileDeviceSettingsStore(
            Path.Combine(temporary.RootPath, "device-settings.json"));

        var read = await store.ReadAsync();

        Assert.Equal(DeviceSettingsReadResultStatus.NotFound, read.Status);
        Assert.Null(read.Settings);
    }

    [Fact]
    public async Task Read_WhenJsonInvalid_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "device-settings.json");
        await File.WriteAllTextAsync(path, "{");

        var read = await new FileDeviceSettingsStore(path).ReadAsync();

        Assert.Equal(DeviceSettingsReadResultStatus.Invalid, read.Status);
        Assert.Equal("device_settings_json_invalid", read.ErrorCode);
    }

    [Fact]
    public async Task Read_WhenFormatVersionUnsupported_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "device-settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "formatVersion": 2,
              "startupRegistrationEnabled": false,
              "hotkeys": null,
              "displayBindings": {}
            }
            """);

        var read = await new FileDeviceSettingsStore(path).ReadAsync();

        Assert.Equal(DeviceSettingsReadResultStatus.Invalid, read.Status);
        Assert.Equal("device_settings_version_unsupported", read.ErrorCode);
    }

    [Fact]
    public async Task Read_WhenDisplayBindingInvalid_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "device-settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "formatVersion": 1,
              "startupRegistrationEnabled": false,
              "hotkeys": null,
              "displayBindings": {
                "c4a1f6a6-89d5-4c96-9f4e-4f3f24a5f001": ""
              }
            }
            """);

        var read = await new FileDeviceSettingsStore(path).ReadAsync();

        Assert.Equal(DeviceSettingsReadResultStatus.Invalid, read.Status);
        Assert.Equal("device_settings_invalid", read.ErrorCode);
    }

    [Fact]
    public async Task Write_WithNullHotkeys_PreservesDisabledHotkeys()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "device-settings.json");
        var store = new FileDeviceSettingsStore(path);
        var noteId = Guid.NewGuid();
        var settings = new DeviceSettings
        {
            Hotkeys = null,
            DisplayBindings = new Dictionary<Guid, string> { [noteId] = "DISPLAY2" }
        };

        await store.WriteAsync(settings);
        var read = await store.ReadAsync();

        Assert.Equal(DeviceSettingsReadResultStatus.Found, read.Status);
        Assert.Null(read.Settings!.Hotkeys);
        var binding = Assert.Single(read.Settings.DisplayBindings);
        Assert.Equal(noteId, binding.Key);
        Assert.Equal("DISPLAY2", binding.Value);
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"displayBindings\"", json);
    }

    [Fact]
    public async Task Write_WhenSettingsInvalid_ThrowsAndDoesNotReplaceExistingFile()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = Path.Combine(temporary.RootPath, "device-settings.json");
        var store = new FileDeviceSettingsStore(path);
        var original = new DeviceSettings
        {
            BackupDirectory = Path.Combine(temporary.RootPath, "backups")
        };
        await store.WriteAsync(original);
        var originalJson = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(original with { FormatVersion = 2 }));

        Assert.Equal(originalJson, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
        var read = await store.ReadAsync();
        Assert.Equal(DeviceSettingsReadResultStatus.Found, read.Status);
        Assert.Equal(original.BackupDirectory, read.Settings!.BackupDirectory);
    }
}
