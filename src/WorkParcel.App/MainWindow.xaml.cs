using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WorkParcel_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(string? startupError = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 720));

        if (string.IsNullOrWhiteSpace(startupError)) RootFrame.Navigate(typeof(MainPage));
        else RootFrame.Content = new Microsoft.UI.Xaml.Controls.StackPanel { Padding = new Microsoft.UI.Xaml.Thickness(48), Spacing = 12, Children = { new Microsoft.UI.Xaml.Controls.TextBlock { Text = "WorkParcel could not start", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new Microsoft.UI.Xaml.Controls.TextBlock { Text = startupError, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, FontSize = 15 } } };
    }
}
