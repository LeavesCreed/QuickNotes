using System.Windows;

namespace QuickNotes.Desktop.Wpf;

public enum CloseChoice
{
    Cancel,
    MinimizeToTray,
    ExitApplication
}

public partial class CloseChoiceWindow : Window
{
    public CloseChoiceWindow()
    {
        InitializeComponent();
    }

    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.MinimizeToTray;
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.ExitApplication;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }
}
