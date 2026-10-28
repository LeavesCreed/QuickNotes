using System.Windows;
using QuickNotes.Desktop.Hosting;

namespace QuickNotes.Desktop.Wpf;

public partial class App : System.Windows.Application
{
    private DesktopApplicationHost? _host;

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
            var window = result.Assessment?.CanEnterNormalUse == true
                && _host.EditingServices is not null
                ? new WorkspaceWindow(_host)
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Dispose();
        }
        catch
        {
        }

        base.OnExit(e);
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
