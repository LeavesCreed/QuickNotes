using System.Windows.Threading;
using QuickNotes.Application;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Wpf;

public sealed class NoteWindowManager : IAsyncDisposable
{
    private readonly DesktopApplicationHost _host;
    private readonly DesktopEditingServices _services;
    private readonly Dictionary<Guid, NoteWindow> _windows = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private readonly WindowPlacementService _placement = new();
    private bool _started;
    private bool _disposed;

    public NoteWindowManager(DesktopApplicationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _services = host.EditingServices
            ?? throw new InvalidOperationException("Editing services are not available.");
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public int WindowCount => _windows.Count;

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await RefreshAsync();
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _syncGate.WaitAsync();
        try
        {
            var services = _services;
            if (services is null)
            {
                await CloseAllWindowsAsync();
                return;
            }

            var settings = await services.Settings.GetAsync();
            var bindings = await ReadDisplayBindingsAsync();
            var active = await services.Notes.QueryAsync(new NoteQuery
            {
                Lifecycle = NoteLifecycle.Active
            });
            var desired = settings.GlobalHidden
                ? new Dictionary<Guid, Note>()
                : active
                    .Where(note => note.DisplayIntent == NoteDisplayIntent.Visible)
                    .ToDictionary(note => note.Id);

            foreach (var (noteId, window) in _windows.ToArray())
            {
                if (!desired.ContainsKey(noteId))
                {
                    await CloseWindowAsync(noteId, window);
                }
            }

            var index = 0;
            foreach (var (noteId, note) in desired)
            {
                index++;
                if (_windows.TryGetValue(noteId, out var existing))
                {
                    if (!existing.IsVisible)
                    {
                        existing.Show();
                    }

                    continue;
                }

                try
                {
                    var session = await services.Sessions.GetOrOpenAsync(noteId);
                    var window = new NoteWindow(_host, session, _placement);
                    window.HideRequested += OnHideRequested;
                    window.Closed += OnWindowClosed;
                    window.ApplyPlacement(
                        note.WindowLayout,
                        bindings.TryGetValue(noteId, out var displayId) ? displayId : null,
                        index - 1);
                    _windows[noteId] = window;
                    window.Show();
                }
                catch (Exception exception)
                {
                    _host.Log.TryWrite(
                        "WARN",
                        "note_windows",
                        "note window creation failed",
                        exception.GetType().Name);
                }
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite(
                "WARN",
                "note_windows",
                "note window refresh failed",
                exception.GetType().Name);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task ActivateNoteAsync(Guid noteId)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var note = await services.Notes.GetAsync(noteId);
            if (note is null || note.Lifecycle != NoteLifecycle.Active)
            {
                return;
            }

            if (note.DisplayIntent != NoteDisplayIntent.Visible)
            {
                var session = await services.Sessions.GetOrOpenAsync(noteId);
                session.ChangeDisplayIntent(NoteDisplayIntent.Visible);
                await session.SaveNowAsync();
                await services.Sessions.ReleaseAsync(noteId);
                await RefreshAsync();
            }

            if (_windows.TryGetValue(noteId, out var window))
            {
                await window.ActivateAsync();
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite(
                "WARN",
                "note_windows",
                "note window activation failed",
                exception.GetType().Name);
        }
    }

    public async Task CloseNoteWindowAsync(Guid noteId)
    {
        if (_windows.TryGetValue(noteId, out var window))
        {
            await CloseWindowAsync(noteId, window);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        await CloseAllWindowsAsync();
        _syncGate.Dispose();
    }

    private async void OnHideRequested(NoteWindow window)
    {
        await CloseNoteWindowAsync(window.NoteId);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is NoteWindow window)
        {
            window.HideRequested -= OnHideRequested;
            window.Closed -= OnWindowClosed;
        }
    }

    public async Task ReapplyPlacementAsync()
    {
        if (_disposed)
        {
            return;
        }

        var services = _services;
        var bindings = await ReadDisplayBindingsAsync();
        foreach (var (noteId, window) in _windows)
        {
            try
            {
                var note = await services.Notes.GetAsync(noteId);
                if (note is null)
                {
                    continue;
                }

                window.ApplyPlacement(
                    note.WindowLayout,
                    bindings.TryGetValue(noteId, out var displayId) ? displayId : null);
            }
            catch (Exception exception)
            {
                _host.Log.TryWrite(
                    "WARN",
                    "note_windows",
                    "window placement refresh failed",
                    exception.GetType().Name);
            }
        }
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ReadDisplayBindingsAsync()
    {
        if (_host.DeviceSettings is null)
        {
            return new Dictionary<Guid, string>();
        }

        var result = await _host.DeviceSettings.ReadAsync();
        return result.Status == QuickNotes.Application.DeviceSettingsReadResultStatus.Found
            && result.Settings is not null
            ? result.Settings.DisplayBindings
            : new Dictionary<Guid, string>();
    }

    private async Task CloseWindowAsync(Guid noteId, NoteWindow window)
    {
        if (!_windows.Remove(noteId))
        {
            return;
        }

        window.HideRequested -= OnHideRequested;
        window.Closed -= OnWindowClosed;
        try
        {
            await window.Session.FlushDraftAsync();
            if (window.Session.HasPendingChanges)
            {
                await window.Session.SaveNowAsync();
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite(
                "WARN",
                "note_windows",
                "session save before window close failed",
                exception.GetType().Name);
        }

        window.CloseFromManager();
        await _services.Sessions.ReleaseAsync(noteId);
    }

    public async Task<bool> PrepareForRestoreAsync()
    {
        await _syncGate.WaitAsync();
        try
        {
            foreach (var window in _windows.Values)
            {
                try
                {
                    await window.Session.FlushDraftAsync();
                    if (window.Session.HasPendingChanges)
                    {
                        var result = await window.Session.SaveNowAsync();
                        if (result.Status != SaveStatus.Saved || window.Session.HasPendingChanges)
                        {
                            return false;
                        }
                    }
                }
                catch (Exception exception)
                {
                    _host.Log.TryWrite(
                        "ERROR",
                        "note_windows",
                        "note window save before restore failed",
                        exception.GetType().Name);
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task CloseAllWindowsAsync()
    {
        foreach (var (noteId, window) in _windows.ToArray())
        {
            window.HideRequested -= OnHideRequested;
            window.Closed -= OnWindowClosed;
            try
            {
                await window.Session.FlushDraftAsync();
                if (window.Session.HasPendingChanges)
                {
                    await window.Session.SaveNowAsync();
                }
            }
            catch (Exception exception)
            {
                _host.Log.TryWrite(
                    "WARN",
                    "note_windows",
                    "session save during shutdown failed",
                    exception.GetType().Name);
            }

            window.CloseFromManager();
            await _services.Sessions.ReleaseAsync(noteId);
        }

        _windows.Clear();
    }
}
