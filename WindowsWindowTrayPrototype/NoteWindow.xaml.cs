using System;
using System.ComponentModel;
using System.Windows;

namespace WindowsWindowTrayPrototype;

public partial class NoteWindow : Window
{
    private readonly Action<string> _log;
    private bool _allowClose;

    public NoteWindow(int number, Action<string> log)
    {
        InitializeComponent();
        _log = log;
        Title = $"便签 {number}";
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
        _log($"{Title} 置顶状态变为 {Topmost}。");
    }

    public void SetTopmostCheck(bool value)
    {
        TopmostCheck.IsChecked = value;
    }

    private void NoteWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
        _log($"{Title} 的 X 被解释为隐藏，而不是删除或退出。");
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}
