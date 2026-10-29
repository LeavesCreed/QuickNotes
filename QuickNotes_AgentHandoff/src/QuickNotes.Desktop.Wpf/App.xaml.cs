using System.Windows;
using Microsoft.Win32;
using QuickNotes.Desktop.Hosting;

namespace QuickNotes.Desktop.Wpf;

public partial class App : System.Windows.Application
{
    private DesktopApplicationHost? _host;
    private NoteWindowManager? _noteWindows;

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

        _host = new DesktopApplicationHost(options);
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
            }

            var window = canUseEditing
                ? new WorkspaceWindow(_host, _noteWindows)
                : (Window)new StartupWindow(_host);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
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

    private void OnActivationRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (MainWindow is not Window window)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
        });
    }

    private void OnRestoreApplied()
    {
        Dispatcher.Invoke(() =>
        {
            if (_host is null)
            {
                return;
            }

            var oldWindow = MainWindow;
            var oldNoteWindows = _noteWindows;
            _noteWindows = null;
            _ = RebuildAfterRestoreAsync(oldWindow, oldNoteWindows);
        });
    }

    private async Task RebuildAfterRestoreAsync(Window? oldWindow, NoteWindowManager? oldNoteWindows)
    {
        if (_host is null)
        {
            return;
        }

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
            ? new WorkspaceWindow(_host, _noteWindows)
            : (Window)new StartupWindow(_host);
        MainWindow = window;
        window.Show();
        if (oldWindow is WorkspaceWindow workspace)
        {
            workspace.CloseAfterRestore();
        }
        else
        {
            oldWindow?.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            _noteWindows?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _noteWindows = null;
            _host?.Dispose();
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
