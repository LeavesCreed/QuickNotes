using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.Startup;
using QuickNotes.Desktop.State;

namespace QuickNotes.Desktop.Wpf;

public partial class StartupWindow : Window
{
    private readonly DesktopApplicationHost _host;
    private bool _busy;

    public StartupWindow(DesktopApplicationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var assessment = _host.Assessment;
        if (assessment is null)
        {
            StatusText.Text = "启动中";
            ReasonText.Text = "正在检查数据目录和恢复状态。";
            DetailsText.Text = string.Empty;
            SetButtonsEnabled(false, false, false);
            OpenWorkspaceButton.IsEnabled = false;
            HintText.Text = string.Empty;
            return;
        }

        StatusText.Text = assessment.Mode switch
        {
            StartupMode.FirstRunInitialized => "首次初始化完成",
            StartupMode.Ready => "数据层已就绪",
            StartupMode.NeedsRecovery => "需要恢复处理",
            StartupMode.WriteBlocked => "数据库写入已阻断",
            _ => "只读诊断模式"
        };
        ReasonText.Text = assessment.Message;
        DetailsText.Text = BuildDetails(assessment);
        var canRebuild = !_busy
            && assessment.State is not null
            && assessment.Reason is StartupReason.DatabaseMissing
                or StartupReason.DatabaseCorrupt
                or StartupReason.WritesBlocked;
        SetButtonsEnabled(
            openData: true,
            rebuild: canRebuild,
            retry: !_busy);
        RetryButton.Content = assessment.Reason == StartupReason.InitializationIncomplete
            ? "重试初始化"
            : "重试检查";
        HintText.Text = assessment.Mode switch
        {
            StartupMode.FirstRunInitialized or StartupMode.Ready =>
                "数据层已就绪；可以打开编辑器。",
            StartupMode.WriteBlocked =>
                "写入已阻断；重启不会自动解除。请保留诊断信息并等待恢复入口处理。",
            StartupMode.NeedsRecovery =>
                "不会自动删除恢复文件；请使用已验证备份或打开数据目录检查。",
            _ =>
                "当前为只读诊断；不会初始化空库或删除现有数据。"
        };
        OpenWorkspaceButton.IsEnabled = !_busy
            && assessment.CanEnterNormalUse
            && _host.EditingServices is not null;
    }

    private static string BuildDetails(StartupAssessment assessment)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"模式: {assessment.Mode}");
        builder.AppendLine($"原因: {assessment.Reason}");
        if (!string.IsNullOrWhiteSpace(assessment.ErrorCode))
        {
            builder.AppendLine($"错误码: {assessment.ErrorCode}");
        }

        if (assessment.DatabaseInfo is { } info)
        {
            builder.AppendLine($"schema: {info.SchemaVersion}");
            builder.AppendLine(
                $"便签: 正常 {info.Counts.ActiveNotes}, 归档 {info.Counts.ArchivedNotes}, 回收站 {info.Counts.TrashNotes}");
            builder.AppendLine($"分类: {info.Counts.Categories}; 标签: {info.Counts.Tags}; 关联: {info.Counts.NoteTags}");
        }

        if (assessment.State is { } state)
        {
            builder.AppendLine($"初始化: {state.InitializationStatus}; schema={state.SchemaVersion}");
            if (state.WritesBlocked)
            {
                builder.AppendLine($"写入阻断: {state.WriteBlockReason}");
            }

            if (!string.IsNullOrWhiteSpace(state.RecoverySourceSha256))
            {
                builder.AppendLine($"上次重建来源哈希: {state.RecoverySourceSha256}");
                builder.AppendLine($"保护快照已创建: {state.RecoveryProtectionSnapshotCreated}");
            }
        }

        builder.AppendLine($"恢复产物: {assessment.Artifacts.Artifacts.Count}");
        foreach (var artifact in assessment.Artifacts.Artifacts)
        {
            builder.AppendLine($"  {artifact.Kind}: {Path.GetFileName(artifact.Path)}");
        }

        return builder.ToString();
    }

    private void SetButtonsEnabled(bool openData, bool rebuild, bool retry)
    {
        OpenDataButton.IsEnabled = openData;
        RebuildButton.IsEnabled = rebuild;
        RetryButton.IsEnabled = retry;
    }

    private void OpenDataButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _host.Paths.DataRoot,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "startup_window", "data folder could not be opened", exception.GetType().Name);
            MessageBox.Show(this, "无法打开数据目录。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var assessment = _host.Assessment;
        var dialog = new OpenFileDialog
        {
            Title = "选择已验证的 QuickNotes 备份",
            Filter = "QuickNotes 备份 (*.zip)|*.zip|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var recoveringBlockedWrites = assessment?.Reason == StartupReason.WritesBlocked;
        var quarantine = recoveringBlockedWrites
            || assessment?.Reason == StartupReason.DatabaseCorrupt;
        if (quarantine)
        {
            var answer = MessageBox.Show(
                this,
                recoveringBlockedWrites
                    ? "当前数据库写入已阻断。重建会先隔离当前库，再用已验证备份发布新库；校验成功后解除阻断。是否继续？"
                    : "当前数据库损坏。重建会先把原库隔离保留，再用已验证备份发布新库。是否继续？",
                "QuickNotes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _busy = true;
        Refresh();
        try
        {
            var result = await _host.RebuildFromBackupAsync(
                dialog.FileName,
                quarantine,
                recoverBlockedWrites: recoveringBlockedWrites);
            if (result.Status == MissingDatabaseRebuildStatus.Succeeded)
            {
                MessageBox.Show(
                    this,
                    "恢复完成。数据库已通过校验，未创建保护快照。",
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    this,
                    $"恢复未完成：{result.ErrorCode}\n{result.Message}",
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "startup_window", "rebuild failed", exception.GetType().Name);
            MessageBox.Show(this, "恢复过程中发生错误，请查看诊断日志。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Refresh();
        try
        {
            var assessment = _host.Assessment;
            if (assessment?.Reason == StartupReason.InitializationIncomplete
                && assessment.State?.InitializationStatus == ApplicationInitializationStatus.Initializing)
            {
                await _host.RetryInitializationAsync();
            }
            else
            {
                await _host.RetryStartupAsync();
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "startup_window", "startup retry failed", exception.GetType().Name);
            MessageBox.Show(this, "重试检查失败，请查看诊断日志。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.EditingServices is null)
        {
            MessageBox.Show(this, "编辑服务尚未就绪。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var workspace = new WorkspaceWindow(_host);
        System.Windows.Application.Current.MainWindow = workspace;
        workspace.Show();
        Close();
    }
}
