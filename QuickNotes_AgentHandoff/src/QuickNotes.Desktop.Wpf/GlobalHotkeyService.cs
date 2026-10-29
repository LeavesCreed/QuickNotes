using System.Runtime.InteropServices;
using System.Windows.Interop;
using QuickNotes.Desktop.Hotkeys;

namespace QuickNotes.Desktop.Wpf;

public sealed class GlobalHotkeyService : IGlobalHotkeyRegistrar, IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<GlobalHotkeyAction, int> _actionIds = new();
    private readonly Dictionary<int, GlobalHotkeyAction> _idActions = new();
    private bool _disposed;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("QuickNotesHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler<GlobalHotkeyAction>? Pressed;

    public GlobalHotkeyRegistrationResult Register(
        GlobalHotkeyAction action,
        HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (_disposed)
        {
            return new GlobalHotkeyRegistrationResult(false, "service_disposed", "Hotkey service is disposed.");
        }

        Unregister(action);
        var id = (int)action + 1;
        var registered = RegisterHotKey(
            _source.Handle,
            id,
            (uint)gesture.Modifiers,
            gesture.VirtualKey);
        if (!registered)
        {
            var error = Marshal.GetLastWin32Error();
            return new GlobalHotkeyRegistrationResult(
                false,
                error == 1409 ? "hotkey_conflict" : $"win32_{error}",
                "The shortcut could not be registered.");
        }

        _actionIds[action] = id;
        _idActions[id] = action;
        return new GlobalHotkeyRegistrationResult(true, null, null);
    }

    public void Unregister(GlobalHotkeyAction action)
    {
        if (_actionIds.TryGetValue(action, out var id))
        {
            UnregisterHotKey(_source.Handle, id);
            _actionIds.Remove(action);
            _idActions.Remove(id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _idActions.Keys.ToArray())
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _actionIds.Clear();
        _idActions.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(
        nint hwnd,
        int msg,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (msg == WmHotkey && _idActions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        nint hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
