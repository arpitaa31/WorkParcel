using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WorkParcel_App.Pages;
using WorkParcel_App.Services;

namespace WorkParcel_App;

public sealed partial class MainPage : Page
{
    private readonly WorkspaceStore _store = WorkspaceStore.Current;

    public MainPage()
    {
        InitializeComponent();
        Navigation.SelectionChanged -= Navigation_SelectionChanged;
        Navigation.SelectionChanged += Navigation_SelectionChanged;
        _store.PropertyChanged += Store_PropertyChanged;
        Loaded += MainPage_Loaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        Navigation.SelectionChanged -= Navigation_SelectionChanged;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Navigation.SelectionChanged += Navigation_SelectionChanged;
        Navigate("parcels");
    }

    private void Store_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceStore.Theme))
            RequestedTheme = _store.Theme == "Dark" ? ElementTheme.Dark : _store.Theme == "Light" ? ElementTheme.Light : ElementTheme.Default;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag) Navigate(tag);
    }

    private void Navigate(string tag)
    {
        var type = tag switch
        {
            "today" => typeof(TodayPage),
            "archive" => typeof(ArchivePage),
            "settings" => typeof(SettingsPage),
            "capture" => typeof(CapturePage),
            _ => typeof(ParcelsPage)
        };
        if (ContentFrame.CurrentSourcePageType != type) ContentFrame.Navigate(type);
    }

}
