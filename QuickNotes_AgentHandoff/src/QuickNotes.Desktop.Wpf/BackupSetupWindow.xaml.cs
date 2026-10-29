using System.Windows;
using Microsoft.Win32;
using QuickNotes.Application;
using QuickNotes.Desktop.Hosting;

namespace QuickNotes.Desktop.Wpf;

public partial class BackupSetupWindow : Window
{
    private readonly DesktopApplicationHost _host;

    public BackupSetupWindow(DesktopApplicationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
    }

    private async void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.BackupServices is null)
        {
            ErrorText.Text = "备份服务尚未就绪。";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择 QuickNotes 备份目录"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ChooseButton.IsEnabled = false;
        try
        {
            var result = await _host.BackupServices.Host.SetBackupDirectoryAsync(dialog.FolderName);
            if (result.Status == BackupOperationStatus.Succeeded)
            {
                DirectoryText.Text = dialog.FolderName;
                Close();
                return;
            }

            ErrorText.Text = result.Message ?? "备份目录无法使用。";
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "backup_setup", "backup directory selection failed", exception.GetType().Name);
            ErrorText.Text = "备份目录配置失败，请查看诊断日志。";
        }
        finally
        {
            ChooseButton.IsEnabled = true;
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) => Close();
}
