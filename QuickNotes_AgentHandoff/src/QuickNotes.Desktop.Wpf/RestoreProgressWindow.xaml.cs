using System.Windows;
using QuickNotes.Application;

namespace QuickNotes.Desktop.Wpf;

public partial class RestoreProgressWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    public RestoreProgressWindow()
    {
        InitializeComponent();
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Report(RestoreProgress progress)
    {
        if (!IsVisible)
        {
            return;
        }

        StageText.Text = StageTextFor(progress.Stage, progress.Message);
        CancelButton.IsEnabled = progress.IsCancelable;
        HintText.Text = progress.IsCancelable
            ? "切换数据库前的阶段可以取消。"
            : "数据库正在切换或校验，当前阶段不可取消。";
    }

    public void Finish(string message)
    {
        StageText.Text = message;
        CancelButton.IsEnabled = false;
        _finished = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = "正在请求取消…";
        HintText.Text = "将等待当前阶段安全结束。";
        _cancellation.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Dispose();
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private static string StageTextFor(RestoreProgressStage stage, string message) => stage switch
    {
        RestoreProgressStage.Preflight => "预检备份…",
        RestoreProgressStage.ProtectionSnapshot => "创建保护快照…",
        RestoreProgressStage.Staging => "准备恢复暂存数据…",
        RestoreProgressStage.Migrating => "准备当前 schema…",
        RestoreProgressStage.Switching => "切换数据库…",
        RestoreProgressStage.Verifying => "校验恢复结果…",
        RestoreProgressStage.Completed => "恢复完成。",
        RestoreProgressStage.Failed => "恢复失败。",
        RestoreProgressStage.RolledBack => "恢复失败，已回退。",
        _ => message
    };
}
