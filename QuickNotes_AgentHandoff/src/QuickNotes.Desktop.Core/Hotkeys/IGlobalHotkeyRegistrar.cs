using QuickNotes.Domain;

namespace QuickNotes.Desktop.Hotkeys;

public enum GlobalHotkeyAction
{
    NewNote,
    OpenManagementCenter,
    ToggleGlobalHidden
}

public sealed record GlobalHotkeyRegistrationResult(
    bool Success,
    string? ErrorCode,
    string? Message);

public interface IGlobalHotkeyRegistrar
{
    GlobalHotkeyRegistrationResult Register(
        GlobalHotkeyAction action,
        HotkeyGesture gesture);

    void Unregister(GlobalHotkeyAction action);
    void UnregisterAll();

    event EventHandler<GlobalHotkeyAction>? Pressed;
}

public sealed record HotkeyApplyResult(
    GlobalHotkeyAction Action,
    HotkeyGesture? Gesture,
    string? ErrorCode);

public sealed class GlobalHotkeyRegistrationManager
{
    private readonly IGlobalHotkeyRegistrar _registrar;

    public GlobalHotkeyRegistrationManager(IGlobalHotkeyRegistrar registrar)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
    }

    public IReadOnlyList<HotkeyApplyResult> Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _registrar.UnregisterAll();
        var results = new List<HotkeyApplyResult>();
        var configured = new[]
        {
            (GlobalHotkeyAction.NewNote, settings.NewNote),
            (GlobalHotkeyAction.OpenManagementCenter, settings.OpenManagementCenter),
            (GlobalHotkeyAction.ToggleGlobalHidden, settings.ToggleGlobalHidden)
        };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (action, text) in configured)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                results.Add(new HotkeyApplyResult(action, null, null));
                continue;
            }

            if (!HotkeyGesture.TryParse(text, out var gesture, out var parseError))
            {
                results.Add(new HotkeyApplyResult(action, null, parseError ?? "invalid_gesture"));
                continue;
            }

            if (!used.Add(gesture!.Display))
            {
                results.Add(new HotkeyApplyResult(action, gesture, "duplicate_hotkey"));
                continue;
            }

            var registration = _registrar.Register(action, gesture);
            results.Add(new HotkeyApplyResult(
                action,
                gesture,
                registration.Success ? null : registration.ErrorCode ?? "registration_failed"));
        }

        return results;
    }
}
