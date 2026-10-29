using QuickNotes.Desktop.Startup;

namespace QuickNotes.Desktop.Tests;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public void SetEnabled_WritesAndRemovesRegistryValue()
    {
        var registry = new FakeRunRegistry();
        var service = new StartupRegistrationService(registry, "QuickNotes", "\"QuickNotes.exe\" --startup");

        var enabled = service.SetEnabled(true);
        Assert.True(enabled.Registered);
        Assert.Equal("\"QuickNotes.exe\" --startup", registry.Values["QuickNotes"]);

        var disabled = service.SetEnabled(false);
        Assert.False(disabled.Registered);
        Assert.False(registry.Values.ContainsKey("QuickNotes"));
    }

    [Fact]
    public void GetStatus_ReportsActualRegistryStateAndFailures()
    {
        var registry = new FakeRunRegistry();
        var service = new StartupRegistrationService(registry, "QuickNotes", "cmd");
        registry.Values["QuickNotes"] = "stale";
        Assert.True(service.GetStatus().Registered);
        Assert.Equal("stale", service.GetStatus().ActualValue);

        registry.FailRead = true;
        var status = service.GetStatus();
        Assert.False(status.Registered);
        Assert.NotNull(status.ErrorCode);
    }

    [Fact]
    public void BuildCommand_UsesEntryAssemblyForDotnetHost()
    {
        var command = StartupRegistrationService.BuildCommand(
            @"C:\Program Files\dotnet\dotnet.exe",
            @"D:\Apps\QuickNotes.dll");

        Assert.Equal(
            "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"D:\\Apps\\QuickNotes.dll\" --startup",
            command);
    }

    private sealed class FakeRunRegistry : IRunRegistry
    {
        public Dictionary<string, string> Values { get; } = new();
        public bool FailRead { get; set; }

        public string? Read(string valueName)
        {
            if (FailRead)
            {
                throw new UnauthorizedAccessException();
            }

            return Values.TryGetValue(valueName, out var value) ? value : null;
        }

        public void Write(string valueName, string value) => Values[valueName] = value;

        public void Delete(string valueName) => Values.Remove(valueName);
    }
}
