using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Startup;

namespace QuickNotes.Desktop.Wpf;

public partial class App : System.Windows.Application
{
    private DesktopApplicationHost? _host;
    private NoteWindowManager? _noteWindows;
    private TrayIconService? _tray;
    private WorkspaceWindow? _workspace;
    private StartupRegistrationService? _startupRegistration;
    private bool _exiting;
    private bool _startOnLogin;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = StartAsync(e.Args);
    }

    private async Task StartAsync(string[] args)
    {
        DesktopStartupOptions options;
        try
        {
            options = DesktopStartupOptions.Parse(args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _startOnLogin = options.StartOnLogin;
        var executable = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        _startupRegistration = new StartupRegistrationService(
            new WindowsRunRegistry(),
            "QuickNotes",
            StartupRegistrationService.BuildCommand(
                executable,
                Assembly.GetEntryAssembly()?.Location));
        _host = new DesktopApplicationHost(options, startupRegistration: _startupRegistration);
        _host.RestoreApplied += OnRestoreApplied;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            var result = await _host.StartAsync();
            if (!result.CanShowWindow)
            {
                if (!options.SingleInstanceProbe
                    && result.ExitCode != 0
                    && !string.IsNullOrWhiteSpace(result.Message))
                {
                    MessageBox.Show(
                        result.Message,
                        "QuickNotes",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                Shutdown(result.ExitCode);
                return;
            }

            _host.ActivationRequested += OnActivationRequested;
            var canUseEditing = result.Assessment?.CanEnterNormalUse == true
                && _host.EditingServices is not null;
            if (canUseEditing)
            {
                _noteWindows = new NoteWindowManager(_host);
                await _noteWindows.StartAsync();
                _tray = CreateTray();
                _workspace = new WorkspaceWindow(_host, _noteWindows, ShutdownAsync);
                MainWindow = _workspace;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (!_startOnLogin)
                {
                    _workspace.Show();
                }
                else
                {
                    _workspace.Hide();
                }

                await UpdateTrayStateAsync();
                return;
            }

            var startupWindow = new StartupWindow(_host);
            MainWindow = startupWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            startupWindow.Show();
        }
        catch (Exception exception)
        {
            TryLog("startup", "unexpected startup failure", exception.GetType().Name);
            MessageBox.Show(
                "QuickNotes 无法完成启动。请查看数据目录下的诊断日志。",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private TrayIconService CreateTray() =>
        new(
            createNote: () => _ = CreateNoteFromTrayAsync(),
            showWorkspace: ShowWorkspace,
            toggleGlobalHidden: () => _ = ToggleGlobalHiddenAsync(),
            showSettings: () => _ = ShowSettingsAsync(),
            exit: () => _ = ShutdownAsync());

    private async Task CreateNoteFromTrayAsync()
    {
        ShowWorkspace();
        if (_workspace is not null)
        {
            await _workspace.CreateNewFromTrayAsync();
        }
    }

    private void ShowWorkspace()
    {
        if (_host?.EditingServices is null)
        {
            return;
        }

        if (_workspace is null)
        {
            _workspace = new WorkspaceWindow(_host, _noteWindows, ShutdownAsync);
            MainWindow = _workspace;
        }

        _workspace.Show();
        if (_workspace.WindowState == WindowState.Minimized)
        {
            _workspace.WindowState = WindowState.Normal;
        }

        _workspace.Activate();
    }

    private async Task ShowSettingsAsync()
    {
        ShowWorkspace();
        _workspace?.OpenSettings();
        await UpdateTrayStateAsync();
        if (_noteWindows is not null)
        {
            await _noteWindows.RefreshAsync();
        }
    }

    private async Task ToggleGlobalHiddenAsync()
    {
        if (_host?.EditingServices is not { } services)
        {
            return;
        }

        try
        {
            var current = await services.Settings.GetAsync();
            var updated = current with
            {
                GlobalHidden = !current.GlobalHidden,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await services.Settings.UpdateAsync(updated, current.Version);
            _tray?.SetGlobalHidden(updated.GlobalHidden);
            if (_noteWindows is not null)
            {
                await _noteWindows.RefreshAsync();
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite(
                "WARN",
                "tray",
                "global visibility toggle failed",
                exception.GetType().Name);
        }
    }

    private async Task UpdateTrayStateAsync()
    {
        if (_host?.EditingServices is not { } services)
        {
            return;
        }

        try
        {
            var settings = await services.Settings.GetAsync();
            _tray?.SetGlobalHidden(settings.GlobalHidden);
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("WARN", "tray", "tray state read failed", exception.GetType().Name);
        }
    }

    public async Task NotifySettingsChangedAsync()
    {
        await UpdateTrayStateAsync();
        if (_noteWindows is not null)
        {
            await _noteWindows.RefreshAsync();
        }
    }

    private async void OnActivationRequested()
    {
        await Dispatcher.InvokeAsync(ShowWorkspace);
    }

    private void OnRestoreApplied()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_host is null || _exiting)
            {
                return;
            }

            var oldWindow = MainWindow;
            var oldNoteWindows = _noteWindows;
            _noteWindows = null;
            try
            {
                if (oldNoteWindows is not null)
                {
                    await oldNoteWindows.DisposeAsync();
                }

                var canUseEditing = _host.Assessment?.CanEnterNormalUse == true
                    && _host.EditingServices is not null;
                if (canUseEditing)
                {
                    _noteWindows = new NoteWindowManager(_host);
                    await _noteWindows.StartAsync();
                }

                var window = canUseEditing
                    ? new WorkspaceWindow(_host, _noteWindows, ShutdownAsync)
                    : (Window)new StartupWindow(_host);
                MainWindow = window;
                _workspace = window as WorkspaceWindow;
                window.Show();
                if (oldWindow is WorkspaceWindow workspace)
                {
                    workspace.CloseAfterRestore();
                }
                else
                {
                    oldWindow?.Close();
                }

                await UpdateTrayStateAsync();
            }
            catch (Exception exception)
            {
                TryLog("restore", "window rebuild failed", exception.GetType().Name);
            }
        });
    }

    private async Task ShutdownAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_workspace is not null)
            {
                var prepared = await _workspace.PrepareForExitAsync();
                if (!prepared)
                {
                    _exiting = false;
                    MessageBox.Show(
                        _workspace,
                        "当前便签未能保存，应用未退出。请先处理保存失败或冲突。",
                        "QuickNotes",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            if (_noteWindows is not null)
            {
                await _noteWindows.DisposeAsync();
                _noteWindows = null;
            }

            _tray?.Dispose();
            _tray = null;
            _host?.Dispose();
            _host = null;
        }
        catch (Exception exception)
        {
            TryLog("shutdown", "shutdown failed", exception.GetType().Name);
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            _noteWindows?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _noteWindows = null;
            _tray?.Dispose();
            _tray = null;
            _host?.Dispose();
            _host = null;
        }
        catch
        {
        }

        base.OnExit(e);
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _host?.BackupServices is not { } services)
        {
            return;
        }

        try
        {
            await services.Host.TriggerWakeCheckAsync();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("WARN", "backup_host", "wake backup check failed", exception.GetType().Name);
        }
    }

    private void TryLog(string eventName, string message, string errorCode)
    {
        try
        {
            _host?.Log.TryWrite("ERROR", eventName, message, errorCode);
        }
        catch
        {
        }
    }
}
