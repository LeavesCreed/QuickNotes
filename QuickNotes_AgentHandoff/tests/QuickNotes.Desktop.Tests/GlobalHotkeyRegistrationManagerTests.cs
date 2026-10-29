using QuickNotes.Desktop.Hotkeys;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Tests;

public sealed class GlobalHotkeyRegistrationManagerTests
{
    [Fact]
    public void Apply_RegistersConfiguredGestures_AndSkipsDisabledOnes()
    {
        var registrar = new FakeRegistrar();
        var manager = new GlobalHotkeyRegistrationManager(registrar);
        var results = manager.Apply(new HotkeySettings
        {
            NewNote = "Ctrl+Alt+N",
            OpenManagementCenter = null,
            ToggleGlobalHidden = "Ctrl+Alt+H"
        });

        Assert.Equal(2, registrar.Registered.Count);
        Assert.Contains(GlobalHotkeyAction.NewNote, registrar.Registered.Keys);
        Assert.Contains(GlobalHotkeyAction.ToggleGlobalHidden, registrar.Registered.Keys);
        Assert.Null(results.Single(result => result.Action == GlobalHotkeyAction.OpenManagementCenter).ErrorCode);
    }

    [Fact]
    public void Apply_DuplicateOrConflictingGestures_ReportFailures()
    {
        var registrar = new FakeRegistrar
        {
            Conflict = GlobalHotkeyAction.ToggleGlobalHidden
        };
        var manager = new GlobalHotkeyRegistrationManager(registrar);
        var results = manager.Apply(new HotkeySettings
        {
            NewNote = "Ctrl+Alt+N",
            OpenManagementCenter = "Ctrl+Alt+N",
            ToggleGlobalHidden = "Ctrl+Alt+H"
        });

        Assert.Equal(
            "duplicate_hotkey",
            results.Single(result => result.Action == GlobalHotkeyAction.OpenManagementCenter).ErrorCode);
        Assert.Equal(
            "hotkey_conflict",
            results.Single(result => result.Action == GlobalHotkeyAction.ToggleGlobalHidden).ErrorCode);
    }

    [Fact]
    public void Apply_InvalidGesture_DoesNotRegister()
    {
        var registrar = new FakeRegistrar();
        var manager = new GlobalHotkeyRegistrationManager(registrar);
        var results = manager.Apply(new HotkeySettings
        {
            NewNote = "N",
            OpenManagementCenter = null,
            ToggleGlobalHidden = null
        });

        Assert.Empty(registrar.Registered);
        Assert.Equal("missing_modifier", results[0].ErrorCode);
    }

    private sealed class FakeRegistrar : IGlobalHotkeyRegistrar
    {
        public Dictionary<GlobalHotkeyAction, HotkeyGesture> Registered { get; } = new();
        public GlobalHotkeyAction? Conflict { get; set; }

        public event EventHandler<GlobalHotkeyAction>? Pressed
        {
            add { }
            remove { }
        }

        public GlobalHotkeyRegistrationResult Register(
            GlobalHotkeyAction action,
            HotkeyGesture gesture)
        {
            if (Conflict == action)
            {
                return new GlobalHotkeyRegistrationResult(false, "hotkey_conflict", "conflict");
            }

            Registered[action] = gesture;
            return new GlobalHotkeyRegistrationResult(true, null, null);
        }

        public void Unregister(GlobalHotkeyAction action) => Registered.Remove(action);

        public void UnregisterAll() => Registered.Clear();
    }
}
