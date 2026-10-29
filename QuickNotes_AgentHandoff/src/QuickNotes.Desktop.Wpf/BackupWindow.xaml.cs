using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using QuickNotes.Application;
using QuickNotes.Desktop.Hosting;

namespace QuickNotes.Desktop.Wpf;

public partial class BackupWindow : Window
{
    private readonly DesktopApplicationHost _host;
    private readonly DesktopBackupServices _services;
    private readonly WorkspaceWindow _owner;
    private IReadOnlyList<BackupListItem> _items = Array.Empty<BackupListItem>();
    private bool _busy;
    private bool _allowClose;

    public BackupWindow(DesktopApplicationHost host, WorkspaceWindow owner)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _services = host.BackupServices
            ?? throw new InvalidOperationException("Backup services are not available.");
        InitializeComponent();
        Loaded += BackupWindow_Loaded;
    }

    private async void BackupWindow_Loaded(object sender, RoutedEventArgs e) =>
        await ReloadAsync();

    private async Task ReloadAsync()
    {
        try
        {
            var status = await _services.Host.GetStatusAsync();
            DirectoryText.Text = status.BackupDirectory ?? "未配置";
            StatusText.Text = status.AutoBackupConfigured
                ? "自动备份已配置"
                : "自动备份未配置；请选择备份目录。";
            var warnings = new List<string>();
            if (status.LastSuccessAtUtc is { } success)
            {
                StatusText.Text += $" · 最近成功：{success.ToLocalTime():yyyy-MM-dd HH:mm}";
            }

            if (status.LastFailureAtUtc is { } failure)
            {
                warnings.Add($"最近失败：{failure.ToLocalTime():yyyy-MM-dd HH:mm}（{status.LastErrorCode ?? "unknown"}）");
            }

            if (!string.IsNullOrWhiteSpace(status.LastWarning))
            {
                warnings.Add($"最近警告：{status.LastWarning}");
            }

            WarningText.Text = string.Join(Environment.NewLine, warnings);

            var catalog = await _services.Host.ListBackupsAsync();
            _items = catalog.ValidEntries.Select(entry => new BackupListItem(entry)).ToArray();
            BackupsList.ItemsSource = _items;
            IssuesText.Text = catalog.Issues.Count == 0
                ? string.Empty
                : "无法读取的备份：" + string.Join(
                    Environment.NewLine,
                    catalog.Issues.Select(issue => $"{Path.GetFileName(issue.PackagePath)}: {issue.ErrorCode}"));
            BackupsList.SelectedItem = _items.FirstOrDefault();
            UpdateButtons();
        }
        catch (Exception exception)
        {
            ShowError("读取备份列表失败", exception);
        }
    }

    private async void ConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
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

        SetBusy(true, "正在验证备份目录…");
        try
        {
            var result = await _services.Host.SetBackupDirectoryAsync(dialog.FolderName);
            ShowOperation(result);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError("备份目录配置失败", exception);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async void ManualBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在创建手动备份…");
        try
        {
            var result = await _services.Host.CreateManualBackupAsync();
            ShowOperation(result);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError("手动备份失败", exception);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async void RetryCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在检查备份…");
        try
        {
            var result = await _services.Host.RunScheduledCheckAsync();
            MessageBox.Show(
                this,
                result.Status switch
                {
                    BackupHostCheckStatus.Completed => result.Result?.Outcome switch
                    {
                        DailyBackupOutcome.BackedUp => "已创建自动日备份。",
                        DailyBackupOutcome.NoChanges => "当日没有数据变化，未创建重复备份。",
                        DailyBackupOutcome.AlreadyBackedUpToday => "当日已有有效日备份。",
                        _ => result.Result?.Error?.Message ?? "备份检查失败。"
                    },
                    BackupHostCheckStatus.NotConfigured => "自动备份尚未配置。",
                    BackupHostCheckStatus.Busy => "已有备份操作正在进行。",
                    _ => result.Message ?? "备份检查失败。"
                },
                "QuickNotes",
                MessageBoxButton.OK,
                result.Result?.Outcome == DailyBackupOutcome.Failed
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Information);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError("备份检查失败", exception);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await ReloadAsync();
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedItem is not { } item)
        {
            return;
        }

        SetBusy(true, "正在校验备份…");
        try
        {
            var result = await _services.Host.VerifyBackupAsync(item.Entry.PackagePath);
            MessageBox.Show(
                this,
                result.Status == BackupVerificationStatus.Valid
                    ? "备份完整性和格式校验通过。"
                    : result.Status == BackupVerificationStatus.Missing
                        ? "备份文件已不存在。"
                        : $"备份校验失败：{result.Error?.Diagnostics ?? result.Error?.Message ?? "未知错误"}",
                "QuickNotes",
                MessageBoxButton.OK,
                result.Status == BackupVerificationStatus.Valid
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            ShowError("备份校验失败", exception);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedItem is not { IsManual: true } item)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"将永久删除手动备份：{item.Entry.PackagePath}\n\n自动日/周备份不会从此入口删除。是否继续？",
            "QuickNotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "正在删除手动备份…");
        try
        {
            var result = await _services.Host.DeleteManualBackupAsync(item.Entry.PackagePath);
            ShowOperation(result);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError("删除手动备份失败", exception);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedItem is not { } item)
        {
            return;
        }

        var directory = await GetBackupDirectoryAsync();
        if (directory is null)
        {
            MessageBox.Show(
                this,
                "请先配置备份目录。",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "正在预检备份…");
        try
        {
            var request = new RestoreRequest
            {
                PackagePath = item.Entry.PackagePath,
                CurrentDatabasePath = _host.Paths.DatabasePath,
                BackupDirectory = directory,
                AllowPendingDrafts = true
            };
            var preflight = await _services.Restore.PreflightAsync(request);
            if (!preflight.CanProceed)
            {
                MessageBox.Show(
                    this,
                    $"该备份不能恢复：{preflight.Error?.Code} {preflight.Error?.Message}".Trim(),
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var confirmation = BuildConfirmation(item.Entry, preflight);
            if (MessageBox.Show(
                    this,
                    confirmation,
                    "确认整库恢复",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!await _owner.PrepareForRestoreAsync())
            {
                return;
            }

            IAsyncDisposable? suspension = null;
            RestoreProgressWindow? progressWindow = null;
            try
            {
                suspension = await _services.Host.SuspendForRestoreAsync();
                progressWindow = new RestoreProgressWindow { Owner = this };
                progressWindow.Show();
                var progress = new Progress<RestoreProgress>(progressWindow.Report);
                var result = await _services.Restore.RestoreAsync(
                    request,
                    progress,
                    progressWindow.CancellationToken);
                progressWindow.Finish(
                    result.Status == RestoreStatus.Succeeded
                        ? "恢复完成。"
                        : result.RolledBack
                            ? "恢复失败，已回退。"
                            : "恢复未完成。");
                if (result.Status == RestoreStatus.Succeeded)
                {
                    await suspension.DisposeAsync();
                    suspension = null;
                    _allowClose = true;
                    Close();
                    await _host.RefreshAfterRestoreAsync();
                    return;
                }

                await _owner.UnfreezeAfterRestoreFailureAsync();
                ShowRestoreResult(result);
            }
            catch (OperationCanceledException)
            {
                progressWindow?.Finish("恢复已取消。");
                await _owner.UnfreezeAfterRestoreFailureAsync();
                MessageBox.Show(
                    this,
                    "恢复已取消；当前数据库未被替换。",
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                progressWindow?.Finish("恢复失败。");
                await _owner.UnfreezeAfterRestoreFailureAsync();
                ShowError("恢复失败", exception);
            }
            finally
            {
                if (progressWindow?.IsVisible == true)
                {
                    progressWindow.Close();
                }

                if (suspension is not null)
                {
                    await suspension.DisposeAsync();
                }
            }
        }
        catch (Exception exception)
        {
            ShowError("恢复流程失败", exception);
            await _owner.UnfreezeAfterRestoreFailureAsync();
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy && !_allowClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private BackupListItem? SelectedItem => BackupsList.SelectedItem as BackupListItem;

    private void BackupsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateButtons();

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        BusyText.Text = message;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var item = SelectedItem;
        ConfigureButton.IsEnabled = !_busy;
        ManualBackupButton.IsEnabled = !_busy;
        RetryCheckButton.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
        VerifyButton.IsEnabled = !_busy && item is not null;
        DeleteButton.IsEnabled = !_busy && item is { IsManual: true };
        RestoreButton.IsEnabled = !_busy && item is not null;
    }

    private async Task<string?> GetBackupDirectoryAsync()
    {
        var result = await _services.DeviceSettings.ReadAsync();
        return result.Status == DeviceSettingsReadResultStatus.Found
            ? result.Settings!.BackupDirectory
            : null;
    }

    private static string BuildConfirmation(
        BackupCatalogEntry entry,
        RestorePreflightResult preflight)
    {
        var manifest = preflight.Manifest!;
        var counts = preflight.BackupCounts!;
        return $"""
        备份文件：{Path.GetFileName(entry.PackagePath)}
        备份时间：{manifest.CreatedAtLocal:yyyy-MM-dd HH:mm}（{manifest.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC）
        类型：{TypeText(manifest.BackupType)} · manifest {manifest.FormatVersion} · schema {manifest.SchemaVersion}
        备份内容：正常 {counts.ActiveNotes} / 归档 {counts.ArchivedNotes} / 回收站 {counts.TrashNotes}；分类 {counts.Categories}；标签 {counts.Tags}；关联 {counts.NoteTags}
        当前库：正常 {preflight.CurrentCounts?.ActiveNotes ?? 0} / 归档 {preflight.CurrentCounts?.ArchivedNotes ?? 0} / 回收站 {preflight.CurrentCounts?.TrashNotes ?? 0}
        未保存草稿：{preflight.PendingDraftCount}（恢复后保留，不自动提交）
        迁移：{(preflight.RequiresMigration ? "需要 v1→v2 迁移" : "不需要")}

        当前数据库将被整库替换。
        设备设置（备份目录、开机启动、快捷键、显示器绑定）不会被备份覆盖。
        """;
    }

    private void ShowOperation(BackupOperationResult result)
    {
        if (result.Status == BackupOperationStatus.Succeeded)
        {
            MessageBox.Show(
                this,
                result.Message ?? "操作完成。",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            this,
            $"{result.Message ?? "操作失败。"}{(result.ErrorCode is null ? string.Empty : $"（{result.ErrorCode}）")}",
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowRestoreResult(RestoreResult result)
    {
        var message = result.Status == RestoreStatus.Succeeded
            ? "恢复完成。"
            : result.RolledBack
                ? "恢复失败，当前数据库已回退。"
                : $"恢复失败：{result.Error?.Code} {result.Error?.Message}".Trim();
        if (result.WritesBlocked)
        {
            message += $"\n\n数据库写入已阻断。请重启应用进入诊断/恢复入口。\n保护快照：{result.ProtectionSnapshotPath ?? "未知"}";
        }
        else if (!string.IsNullOrWhiteSpace(result.ProtectionSnapshotPath))
        {
            message += $"\n\n保护快照：{result.ProtectionSnapshotPath}";
        }

        MessageBox.Show(
            this,
            message,
            "QuickNotes",
            MessageBoxButton.OK,
            result.Status == RestoreStatus.Succeeded
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);
    }

    private void ShowError(string title, Exception exception)
    {
        _host.Log.TryWrite("ERROR", "backup_window", title, exception.GetType().Name);
        MessageBox.Show(
            this,
            $"{title}。请查看诊断日志。",
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string TypeText(BackupType type) => type switch
    {
        BackupType.Manual => "手动",
        BackupType.Daily => "日备份",
        BackupType.Weekly => "周备份",
        _ => type.ToString()
    };
}

public sealed record BackupListItem(BackupCatalogEntry Entry)
{
    public string Display =>
        $"{Entry.Manifest.CreatedAtLocal:yyyy-MM-dd HH:mm} · {TypeText(Entry.Manifest.BackupType)} · schema {Entry.Manifest.SchemaVersion} · {Entry.Manifest.DatabaseSizeBytes / 1024} KiB";

    public string Detail => Entry.PackagePath;

    public bool IsManual => Entry.Manifest.BackupType == BackupType.Manual;

    private static string TypeText(BackupType type) => type switch
    {
        BackupType.Manual => "手动",
        BackupType.Daily => "日备份",
        BackupType.Weekly => "周备份",
        _ => type.ToString()
    };
}
