using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WindowsWindowTrayPrototype;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly List<NoteWindow> _notes = new();
    private readonly HashSet<NoteWindow> _notesHiddenByGlobalToggle = new();
    private readonly List<HotkeySetting> _hotkeySettings = new();
    private readonly Dictionary<int, HotkeySetting> _registeredHotkeys = new();
    private WinForms.NotifyIcon? _trayIcon;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _noteCounter;
    private bool _allowExit;
    private bool _globalHidden;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        InitializeDefaultHotkeys();
        InitializeTrayIcon();
        Log("原型启动。请依次验证窗口行为、托盘行为和快捷键。");
        Log("默认快捷键：Ctrl+Alt+N 新建；Ctrl+Alt+M 管理中心；Ctrl+Alt+H 全局隐藏/恢复。");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WindowMessageHook);
        RegisterConfiguredHotkeys();
    }

    private void InitializeDefaultHotkeys()
    {
        _hotkeySettings.Add(new HotkeySetting(1, "新建便签", "Ctrl+Alt", "N", true));
        _hotkeySettings.Add(new HotkeySetting(2, "打开管理中心", "Ctrl+Alt", "M", true));
        _hotkeySettings.Add(new HotkeySetting(3, "全局隐藏／恢复", "Ctrl+Alt", "H", true));
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("新建便签", null, (_, _) => Dispatcher.Invoke(CreateNote));
        menu.Items.Add("全局隐藏／恢复", null, (_, _) => Dispatcher.Invoke(ToggleGlobalHide));
        menu.Items.Add("主管理窗口", null, (_, _) => Dispatcher.Invoke(ShowManager));
        menu.Items.Add("快捷键设置", null, (_, _) => Dispatcher.Invoke(OpenHotkeySettings));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出软件", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "桌面便签窗口验证原型",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                Dispatcher.Invoke(ShowManager);
        };
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => CreateNote();

    private void CreateTwenty_Click(object sender, RoutedEventArgs e)
    {
        for (var i = 0; i < 20; i++)
            CreateNote();
        Log("已创建 20 张测试便签。请检查任务栏、Alt+Tab、重叠和窗口响应。");
    }

    private void CreateNote()
    {
        _noteCounter++;
        var note = new NoteWindow(_noteCounter, Log);
        var workArea = SystemParameters.WorkArea;
        var offset = ((_noteCounter - 1) % 10) * 28;
        note.Left = Math.Min(workArea.Right - 300, workArea.Left + 60 + offset);
        note.Top = Math.Min(workArea.Bottom - 220, workArea.Top + 60 + offset);
        _notes.Add(note);
        NotesList.Items.Add($"便签 {_noteCounter}");
        NotesList.SelectedIndex = NotesList.Items.Count - 1;
        note.Show();
        Log($"创建便签 {_noteCounter}；ShowInTaskbar=false，独立窗口不设置 Owner。");
    }

    private NoteWindow? GetSelectedNote()
    {
        var index = NotesList.SelectedIndex;
        return index >= 0 && index < _notes.Count ? _notes[index] : null;
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        _globalHidden = false;
        _notesHiddenByGlobalToggle.Clear();
        foreach (var note in _notes)
        {
            if (!note.IsVisible)
                note.Show();
        }
        Log("已执行“显示全部”（包括此前单张隐藏的测试窗口）。");
    }

    private void ToggleGlobalHide_Click(object sender, RoutedEventArgs e) => ToggleGlobalHide();

    private void ToggleGlobalHide()
    {
        if (!_globalHidden)
        {
            _notesHiddenByGlobalToggle.Clear();
            foreach (var note in _notes.Where(n => n.IsVisible))
            {
                _notesHiddenByGlobalToggle.Add(note);
                note.Hide();
            }
            _globalHidden = true;
            Log($"全局隐藏：记录并隐藏了 {_notesHiddenByGlobalToggle.Count} 张原本可见的便签。");
        }
        else
        {
            var restore = _notesHiddenByGlobalToggle.ToArray();
            foreach (var note in restore)
            {
                if (!note.IsVisible)
                    note.Show();
            }
            _notesHiddenByGlobalToggle.Clear();
            _globalHidden = false;
            Log($"全局恢复：恢复了原本应显示的 {restore.Length} 张便签。");
        }
    }

    private void LocateSelected_Click(object sender, RoutedEventArgs e)
    {
        var note = GetSelectedNote();
        if (note is null)
        {
            Log("请先在列表中选择一张便签。");
            return;
        }
        if (!note.IsVisible)
            note.Show();
        if (note.WindowState == WindowState.Minimized)
            note.WindowState = WindowState.Normal;
        note.Activate();
        Log($"已定位并激活：{note.Title}。");
    }

    private void HideSelected_Click(object sender, RoutedEventArgs e)
    {
        var note = GetSelectedNote();
        if (note is null)
        {
            Log("请先在列表中选择一张便签。");
            return;
        }
        note.Hide();
        Log($"已单独隐藏：{note.Title}。");
    }

    private void ToggleTopmostSelected_Click(object sender, RoutedEventArgs e)
    {
        var note = GetSelectedNote();
        if (note is null)
        {
            Log("请先在列表中选择一张便签。");
            return;
        }
        note.Topmost = !note.Topmost;
        note.SetTopmostCheck(note.Topmost);
        Log($"{note.Title} 置顶状态：{note.Topmost}。");
    }

    private void Hotkeys_Click(object sender, RoutedEventArgs e) => OpenHotkeySettings();

    private void OpenHotkeySettings()
    {
        var dialog = new HotkeySettingsWindow(_hotkeySettings.Select(x => x.Clone()).ToList())
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            UnregisterConfiguredHotkeys();
            _hotkeySettings.Clear();
            _hotkeySettings.AddRange(dialog.ResultSettings);
            RegisterConfiguredHotkeys();
            Log("已应用快捷键配置（本原型只在当前进程中保存设置，不写入磁盘）。");
        }
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        Log("主管理窗口已隐藏到托盘；进程和便签窗口继续运行。");
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit)
            return;

        e.Cancel = true;
        var dialog = new CloseChoiceWindow { Owner = this };
        dialog.ShowDialog();

        switch (dialog.Choice)
        {
            case CloseChoice.Exit:
                _allowExit = true;
                UnregisterConfiguredHotkeys();
                foreach (var note in _notes.ToArray())
                    note.ForceClose();
                if (_trayIcon is not null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                Log("已通过关闭三选项选择真正退出。");
                e.Cancel = false;
                break;
            case CloseChoice.HideToTray:
                Hide();
                Log("通过关闭三选项将主管理窗口缩小到托盘。");
                break;
            default:
                Log("已取消关闭操作。");
                Activate();
                break;
        }
    }

    private void ExitApplication()
    {
        if (_allowExit)
            return;

        _allowExit = true;
        UnregisterConfiguredHotkeys();
        foreach (var note in _notes.ToArray())
            note.ForceClose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Log("已请求真正退出软件。");
        Close();
    }

    private void ShowManager()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Log("主管理窗口已激活。");
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (_registeredHotkeys.TryGetValue(id, out var setting))
            {
                handled = true;
                switch (setting.Id)
                {
                    case 1:
                        CreateNote();
                        break;
                    case 2:
                        ShowManager();
                        break;
                    case 3:
                        ToggleGlobalHide();
                        break;
                }
            }
        }
        return IntPtr.Zero;
    }

    private void RegisterConfiguredHotkeys()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var failures = new List<string>();
        foreach (var setting in _hotkeySettings.Where(s => s.Enabled))
        {
            try
            {
                var modifiers = ParseModifiers(setting.Modifiers) | ModNoRepeat;
                var key = Enum.Parse<Key>(setting.Key, ignoreCase: true);
                var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (RegisterHotKey(_hwnd, setting.Id, modifiers, virtualKey))
                {
                    _registeredHotkeys[setting.Id] = setting;
                    Log($"快捷键已注册：{setting.Action} = {setting.Modifiers}+{setting.Key}");
                }
                else
                {
                    var error = Marshal.GetLastWin32Error();
                    var message = $"{setting.Action}：{setting.Modifiers}+{setting.Key}（Win32 错误码 {error}）";
                    failures.Add(message);
                    Log($"快捷键注册失败：{message}");
                }
            }
            catch (Exception ex)
            {
                var message = $"{setting.Action}：{ex.Message}";
                failures.Add(message);
                Log($"快捷键配置无效：{message}");
            }
        }

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                "以下快捷键未能注册，请修改组合或禁用对应快捷键：\n\n" + string.Join("\n", failures),
                "快捷键注册失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UnregisterConfiguredHotkeys()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _hotkeySettings.Select(s => s.Id))
                UnregisterHotKey(_hwnd, id);
        }
        _registeredHotkeys.Clear();
    }

    private static uint ParseModifiers(string modifiers)
    {
        uint value = 0;
        foreach (var part in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            value |= part switch
            {
                "Ctrl" => ModControl,
                "Alt" => ModAlt,
                "Shift" => ModShift,
                "Win" => ModWin,
                _ => throw new ArgumentException($"未知修饰键：{part}")
            };
        }
        return value;
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
