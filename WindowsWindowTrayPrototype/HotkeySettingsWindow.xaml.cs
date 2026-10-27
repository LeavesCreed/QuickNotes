using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WindowsWindowTrayPrototype;

public partial class HotkeySettingsWindow : Window
{
    private static readonly string[] ModifierOptions =
    {
        "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Ctrl+Alt+Shift", "Win+Alt"
    };

    private static readonly string[] KeyOptions =
    {
        "N", "M", "H", "F8", "F9", "F10", "F11", "F12"
    };

    private readonly List<HotkeySetting> _initial;
    public List<HotkeySetting> ResultSettings { get; private set; } = new();

    public HotkeySettingsWindow(List<HotkeySetting> settings)
    {
        InitializeComponent();
        _initial = settings.Select(s => s.Clone()).ToList();

        foreach (var combo in new[] { NewModifiers, ManagerModifiers, GlobalModifiers })
        {
            combo.ItemsSource = ModifierOptions;
        }
        foreach (var combo in new[] { NewKey, ManagerKey, GlobalKey })
        {
            combo.ItemsSource = KeyOptions;
        }

        ApplyToControls(_initial[0], NewEnabled, NewModifiers, NewKey);
        ApplyToControls(_initial[1], ManagerEnabled, ManagerModifiers, ManagerKey);
        ApplyToControls(_initial[2], GlobalEnabled, GlobalModifiers, GlobalKey);
    }

    private static void ApplyToControls(HotkeySetting setting, CheckBox enabled, ComboBox modifiers, ComboBox key)
    {
        enabled.IsChecked = setting.Enabled;
        modifiers.SelectedItem = ModifierOptions.Contains(setting.Modifiers) ? setting.Modifiers : "Ctrl+Alt";
        key.SelectedItem = KeyOptions.Contains(setting.Key) ? setting.Key : "N";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultSettings = new List<HotkeySetting>
        {
            ReadSetting(_initial[0], NewEnabled, NewModifiers, NewKey),
            ReadSetting(_initial[1], ManagerEnabled, ManagerModifiers, ManagerKey),
            ReadSetting(_initial[2], GlobalEnabled, GlobalModifiers, GlobalKey)
        };
        DialogResult = true;
    }

    private static HotkeySetting ReadSetting(HotkeySetting original, CheckBox enabled, ComboBox modifiers, ComboBox key)
    {
        var result = original.Clone();
        result.Enabled = enabled.IsChecked == true;
        result.Modifiers = modifiers.SelectedItem?.ToString() ?? "Ctrl+Alt";
        result.Key = key.SelectedItem?.ToString() ?? "N";
        return result;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
