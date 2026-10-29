using System.Windows;
using QuickNotes.Application;
using QuickNotes.Desktop.Hosting;
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
            await _host.EditingServices.Settings.UpdateAsync(updated, current.Version);
            ErrorText.Text = "设置已保存";
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
