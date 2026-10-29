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
    private bool _backupSetupShown;
    private IReadOnlyList<Category> _categories = Array.Empty<Category>();
    private IReadOnlyList<Tag> _tags = Array.Empty<Tag>();
    private NavKind _navKind = NavKind.Active;
    private ManagementUndoToken? _lastUndoToken;
    private CancellationTokenSource? _searchDebounce;
    private Guid? _categoryFilterId;

    public WorkspaceWindow(DesktopApplicationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _services = host.EditingServices
            ?? throw new InvalidOperationException("Editing services are not available.");
        InitializeComponent();
        BackupButton.IsEnabled = _host.BackupServices is not null;
        _suppressSelection = true;
        NavigationList.ItemsSource = new[]
        {
            new NavItem("全部便签", NavKind.Active),
            new NavItem("已归档", NavKind.Archived),
            new NavItem("回收站", NavKind.Trash)
        };
        NavigationList.SelectedIndex = 0;
        _suppressSelection = false;
        Loaded += WorkspaceWindow_Loaded;
    }

    private async void WorkspaceWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadCatalogsAsync();
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
            else if (_navKind == NavKind.Active)
            {
                await StartNewAsync();
            }

            await ShowBackupSetupIfNeededAsync();
            await UpdateBackupStatusAsync();
        }
        catch (Exception exception)
        {
            ShowError("工作区加载失败", exception);
        }
    }

    private async Task LoadCatalogsAsync()
    {
        var categories = await _services.Catalog.ListCategoriesAsync();
        var tags = await _services.Catalog.ListTagsAsync();
        _categories = categories;
        _tags = tags;
        var filterItems = new[] { new CategoryFilterItem(null, "全部分类") }
            .Concat(categories.Select(category => new CategoryFilterItem(category.Id, category.Name)))
            .ToArray();
        _loadingEditor = true;
        _suppressSelection = true;
        CategoryFilterCombo.ItemsSource = filterItems;
        CategoryFilterCombo.SelectedItem = _categoryFilterId is null
            ? filterItems[0]
            : filterItems.FirstOrDefault(item => item.Id == _categoryFilterId) ?? filterItems[0];
        CategoryEditorCombo.ItemsSource = categories;
        BatchCategoryCombo.ItemsSource = categories;
        TagFilterList.ItemsSource = tags;
        TagsEditorList.ItemsSource = tags;
        BatchTagCombo.ItemsSource = tags;
        _suppressSelection = false;
        _loadingEditor = false;
        SyncEditorCatalogSelections();
    }

    private void SyncEditorCatalogSelections()
    {
        if (_session is null)
        {
            return;
        }

        _loadingEditor = true;
        CategoryEditorCombo.SelectedItem = _categories
            .FirstOrDefault(category => category.Id == _session.CategoryId);
        TagsEditorList.SelectedItems.Clear();
        foreach (var tagId in _session.TagIds)
        {
            var tag = _tags.FirstOrDefault(candidate => candidate.Id == tagId);
            if (tag is not null)
            {
                TagsEditorList.SelectedItems.Add(tag);
            }
        }

        PinCheckBox.IsChecked = _session.IsPinned;
        _loadingEditor = false;
    }

    private NoteQuery BuildQuery() => new()
    {
        Lifecycle = _navKind switch
        {
            NavKind.Archived => NoteLifecycle.Archived,
            NavKind.Trash => NoteLifecycle.Trash,
            _ => IncludeArchivedCheckBox.IsChecked == true ? null : NoteLifecycle.Active
        },
        IncludeArchived = IncludeArchivedCheckBox.IsChecked == true,
        CategoryId = (CategoryFilterCombo.SelectedItem as CategoryFilterItem)?.Id,
        AnyTagIds = TagFilterList.SelectedItems
            .OfType<Tag>()
            .Select(tag => tag.Id)
            .ToArray(),
        SearchText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(),
        Sort = NoteSortOrder.ModifiedDescending
    };

    private async Task RefreshNotesAsync(bool clearMissingSession = false)
    {
        try
        {
            var notes = await _services.Management.QueryAsync(BuildQuery());
            var items = notes.Select(ToItem).ToArray();
            var selectedId = _session?.NoteId;
            _suppressSelection = true;
            NotesList.ItemsSource = items;
            var selected = selectedId is null
                ? null
                : items.FirstOrDefault(item => item.Id == selectedId);
            NotesList.SelectedItem = selected;
            _suppressSelection = false;
            if (clearMissingSession && _session is not null && selected is null)
            {
                await DisposeCurrentSessionAsync();
                TitleBox.Text = string.Empty;
                BodyBox.Text = string.Empty;
            }

            UpdateSelectionText();
            UpdateButtons();
        }
        catch (Exception exception)
        {
            ShowError("刷新便签列表失败", exception);
        }
    }

    private async Task DisposeCurrentSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        _session.StatusChanged -= Session_StatusChanged;
        try
        {
            await _session.DisposeAsync();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "workspace", "editor session dispose failed", exception.GetType().Name);
        }
        finally
        {
            _session = null;
        }
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
        CategoryEditorCombo.SelectedItem = _categories
            .FirstOrDefault(category => category.Id == session.CategoryId);
        PinCheckBox.IsChecked = session.IsPinned;
        TagsEditorList.SelectedItems.Clear();
        foreach (var tagId in session.TagIds)
        {
            var tag = _tags.FirstOrDefault(candidate => candidate.Id == tagId);
            if (tag is not null)
            {
                TagsEditorList.SelectedItems.Add(tag);
            }
        }

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

        UpdateSelectionText();
        UpdateButtons();
        var selected = NotesList.SelectedItems.OfType<NoteListItem>().ToArray();
        if (selected.Length != 1 || _session?.NoteId == selected[0].Id)
        {
            return;
        }

        await OpenNoteAsync(selected[0].Id);
    }

    private void UpdateSelectionText()
    {
        var count = NotesList.SelectedItems.Count;
        SelectionText.Text = count switch
        {
            0 => "未选择便签",
            1 => "已选择 1 张便签",
            _ => $"已选择 {count} 张便签"
        };
    }

    private void UpdateButtons()
    {
        var selected = NotesList.SelectedItems.OfType<NoteListItem>().ToArray();
        var hasSelection = selected.Length > 0;
        var single = selected.Length == 1;
        ArchiveButton.IsEnabled = !_restoreFrozen && _navKind == NavKind.Active && single;
        TrashButton.IsEnabled = !_restoreFrozen && _navKind == NavKind.Active && single;
        RestoreButton.IsEnabled = !_restoreFrozen && _navKind == NavKind.Trash && single;
        DeleteButton.IsEnabled = !_restoreFrozen && _navKind == NavKind.Trash && single;
        CopyButton.IsEnabled = !_restoreFrozen && single;
        UndoButton.IsEnabled = !_restoreFrozen && _lastUndoToken is not null;
        BatchArchiveButton.IsEnabled = !_restoreFrozen && _navKind == NavKind.Active && hasSelection;
        BatchTrashButton.IsEnabled = !_restoreFrozen && _navKind == NavKind.Active && hasSelection;
        BatchMoveCategoryButton.IsEnabled = !_restoreFrozen && hasSelection;
        BatchAddTagButton.IsEnabled = !_restoreFrozen && hasSelection;
        BatchRemoveTagButton.IsEnabled = !_restoreFrozen && hasSelection;
        ManageCategoriesButton.IsEnabled = !_restoreFrozen;
        ManageTagsButton.IsEnabled = !_restoreFrozen;
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
            _loadingEditor = true;
            CategoryEditorCombo.SelectedItem = null;
            TagsEditorList.SelectedItems.Clear();
            PinCheckBox.IsChecked = false;
            _loadingEditor = false;
            UpdateButtons();
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
        UpdateButtons();
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

    private async void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || NavigationList.SelectedItem is not NavItem item)
        {
            return;
        }

        _navKind = item.Kind;
        await RefreshNotesAsync(clearMissingSession: true);
        await SelectFirstAndOpenAsync();
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        var source = new CancellationTokenSource();
        _searchDebounce = source;
        var text = SearchBox.Text;
        try
        {
            await Task.Delay(250, source.Token);
            if (!source.IsCancellationRequested && string.Equals(text, SearchBox.Text, StringComparison.Ordinal))
            {
                await RefreshNotesAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void IncludeArchivedCheckBox_Click(object sender, RoutedEventArgs e) =>
        await RefreshNotesAsync();

    private async void CategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || !IsLoaded)
        {
            return;
        }

        _categoryFilterId = (CategoryFilterCombo.SelectedItem as CategoryFilterItem)?.Id;
        await RefreshNotesAsync();
    }

    private async void TagFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || !IsLoaded)
        {
            return;
        }

        await RefreshNotesAsync();
    }

    private void CategoryEditorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _session is null || CategoryEditorCombo.SelectedItem is not Category category)
        {
            return;
        }

        _session.ChangeCategory(category.Id);
        UpdateStatus();
    }

    private void TagsEditorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _session is null)
        {
            return;
        }

        _session.ChangeTags(
            TagsEditorList.SelectedItems.OfType<Tag>().Select(tag => tag.Id).ToArray());
        UpdateStatus();
    }

    private void PinCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _session is null)
        {
            return;
        }

        _session.ChangePin(PinCheckBox.IsChecked == true);
        UpdateStatus();
    }

    private async Task SelectFirstAndOpenAsync()
    {
        if (NotesList.Items.Count == 0)
        {
            return;
        }

        _suppressSelection = true;
        NotesList.SelectedIndex = 0;
        _suppressSelection = false;
        if (NotesList.SelectedItem is NoteListItem item)
        {
            await OpenNoteAsync(item.Id);
        }
    }

    private async Task SelectNoteAsync(Guid noteId)
    {
        var item = NotesList.Items.OfType<NoteListItem>().FirstOrDefault(candidate => candidate.Id == noteId);
        if (item is null)
        {
            return;
        }

        _suppressSelection = true;
        NotesList.SelectedItem = item;
        _suppressSelection = false;
        await OpenNoteAsync(noteId);
    }

    private async Task<bool> SaveCurrentBeforeManagementAsync()
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
            _host.Log.TryWrite("ERROR", "workspace", "management save failed", exception.GetType().Name);
        }

        MessageBox.Show(
            this,
            "当前便签未能保存，无法执行管理操作。请先处理保存失败、冲突或未知结果。",
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private Guid[] SelectedNoteIds() =>
        NotesList.SelectedItems
            .OfType<NoteListItem>()
            .Select(item => item.Id)
            .Distinct()
            .ToArray();

    private async Task RefreshAfterManagementAsync(bool clearMissingSession = true)
    {
        await LoadCatalogsAsync();
        await RefreshNotesAsync(clearMissingSession);
        if (NotesList.Items.Count > 0 && NotesList.SelectedItems.Count == 0)
        {
            _suppressSelection = true;
            NotesList.SelectedIndex = 0;
            _suppressSelection = false;
        }

        UpdateSelectionText();
        UpdateButtons();
    }

    private void ShowBatchResult(BatchOperationResult result)
    {
        if (!result.HasFailures)
        {
            return;
        }

        var failures = result.Items
            .Where(item => item.Status != NoteManagementItemStatus.Succeeded)
            .Select(item => $"{item.NoteId}: {item.Status} ({item.ErrorCode})");
        MessageBox.Show(
            this,
            "部分操作未完成：\n" + string.Join("\n", failures),
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task CaptureUndoAsync(ManagementUndoKind kind, IEnumerable<Guid> ids)
    {
        try
        {
            _lastUndoToken = await _services.Undo.CaptureAsync(kind, ids.ToArray());
        }
        catch (Exception exception)
        {
            _lastUndoToken = null;
            _host.Log.TryWrite("WARN", "workspace", "undo capture failed", exception.GetType().Name);
        }
    }

    private async void ArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        var noteId = _session.NoteId;
        var version = _session.Status.BaseVersion;
        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Lifecycle, new[] { noteId });
            await _services.Management.ArchiveAsync(noteId, version);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("归档失败", exception);
        }
    }

    private async void TrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        var noteId = _session.NoteId;
        var version = _session.Status.BaseVersion;
        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Lifecycle, new[] { noteId });
            await _services.Management.MoveToTrashAsync(noteId, version);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("移入回收站失败", exception);
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        var noteId = _session.NoteId;
        var version = _session.Status.BaseVersion;
        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Lifecycle, new[] { noteId });
            await _services.Management.RestoreFromTrashAsync(noteId, version);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("恢复便签失败", exception);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _navKind != NavKind.Trash)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "永久删除后无法恢复。是否继续？",
            "QuickNotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var noteId = _session.NoteId;
        var version = _session.Status.BaseVersion;
        try
        {
            await DisposeCurrentSessionAsync();
            await _services.Management.DeletePermanentlyAsync(noteId, version);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("永久删除失败", exception);
        }
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        try
        {
            var copy = await _services.Management.CreateCopyAsync(_session.NoteId);
            _lastUndoToken = await _services.Undo.CaptureAsync(
                ManagementUndoKind.Copy,
                new[] { copy.Id });
            await RefreshAfterManagementAsync(clearMissingSession: false);
            await SelectNoteAsync(copy.Id);
        }
        catch (Exception exception)
        {
            ShowError("创建副本失败", exception);
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUndoToken is null)
        {
            return;
        }

        try
        {
            var result = await _services.Undo.TryUndoAsync(_lastUndoToken);
            if (result.Items.Any(item => item.Status == ManagementUndoItemStatus.NeedsConfirmation))
            {
                var answer = MessageBox.Show(
                    this,
                    "副本已被修改。撤销将把副本移入回收站，是否继续？",
                    "QuickNotes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    result = await _services.Undo.TryUndoAsync(_lastUndoToken, allowModifiedCopy: true);
                }
            }

            if (result.AllSucceeded)
            {
                _lastUndoToken = null;
            }
            else
            {
                var failures = result.Items
                    .Where(item => item.Status != ManagementUndoItemStatus.Succeeded)
                    .Select(item => $"{item.NoteId}: {item.Status} ({item.Message})");
                MessageBox.Show(
                    this,
                    "撤销未完全完成：\n" + string.Join("\n", failures),
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("撤销失败", exception);
        }
    }

    private async void BatchArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedNoteIds();
        if (ids.Length == 0 || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Lifecycle, ids);
            var result = await _services.Management.ArchiveAsync(ids);
            ShowBatchResult(result);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("批量归档失败", exception);
        }
    }

    private async void BatchTrashButton_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedNoteIds();
        if (ids.Length == 0 || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Lifecycle, ids);
            var result = await _services.Management.MoveToTrashAsync(ids);
            ShowBatchResult(result);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("批量移入回收站失败", exception);
        }
    }

    private async void BatchMoveCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (BatchCategoryCombo.SelectedItem is not Category category)
        {
            MessageBox.Show(this, "请选择批量目标分类。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ids = SelectedNoteIds();
        if (ids.Length == 0 || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Category, ids);
            var result = await _services.Management.MoveToCategoryAsync(ids, category.Id);
            ShowBatchResult(result);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("批量移动分类失败", exception);
        }
    }

    private async void BatchAddTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (BatchTagCombo.SelectedItem is not Tag tag)
        {
            MessageBox.Show(this, "请选择要添加的标签。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ids = SelectedNoteIds();
        if (ids.Length == 0 || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Tags, ids);
            var result = await _services.Management.AddTagsAsync(ids, new[] { tag.Id });
            ShowBatchResult(result);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("批量添加标签失败", exception);
        }
    }

    private async void BatchRemoveTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (BatchTagCombo.SelectedItem is not Tag tag)
        {
            MessageBox.Show(this, "请选择要移除的标签。", "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ids = SelectedNoteIds();
        if (ids.Length == 0 || !await SaveCurrentBeforeManagementAsync())
        {
            return;
        }

        try
        {
            await CaptureUndoAsync(ManagementUndoKind.Tags, ids);
            var result = await _services.Management.RemoveTagsAsync(ids, new[] { tag.Id });
            ShowBatchResult(result);
            await RefreshAfterManagementAsync();
        }
        catch (Exception exception)
        {
            ShowError("批量移除标签失败", exception);
        }
    }

    private async void ManageCategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new CategoryManagerWindow(_services.Catalog)
        {
            Owner = this
        };
        window.ShowDialog();
        await RefreshAfterManagementAsync(clearMissingSession: false);
    }

    private async void ManageTagsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new TagManagerWindow(_services.Catalog)
        {
            Owner = this
        };
        window.ShowDialog();
        await RefreshAfterManagementAsync(clearMissingSession: false);
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

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_restoreFrozen)
        {
            return;
        }

        if (_host.BackupServices is null)
        {
            MessageBox.Show(
                this,
                "备份服务尚未就绪。",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var window = new BackupWindow(_host, this)
        {
            Owner = this
        };
        window.ShowDialog();
        await UpdateBackupStatusAsync();
    }

    private async Task ShowBackupSetupIfNeededAsync()
    {
        if (_backupSetupShown
            || _host.BackupServices is null
            || _restoreFrozen)
        {
            return;
        }

        try
        {
            var status = await _host.BackupServices.Host.GetStatusAsync();
            if (status.AutoBackupConfigured)
            {
                return;
            }

            _backupSetupShown = true;
            var window = new BackupSetupWindow(_host)
            {
                Owner = this
            };
            window.Closed += async (_, _) => await UpdateBackupStatusAsync();
            window.Show();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("WARN", "workspace", "backup setup could not be opened", exception.GetType().Name);
        }
    }

    private async Task UpdateBackupStatusAsync()
    {
        if (_host.BackupServices is null)
        {
            BackupStatusText.Text = string.Empty;
            return;
        }

        try
        {
            var status = await _host.BackupServices.Host.GetStatusAsync();
            BackupStatusText.Text = status.AutoBackupConfigured
                ? "自动备份已启用"
                : "备份未配置";
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("WARN", "workspace", "backup status could not be read", exception.GetType().Name);
            BackupStatusText.Text = "备份状态未知";
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
        CategoryEditorCombo.IsEnabled = !readOnly;
        TagsEditorList.IsEnabled = !readOnly;
        PinCheckBox.IsEnabled = !readOnly;
        NewButton.IsEnabled = !readOnly;
        SaveButton.IsEnabled = !readOnly;
        RefreshButton.IsEnabled = !readOnly;
        DraftsButton.IsEnabled = !readOnly;
        BackupButton.IsEnabled = !readOnly && _host.BackupServices is not null;
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

public enum NavKind
{
    Active,
    Archived,
    Trash
}

public sealed record NavItem(string Display, NavKind Kind);

public sealed record CategoryFilterItem(Guid? Id, string Name);

