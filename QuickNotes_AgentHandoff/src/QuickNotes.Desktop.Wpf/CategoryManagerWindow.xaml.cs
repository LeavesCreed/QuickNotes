using System.Windows;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Wpf;

public partial class CategoryManagerWindow : Window
{
    private readonly ICatalogRepository _catalog;
    private IReadOnlyList<Category> _categories = Array.Empty<Category>();

    public CategoryManagerWindow(ICatalogRepository catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _categories = await _catalog.ListCategoriesAsync();
        CategoryList.ItemsSource = _categories;
        DeleteTargetCombo.ItemsSource = _categories;
        DeleteTargetCombo.SelectedItem = _categories.FirstOrDefault(
            category => category.Id == Category.UncategorizedId);
        CategoryList.SelectedItem = _categories.FirstOrDefault();
    }

    private void CategoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not Category category)
        {
            return;
        }

        NameBox.Text = category.Name;
        ColorBox.Text = category.DefaultColor;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var color = ColorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(color))
        {
            ShowError("分类名称和颜色不能为空。");
            return;
        }

        try
        {
            await _catalog.CreateCategoryAsync(new Category
            {
                Id = Guid.NewGuid(),
                Name = name,
                DefaultColor = color
            });
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not Category category)
        {
            return;
        }

        try
        {
            await _catalog.UpdateCategoryAsync(
                category.Id,
                NameBox.Text.Trim(),
                ColorBox.Text.Trim());
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not Category category || category.IsSystem)
        {
            ShowError("系统分类不能删除。");
            return;
        }

        if (DeleteTargetCombo.SelectedItem is not Category target)
        {
            ShowError("请选择删除后的目标分类。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"删除分类“{category.Name}”并把便签移动到“{target.Name}”？",
            "QuickNotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _catalog.DeleteCategoryAsync(category.Id, target.Id);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(Exception exception) =>
        MessageBox.Show(this, exception.Message, "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "QuickNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
}
