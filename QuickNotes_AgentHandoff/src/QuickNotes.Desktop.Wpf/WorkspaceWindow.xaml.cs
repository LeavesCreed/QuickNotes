using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Wpf;

public partial class WorkspaceWindow : Window
{
    private readonly DesktopApplicationHost _host;
    private readonly DesktopEditingServices _services;
    private NoteEditorSession? _session;
    private bool _loadingEditor;
    private bool _suppressSelection;
    private bool _allowClose;
    private bool _closing;
    private bool _restoreFrozen;
    private Guid? _restoreNoteId;

    public WorkspaceWindow(DesktopApplicationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _services = host.EditingServices
            ?? throw new InvalidOperationException("Editing services are not available.");
        InitializeComponent();
        Loaded += WorkspaceWindow_Loaded;
    }

    private async void WorkspaceWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshNotesAsync();
            if (NotesList.Items.Count > 0)
            {
                _suppressSelection = true;
                NotesList.SelectedIndex = 0;
                _suppressSelection = false;
                if (NotesList.SelectedItem is NoteListItem item)
                {
                    await OpenNoteAsync(item.Id);
                }
            }
            else
            {
                await StartNewAsync();
            }
        }
        catch (Exception exception)
        {
            ShowError("工作区加载失败", exception);
        }
    }

    private async Task RefreshNotesAsync()
    {
        var notes = await _services.Workspace.ListActiveAsync();
        var items = notes.Select(ToItem).ToArray();
        var selectedId = _session?.NoteId;
        _suppressSelection = true;
        NotesList.ItemsSource = items;
        NotesList.SelectedItem = selectedId is null
            ? null
            : items.FirstOrDefault(item => item.Id == selectedId);
        _suppressSelection = false;
    }

    private async Task StartNewAsync()
    {
        if (!await TryPrepareCurrentForSwitchAsync())
        {
            return;
        }

        try
        {
            var session = await _services.Workspace.CreateNewAsync();
            await BindSessionAsync(session);
            _suppressSelection = true;
            NotesList.SelectedItem = null;
            _suppressSelection = false;
            UpdateStatus();
        }
        catch (Exception exception)
        {
            ShowError("新建便签失败", exception);
        }
    }

    private async Task OpenNoteAsync(Guid noteId)
    {
        if (_session?.NoteId == noteId)
        {
            return;
        }

        if (!await TryPrepareCurrentForSwitchAsync())
        {
            SelectSessionItem();
            return;
        }

        try
        {
            var session = await _services.Workspace.OpenAsync(noteId);
            if (session is null)
            {
                MessageBox.Show(this, "该便签已不存在。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshNotesAsync();
                SelectSessionItem();
                return;
            }

            await BindSessionAsync(session);
        }
        catch (Exception exception)
        {
            ShowError("打开便签失败", exception);
            SelectSessionItem();
        }
    }

    private async Task BindSessionAsync(NoteEditorSession session)
    {
        if (_session is not null)
        {
            _session.StatusChanged -= Session_StatusChanged;
            try
            {
                await _session.DisposeAsync();
            }
            catch (Exception exception)
            {
                _host.Log.TryWrite("ERROR", "workspace", "editor session dispose failed", exception.GetType().Name);
            }
        }

        _session = session;
        _session.StatusChanged += Session_StatusChanged;
        _loadingEditor = true;
        TitleBox.Text = session.Title ?? string.Empty;
        BodyBox.Text = session.Body;
        _loadingEditor = false;
        SelectSessionItem();
        UpdateStatus();
    }

    private async Task<bool> TryPrepareCurrentForSwitchAsync()
    {
        if (_session is null || !_session.HasPendingChanges)
        {
            return true;
        }

        try
        {
            await _session.FlushDraftAsync();
            var result = await _session.SaveNowAsync();
            if (result.Status == SaveStatus.Saved && !_session.HasPendingChanges)
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "workspace", "switch save failed", exception.GetType().Name);
        }

        MessageBox.Show(
            this,
            "当前便签未能保存。草稿已保留；请先处理保存失败/冲突/未知结果，或取消切换。",
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private void SelectSessionItem()
    {
        if (_session is null || NotesList.ItemsSource is not IEnumerable<NoteListItem> items)
        {
            return;
        }

        _suppressSelection = true;
        NotesList.SelectedItem = items.FirstOrDefault(item => item.Id == _session.NoteId);
        _suppressSelection = false;
    }

    private void Session_StatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateStatus);
    }

    private async void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _loadingEditor)
        {
            return;
        }

        if (NotesList.SelectedItem is not NoteListItem item || _session?.NoteId == item.Id)
        {
            return;
        }

        await OpenNoteAsync(item.Id);
    }

    private void UpdateStatus()
    {
        if (_session is null)
        {
            StatusText.Text = "无编辑会话";
            DraftStatusText.Text = string.Empty;
            ErrorText.Text = string.Empty;
            ConflictPanel.Visibility = Visibility.Collapsed;
            RetrySaveButton.Visibility = Visibility.Collapsed;
            RetryDraftButton.Visibility = Visibility.Collapsed;
            return;
        }

        var status = _session.Status;
        StatusText.Text = status.IsNew
            ? $"新便签 · {SaveStateText(status.SaveState)}"
            : $"{SaveStateText(status.SaveState)} · v{status.BaseVersion}";
        DraftStatusText.Text = status.DraftState switch
        {
            DraftProtectionState.Protected => "草稿：已保护",
            DraftProtectionState.Pending => "草稿：等待落盘",
            DraftProtectionState.Failed => "草稿：保护失败",
            _ => "草稿：无需保护"
        };
        ErrorText.Text = status.Message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(ErrorText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ConflictPanel.Visibility = _session.Conflict is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_session.Conflict is { } conflict)
        {
            ConflictText.Text = conflict.RemoteNote is null
                ? "数据库版本已不存在。"
                : $"数据库版本 v{conflict.RemoteVersion}: {conflict.RemoteNote.Title ?? "(无标题)"}";
            ConflictPreview.Text = conflict.RemoteNote?.Body ?? string.Empty;
        }

        RetrySaveButton.Visibility = status.SaveState == EditorSaveState.OutcomeUnknown
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryDraftButton.Visibility = status.DraftState == DraftProtectionState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveButton.IsEnabled = !_closing && !_restoreFrozen && status.SaveState != EditorSaveState.Saving;
    }

    private static string SaveStateText(EditorSaveState state) => state switch
    {
        EditorSaveState.Clean => "已保存",
        EditorSaveState.Modified => "已修改",
        EditorSaveState.Drafting => "草稿保护中",
        EditorSaveState.Saving => "保存中…",
        EditorSaveState.Saved => "已保存",
        EditorSaveState.SaveFailed => "保存失败",
        EditorSaveState.Conflict => "版本冲突",
        EditorSaveState.OutcomeUnknown => "保存结果未知",
        _ => state.ToString()
    };

    private void EditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _restoreFrozen || _session is null)
        {
            return;
        }

        _session.ChangeText(TitleBox.Text, BodyBox.Text);
        UpdateStatus();
    }

    private async void EditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loadingEditor
            || _closing
            || _restoreFrozen
            || _session is null
            || !_session.HasPendingChanges)
        {
            return;
        }

        var focused = Keyboard.FocusedElement;
        if (ReferenceEquals(focused, TitleBox) || ReferenceEquals(focused, BodyBox))
        {
            return;
        }

        try
        {
            await _session.SaveNowAsync();
            UpdateStatus();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "workspace", "focus loss save failed", exception.GetType().Name);
        }
    }

    private async void NewButton_Click(object sender, RoutedEventArgs e) => await StartNewAsync();

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var result = await _session.SaveNowAsync();
            if (result.Status == SaveStatus.Saved)
            {
                await RefreshNotesAsync();
            }

            UpdateStatus();
        }
        catch (Exception exception)
        {
            ShowError("保存失败", exception);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshNotesAsync();
            SelectSessionItem();
            UpdateStatus();
        }
        catch (Exception exception)
        {
            ShowError("刷新列表失败", exception);
        }
    }

    private async void DraftsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is not null)
            {
                await _session.FlushDraftAsync();
            }

            var dialog = new DraftRecoveryWindow(_services.DraftRecovery)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true && dialog.RestoredSession is { } restored)
            {
                if (!await TryPrepareCurrentForSwitchAsync())
                {
                    await restored.DisposeAsync();
                    return;
                }

                await BindSessionAsync(restored);
                await RefreshNotesAsync();
                SelectSessionItem();
                UpdateStatus();
            }
        }
        catch (Exception exception)
        {
            ShowError("草稿恢复失败", exception);
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "将放弃本地当前版本并重新载入数据库版本。是否继续？",
            "QuickNotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _session.ReloadAsync();
            await RefreshNotesAsync();
            UpdateStatus();
        }
        catch (Exception exception)
        {
            ShowError("重新载入失败", exception);
        }
    }

    private async void SaveAsNewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var newSession = await _session.SaveAsNewAsync();
            await BindSessionAsync(newSession);
            await RefreshNotesAsync();
            SelectSessionItem();
            UpdateStatus();
            if (newSession.Status.SaveState != EditorSaveState.Saved)
            {
                MessageBox.Show(
                    this,
                    "另存为新便签未完成；本地内容与草稿仍保留。",
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowError("另存为新便签失败", exception);
        }
    }

    private async void RetrySaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var resolution = await _session.RetryAsync();
            UpdateStatus();
            if (resolution.Status == EditorSaveResolutionStatus.Saved)
            {
                await RefreshNotesAsync();
            }
        }
        catch (Exception exception)
        {
            ShowError("重试保存失败", exception);
        }
    }

    private async void RetryDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.FlushDraftAsync();
            UpdateStatus();
        }
        catch (Exception exception)
        {
            ShowError("草稿保护重试失败", exception);
        }
    }

    public async Task<bool> PrepareForRestoreAsync()
    {
        if (_restoreFrozen)
        {
            return false;
        }

        if (_session is null)
        {
            return true;
        }

        _restoreNoteId = _session.NoteId;
        _restoreFrozen = true;
        SetEditorReadOnly(true);
        try
        {
            await _session.FlushDraftAsync();
            if (_session.HasPendingChanges)
            {
                var result = await _session.SaveNowAsync();
                if (result.Status != SaveStatus.Saved || _session.HasPendingChanges)
                {
                    MessageBox.Show(
                        this,
                        "恢复前无法保存当前便签。请先处理保存失败、冲突或未知结果，或取消恢复。",
                        "QuickNotes",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    await UnfreezeAfterRestoreFailureAsync();
                    return false;
                }
            }

            _session.Invalidate();
            UpdateStatus();
            return true;
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "workspace", "restore preparation failed", exception.GetType().Name);
            MessageBox.Show(
                this,
                "恢复前无法完成草稿或保存保护；恢复已取消。",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await UnfreezeAfterRestoreFailureAsync();
            return false;
        }
    }

    public async Task UnfreezeAfterRestoreFailureAsync()
    {
        _restoreFrozen = false;
        SetEditorReadOnly(false);
        if (_session is { IsInvalidated: true } && _restoreNoteId is { } noteId)
        {
            try
            {
                var reopened = await _services.Workspace.OpenAsync(noteId);
                if (reopened is not null)
                {
                    await BindSessionAsync(reopened);
                }
                else
                {
                    await BindSessionAsync(await _services.Workspace.CreateNewAsync());
                }
            }
            catch (Exception exception)
            {
                ShowError("恢复失败后重新打开便签失败", exception);
            }
        }

        _restoreNoteId = null;
        UpdateStatus();
    }

    public void CloseAfterRestore()
    {
        _allowClose = true;
        Close();
    }

    private void SetEditorReadOnly(bool readOnly)
    {
        TitleBox.IsReadOnly = readOnly;
        BodyBox.IsReadOnly = readOnly;
        NewButton.IsEnabled = !readOnly;
        SaveButton.IsEnabled = !readOnly;
        RefreshButton.IsEnabled = !readOnly;
        DraftsButton.IsEnabled = !readOnly;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (_session is null || !_session.HasPendingChanges)
        {
            _allowClose = true;
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _ = CloseFlowAsync();
    }

    private async Task CloseFlowAsync()
    {
        try
        {
            await _session!.FlushDraftAsync();
            SaveResult? result = null;
            try
            {
                result = await _session.SaveNowAsync();
            }
            catch (Exception exception)
            {
                _host.Log.TryWrite("ERROR", "workspace", "close save failed", exception.GetType().Name);
            }

            if (result?.Status == SaveStatus.Saved && !_session.HasPendingChanges)
            {
                _allowClose = true;
                Close();
                return;
            }

            var answer = MessageBox.Show(
                this,
                "当前便签未能保存。草稿已保留；是否保留草稿并退出？",
                "QuickNotes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                _allowClose = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            ShowError("退出前保存失败", exception);
        }
        finally
        {
            _closing = false;
            UpdateStatus();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
        }

        base.OnClosed(e);
    }

    private void ShowError(string title, Exception exception)
    {
        _host.Log.TryWrite("ERROR", "workspace", title, exception.GetType().Name);
        MessageBox.Show(
            this,
            $"{title}。请查看诊断日志。",
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static NoteListItem ToItem(Note note) => new(
        note.Id,
        string.IsNullOrWhiteSpace(note.Title) ? "(无标题)" : note.Title,
        Preview(note.Body),
        note.ModifiedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"));

    private static string Preview(string body)
    {
        var compact = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 80 ? compact : compact[..80] + "…";
    }
}

public sealed record NoteListItem(Guid Id, string TitleDisplay, string PreviewDisplay, string ModifiedDisplay);

