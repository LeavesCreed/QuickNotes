using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QuickNotes.Application;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Wpf;

public partial class NoteWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private readonly DesktopApplicationHost _host;
    private readonly NoteEditorSession _session;
    private readonly WindowPlacementService _placement;
    private bool _syncing;
    private bool _allowClose;
    private bool _savingOnHide;
    private bool _layoutReady;
    private bool _applyingPlacement;
    private CancellationTokenSource? _layoutDebounce;
    private ApplicationSettings? _settings;
    private IReadOnlyList<Category> _categories = Array.Empty<Category>();

    public NoteWindow(
        DesktopApplicationHost host,
        NoteEditorSession session,
        WindowPlacementService? placement = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _placement = placement ?? new WindowPlacementService();
        InitializeComponent();
        _session.StatusChanged += Session_StatusChanged;
        LocationChanged += NoteWindow_LayoutChanged;
        SizeChanged += NoteWindow_LayoutChanged;
        Loaded += NoteWindow_Loaded;
        BuildColorMenu();
        BuildFontSizeMenu();
    }

    public Guid NoteId => _session.NoteId;

    public NoteEditorSession Session => _session;

    public void ApplyPlacement(
        NoteWindowLayout? layout,
        string? preferredDisplayId,
        int cascadeIndex = 0)
    {
        _applyingPlacement = true;
        try
        {
            _placement.ApplyToWindow(this, layout, preferredDisplayId, cascadeIndex);
        }
        finally
        {
            Dispatcher.BeginInvoke(() => _applyingPlacement = false);
        }
    }

    public event Action<NoteWindow>? HideRequested;

    public async Task RefreshAsync()
    {
        _settings = await _host.EditingServices!.Settings.GetAsync();
        _categories = await _host.EditingServices.Catalog.ListCategoriesAsync();
        var actions = _settings.TopBar.Actions;
        PinButton.Visibility = actions.Contains(TopBarAction.Pin)
            ? Visibility.Visible
            : Visibility.Collapsed;
        MoreButton.Visibility = actions.Contains(TopBarAction.More)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ColorMenu.Visibility = actions.Contains(TopBarAction.Color)
            ? Visibility.Visible
            : Visibility.Collapsed;
        FontSizeMenu.Visibility = actions.Contains(TopBarAction.FontSize)
            ? Visibility.Visible
            : Visibility.Collapsed;
        await ApplySessionStateAsync();
    }

    public async Task ActivateAsync()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        await Task.CompletedTask;
    }

    public void CloseFromManager()
    {
        _allowClose = true;
        Close();
    }

    private async void NoteWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _layoutReady = true;
        try
        {
            await RefreshAsync();
            UpdateStatus();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "note_window", "note window load failed", exception.GetType().Name);
        }
    }

    private void NoteWindow_LayoutChanged(object? sender, EventArgs e)
    {
        if (!_layoutReady || _applyingPlacement || _allowClose)
        {
            return;
        }

        _layoutDebounce?.Cancel();
        _layoutDebounce?.Dispose();
        var source = new CancellationTokenSource();
        _layoutDebounce = source;
        _ = SaveLayoutAfterDelayAsync(source.Token);
    }

    private async Task SaveLayoutAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _session.ChangeLayout(new NoteWindowLayout
            {
                Left = Left,
                Top = Top,
                Width = Width,
                Height = Height
            });
            await _session.SaveNowAsync();
            await SaveDisplayBindingAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("WARN", "note_window", "layout save failed", exception.GetType().Name);
        }
    }

    private async Task SaveDisplayBindingAsync()
    {
        if (_host.DeviceSettings is not { } store)
        {
            return;
        }

        var displayId = _placement.GetDisplayId(this);
        if (string.IsNullOrWhiteSpace(displayId))
        {
            return;
        }

        var read = await store.ReadAsync();
        var current = read.Status == QuickNotes.Application.DeviceSettingsReadResultStatus.Found
            ? read.Settings
            : null;
        var settings = current ?? new DeviceSettings();
        if (settings.DisplayBindings.TryGetValue(_session.NoteId, out var existing)
            && string.Equals(existing, displayId, StringComparison.Ordinal))
        {
            return;
        }

        var bindings = new Dictionary<Guid, string>(settings.DisplayBindings)
        {
            [_session.NoteId] = displayId
        };
        await store.WriteAsync(settings with { DisplayBindings = bindings });
    }

    private void Session_StatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_syncing)
            {
                _ = ApplySessionStateAsync();
            }

            UpdateStatus();
        });
    }

    private async Task ApplySessionStateAsync()
    {
        if (_syncing || _session.IsDisposed)
        {
            return;
        }

        _syncing = true;
        try
        {
            var title = _session.Title ?? string.Empty;
            if (!string.Equals(TitleBox.Text, title, StringComparison.Ordinal))
            {
                TitleBox.Text = title;
            }

            if (!string.Equals(BodyBox.Text, _session.Body, StringComparison.Ordinal))
            {
                BodyBox.Text = _session.Body;
            }

            var fontSize = _session.FontSize
                ?? _settings?.DefaultFontSize
                ?? 16;
            TitleBox.FontSize = Math.Max(12, fontSize);
            BodyBox.FontSize = Math.Max(12, fontSize);
            TopBarTitle.Text = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title;
            PinButton.IsChecked = _session.IsPinned;
            Topmost = _session.IsPinned;
            await ApplyBackgroundAsync();
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task ApplyBackgroundAsync()
    {
        string? colorHex = null;
        if (_session.ColorMode == NoteColorMode.Manual)
        {
            colorHex = _session.ColorHex;
        }
        else
        {
            var category = _categories.FirstOrDefault(candidate => candidate.Id == _session.CategoryId);
            if (category is null)
            {
                category = await _host.EditingServices!.Catalog.GetCategoryAsync(_session.CategoryId);
            }

            colorHex = category?.DefaultColor;
        }

        try
        {
            RootBorder.Background = string.IsNullOrWhiteSpace(colorHex)
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xDC))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }
        catch (FormatException)
        {
            RootBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xDC));
        }
    }

    private void UpdateStatus()
    {
        var status = _session.Status;
        SaveStatusText.Text = status.SaveState switch
        {
            EditorSaveState.Clean => "已保存",
            EditorSaveState.Saved => "已保存",
            EditorSaveState.Modified => "已修改",
            EditorSaveState.Drafting => "草稿保护中",
            EditorSaveState.Saving => "保存中…",
            EditorSaveState.SaveFailed => "保存失败",
            EditorSaveState.Conflict => "版本冲突",
            EditorSaveState.OutcomeUnknown => "保存结果未知",
            _ => string.Empty
        };
        StatusText.Text = status.Message ?? string.Empty;
        StatusText.ToolTip = status.Message;
    }

    private void EditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _session.IsDisposed)
        {
            return;
        }

        _session.ChangeText(TitleBox.Text, BodyBox.Text);
    }

    private async void EditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
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
            _host.Log.TryWrite("ERROR", "note_window", "focus loss save failed", exception.GetType().Name);
        }
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _session.ChangePin(PinButton.IsChecked == true);
        await SaveAsync();
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e) =>
        await HideWithIntentAsync();

    private async void HideMenuItem_Click(object sender, RoutedEventArgs e) =>
        await HideWithIntentAsync();

    private async void OpenManagementMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current.MainWindow is Window workspace)
        {
            workspace.Show();
            if (workspace.WindowState == WindowState.Minimized)
            {
                workspace.WindowState = WindowState.Normal;
            }

            workspace.Activate();
        }

        await Task.CompletedTask;
    }

    private async void ArchiveMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ChangeLifecycleAsync(NoteLifecycle.Archived);

    private async void TrashMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ChangeLifecycleAsync(NoteLifecycle.Trash);

    private async Task ChangeLifecycleAsync(NoteLifecycle lifecycle)
    {
        if (!await SaveBeforeManagementAsync())
        {
            return;
        }

        try
        {
            var management = _host.EditingServices!.Management;
            if (lifecycle == NoteLifecycle.Archived)
            {
                await management.ArchiveAsync(_session.NoteId, _session.Status.BaseVersion);
            }
            else
            {
                await management.MoveToTrashAsync(_session.NoteId, _session.Status.BaseVersion);
            }

            HideRequested?.Invoke(this);
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "note_window", "lifecycle change failed", exception.GetType().Name);
            MessageBox.Show(
                this,
                "便签状态更新失败。请稍后重试。",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task<bool> SaveBeforeManagementAsync()
    {
        if (!_session.HasPendingChanges)
        {
            return true;
        }

        await _session.FlushDraftAsync();
        var result = await _session.SaveNowAsync();
        if (result.Status == SaveStatus.Saved && !_session.HasPendingChanges)
        {
            return true;
        }

        MessageBox.Show(
            this,
            "当前便签未能保存，无法执行此操作。",
            "QuickNotes",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private async Task HideWithIntentAsync()
    {
        if (_savingOnHide)
        {
            return;
        }

        _savingOnHide = true;
        try
        {
            _session.ChangeDisplayIntent(NoteDisplayIntent.Hidden);
            var result = await _session.SaveNowAsync();
            if (result.Status != SaveStatus.Saved || _session.HasPendingChanges)
            {
                MessageBox.Show(
                    this,
                    "隐藏状态未能保存。便签仍保持显示。",
                    "QuickNotes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            HideRequested?.Invoke(this);
            Hide();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "note_window", "hide failed", exception.GetType().Name);
        }
        finally
        {
            _savingOnHide = false;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _session.SaveNowAsync();
            UpdateStatus();
        }
        catch (Exception exception)
        {
            _host.Log.TryWrite("ERROR", "note_window", "save failed", exception.GetType().Name);
        }
    }

    private void BuildColorMenu()
    {
        ColorMenu.Items.Clear();
        AddColorItem("跟随分类", NoteColorMode.FollowCategory, null);
        AddColorItem("黄色", NoteColorMode.Manual, "#FFF3B0");
        AddColorItem("蓝色", NoteColorMode.Manual, "#CFE8FF");
        AddColorItem("粉色", NoteColorMode.Manual, "#FFD6E7");
        AddColorItem("绿色", NoteColorMode.Manual, "#D8F3DC");
    }

    private void AddColorItem(string header, NoteColorMode mode, string? colorHex)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try
            {
                _session.ChangeAppearance(mode, colorHex, _session.FontSize);
                await SaveAsync();
                await ApplySessionStateAsync();
            }
            catch (Exception exception)
            {
                _host.Log.TryWrite("ERROR", "note_window", "color change failed", exception.GetType().Name);
            }
        };
        ColorMenu.Items.Add(item);
    }

    private void BuildFontSizeMenu()
    {
        FontSizeMenu.Items.Clear();
        AddFontSizeItem("跟随全局", null);
        foreach (var size in new[] { 14, 16, 18, 20, 24 })
        {
            AddFontSizeItem($"{size}", size);
        }
    }

    private void AddFontSizeItem(string header, int? size)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try
            {
                _session.ChangeAppearance(_session.ColorMode, _session.ColorHex, size);
                await SaveAsync();
                await ApplySessionStateAsync();
            }
            catch (Exception exception)
            {
                _host.Log.TryWrite("ERROR", "note_window", "font size change failed", exception.GetType().Name);
            }
        };
        FontSizeMenu.Items.Add(item);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            _session.StatusChanged -= Session_StatusChanged;
            _layoutDebounce?.Cancel();
            _layoutDebounce?.Dispose();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = HideWithIntentAsync();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern nint GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern nint SetWindowLong(nint hWnd, int nIndex, nint dwNewLong);
}
