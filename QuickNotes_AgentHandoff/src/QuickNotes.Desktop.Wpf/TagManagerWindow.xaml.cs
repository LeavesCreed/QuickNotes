using System.Windows;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Wpf;

public partial class TagManagerWindow : Window
{
    private readonly ICatalogRepository _catalog;
    private IReadOnlyList<Tag> _tags = Array.Empty<Tag>();

    public TagManagerWindow(ICatalogRepository catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _tags = await _catalog.ListTagsAsync();
        TagList.ItemsSource = _tags;
        MergeTargetCombo.ItemsSource = _tags;
        MergeTargetCombo.SelectedItem = _tags.FirstOrDefault();
        TagList.SelectedItem = _tags.FirstOrDefault();
    }

    private void TagList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TagList.SelectedItem is Tag tag)
        {
            NameBox.Text = tag.Name;
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("标签名称不能为空。");
            return;
        }

        try
        {
            await _catalog.CreateTagAsync(new Tag { Id = Guid.NewGuid(), Name = name });
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is not Tag tag)
        {
            return;
        }

        try
        {
            await _catalog.UpdateTagAsync(tag.Id, NameBox.Text.Trim());
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is not Tag source
            || MergeTargetCombo.SelectedItem is not Tag target
            || source.Id == target.Id)
        {
            ShowError("请选择不同的源标签和合并目标。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"把标签“{source.Name}”合并到“{target.Name}”？关联便签会保留并改到目标标签。",
            "QuickNotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _catalog.MergeTagAsync(source.Id, target.Id);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is Tag tag)
        {
            var answer = MessageBox.Show(
                this,
                $"删除标签“{tag.Name}”？只解除关联，不删除便签。",
                "QuickNotes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    await _catalog.DeleteTagAsync(tag.Id);
                    await ReloadAsync();
                    return;
                }
                catch (Exception exception)
                {
                    ShowError(exception);
                    return;
                }
            }
        }

        Close();
    }

    private void ShowError(Exception exception) =>
        MessageBox.Show(this, exception.Message, "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
}
