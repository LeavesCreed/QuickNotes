using System.Windows;
using QuickNotes.Desktop.Editing;

namespace QuickNotes.Desktop.Wpf;

public partial class DraftRecoveryWindow : Window
{
    private readonly DraftRecoveryService _service;
    private IReadOnlyList<DraftRecoveryItem> _items = Array.Empty<DraftRecoveryItem>();
    private bool _loading;

    public DraftRecoveryWindow(DraftRecoveryService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        Loaded += DraftRecoveryWindow_Loaded;
    }

    public NoteEditorSession? RestoredSession { get; private set; }

    private async void DraftRecoveryWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var inventory = await _service.ListAsync();
            _items = inventory.Items;
            DraftsList.ItemsSource = _items.Select(FormatItem).ToArray();
            IssuesText.Text = inventory.Issues.Count == 0
                ? string.Empty
                : string.Join(
                    Environment.NewLine,
                    inventory.Issues.Select(issue => $"{issue.FileName}: {issue.ErrorCode}"));
            IssuesText.Visibility = inventory.Issues.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            DraftPreview.Text = string.Empty;
            DraftsList.SelectedIndex = _items.Count > 0 ? 0 : -1;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"草稿列表读取失败：{exception.GetType().Name}",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            UpdateButtons();
        }
    }

    private static string FormatItem(DraftRecoveryItem item)
    {
        var title = string.IsNullOrWhiteSpace(item.Draft.Title) ? "(无标题)" : item.Draft.Title;
        var source = item.NoteExists ? "已有便签" : "新便签草稿";
        return $"{title} · {item.Draft.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {source}";
    }

    private DraftRecoveryItem? SelectedItem =>
        DraftsList.SelectedIndex >= 0 && DraftsList.SelectedIndex < _items.Count
            ? _items[DraftsList.SelectedIndex]
            : null;

    private void UpdateButtons()
    {
        var hasSelection = SelectedItem is not null && !_loading;
        RestoreButton.IsEnabled = hasSelection;
        DiscardButton.IsEnabled = hasSelection;
    }

    private void DraftsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var item = SelectedItem;
        DraftPreview.Text = item is null
            ? string.Empty
            : $"{item.Draft.Title ?? "(无标题)"}{Environment.NewLine}{Environment.NewLine}{item.Draft.Body}";
        UpdateButtons();
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        try
        {
            RestoredSession = await _service.OpenAsync(item.Draft);
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"草稿恢复失败：{exception.GetType().Name}",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "丢弃后无法恢复该草稿。是否继续？",
            "QuickNotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _service.DiscardAsync(item.Draft.DraftId);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"草稿丢弃失败：{exception.GetType().Name}",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
