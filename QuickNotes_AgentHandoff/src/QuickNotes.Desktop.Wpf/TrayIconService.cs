using Forms = System.Windows.Forms;

namespace QuickNotes.Desktop.Wpf;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _toggleGlobalHiddenItem;
    private bool _disposed;

    public TrayIconService(
        Action createNote,
        Action showWorkspace,
        Action toggleGlobalHidden,
        Action showSettings,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(createNote);
        ArgumentNullException.ThrowIfNull(showWorkspace);
        ArgumentNullException.ThrowIfNull(toggleGlobalHidden);
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(exit);

        _menu = new Forms.ContextMenuStrip();
        var createItem = new Forms.ToolStripMenuItem("新建便签");
        createItem.Click += (_, _) => createNote();
        var workspaceItem = new Forms.ToolStripMenuItem("管理中心");
        workspaceItem.Click += (_, _) => showWorkspace();
        _toggleGlobalHiddenItem = new Forms.ToolStripMenuItem("全局隐藏便签");
        _toggleGlobalHiddenItem.Click += (_, _) => toggleGlobalHidden();
        var settingsItem = new Forms.ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => showSettings();
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => exit();
        _menu.Items.Add(createItem);
        _menu.Items.Add(workspaceItem);
        _menu.Items.Add(_toggleGlobalHiddenItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "QuickNotes",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => showWorkspace();
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                showWorkspace();
            }
        };
    }

    public void SetGlobalHidden(bool hidden)
    {
        _toggleGlobalHiddenItem.Text = hidden ? "恢复桌面便签" : "全局隐藏便签";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
