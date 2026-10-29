using System.Windows;
using QuickNotes.Application;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Hotkeys;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Wpf;

public partial class SettingsWindow : Window
{
    private readonly DesktopApplicationHost _host;
    private readonly WorkspaceWindow _owner;

    public SettingsWindow(DesktopApplicationHost host, WorkspaceWindow owner)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var settings = await _host.EditingServices!.Settings.GetAsync();
            DefaultFontSizeBox.Text = settings.DefaultFontSize.ToString();
            NotificationsCheckBox.IsChecked = settings.NotificationsEnabled;
            GlobalHiddenCheckBox.IsChecked = settings.GlobalHidden;
            var startup = _host.StartupRegistration;
            if (startup is null)
            {
                StartupCheckBox.IsEnabled = false;
                StartupStatusText.Text = "当前运行环境不支持开机启动注册。";
            }
            else
            {
                var status = startup.GetStatus();
                StartupCheckBox.IsChecked = status.Registered;
                StartupStatusText.Text = status.ErrorCode is null
                    ? (status.Registered ? "当前已注册开机启动。" : "当前未注册开机启动。")
                    : $"读取注册状态失败：{status.ErrorCode}";
            }

            TopBarPinCheckBox.IsChecked = settings.TopBar.Actions.Contains(TopBarAction.Pin);
            TopBarCategoryCheckBox.IsChecked = settings.TopBar.Actions.Contains(TopBarAction.Category);
            TopBarColorCheckBox.IsChecked = settings.TopBar.Actions.Contains(TopBarAction.Color);
            TopBarFontSizeCheckBox.IsChecked = settings.TopBar.Actions.Contains(TopBarAction.FontSize);
            TopBarMoreCheckBox.IsChecked = settings.TopBar.Actions.Contains(TopBarAction.More);
            var device = await _host.BackupServices!.DeviceSettings.ReadAsync();
            BackupDirectoryText.Text = device.Status == DeviceSettingsReadResultStatus.Found
                && device.Settings?.BackupDirectory is { } directory
                ? directory
                : "未配置";
            var hotkeys = device.Status == DeviceSettingsReadResultStatus.Found
                ? device.Settings?.Hotkeys ?? HotkeySettings.Default
                : HotkeySettings.Default;
            NewHotkeyBox.Text = hotkeys.NewNote ?? string.Empty;
            ManagementHotkeyBox.Text = hotkeys.OpenManagementCenter ?? string.Empty;
            ToggleHotkeyBox.Text = hotkeys.ToggleGlobalHidden ?? string.Empty;
        }
        catch (Exception exception)
        {
            ShowError("读取设置失败", exception);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = await _host.EditingServices!.Settings.GetAsync();
            if (!int.TryParse(DefaultFontSizeBox.Text.Trim(), out var fontSize))
            {
                ErrorText.Text = "字号必须是数字。";
                return;
            }

            var actions = new List<TopBarAction>();
            if (TopBarPinCheckBox.IsChecked == true) actions.Add(TopBarAction.Pin);
            if (TopBarCategoryCheckBox.IsChecked == true) actions.Add(TopBarAction.Category);
            if (TopBarColorCheckBox.IsChecked == true) actions.Add(TopBarAction.Color);
            if (TopBarFontSizeCheckBox.IsChecked == true) actions.Add(TopBarAction.FontSize);
            if (TopBarMoreCheckBox.IsChecked == true) actions.Add(TopBarAction.More);
            var updated = current with
            {
                DefaultFontSize = fontSize,
                NotificationsEnabled = NotificationsCheckBox.IsChecked == true,
                GlobalHidden = GlobalHiddenCheckBox.IsChecked == true,
                TopBar = new TopBarConfig { Actions = actions },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            updated.Validate();
            if (!TryReadHotkeys(out var hotkeys))
            {
                return;
            }

            var startupEnabled = StartupCheckBox.IsChecked == true;
            var startup = _host.StartupRegistration;
            if (startup is not null)
            {
                var registration = startup.SetEnabled(startupEnabled);
                if (registration.Registered != startupEnabled)
                {
                    ErrorText.Text = registration.ErrorCode is null
                        ? "开机启动注册状态未生效。"
                        : $"开机启动注册失败：{registration.ErrorCode}";
                    return;
                }
            }

            if (_host.DeviceSettings is { } deviceStore)
            {
                var device = await deviceStore.ReadAsync();
                var currentDevice = device.Status == DeviceSettingsReadResultStatus.Found
                    ? device.Settings!
                    : new DeviceSettings();
                await deviceStore.WriteAsync(currentDevice with
                {
                    StartupRegistrationEnabled = startupEnabled,
                    Hotkeys = hotkeys
                });
            }

            await _host.EditingServices.Settings.UpdateAsync(updated, current.Version);
            ErrorText.Text = "设置已保存";
            if (System.Windows.Application.Current is App app)
            {
                await app.NotifySettingsChangedAsync();
            }
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception is SettingsConcurrencyException
                ? "设置已被其他窗口修改，请关闭后重新打开。"
                : "保存设置失败。";
            if (exception is not SettingsConcurrencyException)
            {
                _host.Log.TryWrite("ERROR", "settings", "settings save failed", exception.GetType().Name);
            }
        }
    }

    private bool TryReadHotkeys(out HotkeySettings hotkeys)
    {
        hotkeys = new HotkeySettings();
        var values = new[]
        {
            (Name: "新建便签", Text: NewHotkeyBox.Text),
            (Name: "管理中心", Text: ManagementHotkeyBox.Text),
            (Name: "全局隐藏/恢复", Text: ToggleHotkeyBox.Text)
        };
        var parsed = new List<(string Name, string? Gesture)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in values)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                parsed.Add((name, null));
                continue;
            }

            if (!HotkeyGesture.TryParse(text, out var gesture, out var error))
            {
                ErrorText.Text = $"{name}快捷键无效：{error}";
                return false;
            }

            if (!used.Add(gesture!.Display))
            {
                ErrorText.Text = $"{name}快捷键与其他快捷键重复。";
                return false;
            }

            parsed.Add((name, gesture.Display));
        }

        hotkeys = new HotkeySettings
        {
            NewNote = parsed[0].Gesture,
            OpenManagementCenter = parsed[1].Gesture,
            ToggleGlobalHidden = parsed[2].Gesture
        };
        return true;
    }

    private async void BackupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.BackupServices is null)
        {
            return;
        }

        var window = new BackupWindow(_host, _owner)
        {
            Owner = this
        };
        window.ShowDialog();
        await ReloadAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string title, Exception exception)
    {
        _host.Log.TryWrite("ERROR", "settings", title, exception.GetType().Name);
        ErrorText.Text = title;
    }
}
