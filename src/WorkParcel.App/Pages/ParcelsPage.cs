using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Windows.System;

namespace WorkParcel_App.Pages;

public sealed partial class ParcelsPage : PageBase
{
    private readonly StackPanel _rows = Ui.Stack(2);
    private readonly TextBox _search = new() { PlaceholderText = "Search name or description", MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _filter = new() { MinWidth = 110, ItemsSource = new[] { "All", "Open", "Packed" }, SelectedIndex = 0 };
    private readonly ComboBox _sort = new() { MinWidth = 155, ItemsSource = new[] { "Recently used", "Recently updated", "Name", "Date created" }, SelectedIndex = 0 };
    private string _filterText = "All";
    private int _searchVersion;

    public ParcelsPage()
    {
        Ui.Interactive(_search, 1.005f); Ui.Interactive(_filter, 1.01f); Ui.Interactive(_sort, 1.01f);
        var clearFilters = Ui.Button("CLEAR FILTERS"); clearFilters.Click += (_, _) => { _search.Text = string.Empty; _filter.SelectedIndex = 0; _sort.SelectedIndex = 0; };
        var localStatus = Ui.Mono("LOCAL / READY", 10, "#9BE28F", true);
        localStatus.VerticalAlignment = VerticalAlignment.Center;

        var body = Body(18);
        body.Children.Add(BuildHeader());
        body.Children.Add(BuildFilters(clearFilters, localStatus));
        body.Children.Add(Ui.Rule());
        body.Children.Add(_rows);
        _search.TextChanged += (_, _) => QueueSearchRefresh();
        _filter.SelectionChanged += (_, _) => { _filterText = _filter.SelectedItem?.ToString() ?? "All"; Refresh(); };
        _sort.SelectionChanged += (_, _) => Refresh();
        SetContent(body);
        Refresh();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private async void QueueSearchRefresh()
    {
        var version = ++_searchVersion; await Task.Delay(180); if (version == _searchVersion) Refresh();
    }

    private Grid BuildHeader()
    {
        var header = new Grid { ColumnSpacing = 24 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = Ui.Stack(4);
        copy.Children.Add(Ui.Text("PARCELS", 26, true));
        copy.Children.Add(Ui.Text("Save a setup. Open it whenever you return.", 12, false, "#8D9CA2"));
        header.Children.Add(copy);

        var newParcel = Ui.Button("+ NEW PARCEL  \u25BE");
        newParcel.HorizontalAlignment = HorizontalAlignment.Right;
        newParcel.VerticalAlignment = VerticalAlignment.Top;
        newParcel.Padding = new Thickness(13, 8, 13, 8);
        AutomationProperties.SetName(newParcel, "New parcel menu");
        ToolTipService.SetToolTip(newParcel, "Choose how to create a parcel");
        newParcel.Flyout = BuildNewParcelFlyout();
        Grid.SetColumn(newParcel, 1);
        header.Children.Add(newParcel);
        return header;
    }

    private Flyout BuildNewParcelFlyout()
    {
        var capture = MenuOption("CAPTURE CURRENT SETUP", "Select what is currently open", true);
        var create = MenuOption("CREATE EMPTY PARCEL", "Start with a blank parcel", false);
        var options = Ui.Stack(4);
        options.Children.Add(capture);
        options.Children.Add(create);

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            Content = new Border
            {
                Width = 300,
                Padding = new Thickness(5),
                Background = Ui.Resource("SurfaceBrush"),
                BorderBrush = Ui.Resource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = options
            }
        };

        capture.Click += (_, _) => { flyout.Hide(); Frame?.Navigate(typeof(CapturePage)); };
        create.Click += async (_, _) => { flyout.Hide(); await CreateEmptyAsync(); };
        capture.KeyDown += (sender, args) => MoveMenuFocus(args, (Button)sender, capture, create, flyout);
        create.KeyDown += (sender, args) => MoveMenuFocus(args, (Button)sender, capture, create, flyout);
        flyout.Opened += (_, _) => capture.Focus(FocusState.Programmatic);
        return flyout;
    }

    private static Button MenuOption(string title, string subtitle, bool recommended)
    {
        var copy = Ui.Stack(2);
        copy.Children.Add(Ui.Mono(title, 11, recommended ? "#9BE28F" : null, true));
        copy.Children.Add(Ui.Text(subtitle, 11, false, "#8D9CA2"));

        var marker = new Border
        {
            Width = 3,
            Background = recommended ? Ui.Resource("AccentBrush") : Ui.Resource("BorderBrush"),
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 1, 8, 1)
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(marker);
        content.Children.Add(copy);

        var option = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = recommended ? Ui.Resource("AccentSoftBrush") : Ui.Resource("SurfaceBrush"),
            BorderBrush = recommended ? Ui.Resource("AccentBrush") : Ui.Resource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 9, 10, 9),
            UseSystemFocusVisuals = true
        };
        AutomationProperties.SetName(option, $"{title}. {subtitle}{(recommended ? ". Recommended" : string.Empty)}");
        return Ui.Interactive(option, 1.015f);
    }

    private static void MoveMenuFocus(KeyRoutedEventArgs args, Button current, Button first, Button last, Flyout flyout)
    {
        if (args.Key is VirtualKey.Down or VirtualKey.Up)
        {
            var next = current == first ? last : first;
            next.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            flyout.Hide();
            args.Handled = true;
        }
        else if (args.Key is VirtualKey.Home or VirtualKey.End)
        {
            (args.Key == VirtualKey.Home ? first : last).Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }

    private Grid BuildFilters(Button clearFilters, TextBlock localStatus)
    {
        var filters = new Grid { ColumnSpacing = 9, RowSpacing = 9 };
        filters.Children.Add(_search);
        filters.Children.Add(_filter);
        filters.Children.Add(_sort);
        filters.Children.Add(clearFilters);
        filters.Children.Add(localStatus);
        filters.SizeChanged += (_, args) => ArrangeFilters(filters, clearFilters, localStatus, args.NewSize.Width);
        ArrangeFilters(filters, clearFilters, localStatus, 900);
        return filters;
    }

    private void ArrangeFilters(Grid grid, Button clearFilters, TextBlock localStatus, double width)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        if (width >= 760)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(_search, 0, 0); Place(_filter, 0, 1); Place(_sort, 0, 2); Place(clearFilters, 0, 3); Place(localStatus, 0, 5);
        }
        else if (width >= 520)
        {
            for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(_search, 0, 0, 2); Place(localStatus, 0, 2); Place(_filter, 1, 0); Place(_sort, 1, 1); Place(clearFilters, 1, 2);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(_search, 0, 0, 2); Place(_filter, 1, 0); Place(_sort, 1, 1); Place(clearFilters, 2, 0); Place(localStatus, 2, 1);
        }
    }

    private static void Place(FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private async Task CreateEmptyAsync()
    {
        var result = await Dialogs.ShowNewParcelAsync(this, (name, description) => Store.CreateEmptyAsync(name, description));
        if (result is null) return;
        if (result.Captured) { Frame?.Navigate(typeof(CapturePage), new CaptureSeed(result.Name, result.Description)); return; }
        if (result.Parcel is not null) { Refresh(); Frame?.Navigate(typeof(ParcelDetailsPage), result.Parcel.Id); await Dialogs.ShowMessage(this, "PARCEL CREATED", "The empty parcel is ready. It contains 0 items."); }
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        var status = _filterText switch { "Open" => ParcelStatus.Open, "Packed" => ParcelStatus.Packed, _ => (ParcelStatus?)null };
        var sort = _sort.SelectedIndex switch { 1 => ParcelSort.RecentlyUpdated, 2 => ParcelSort.Name, 3 => ParcelSort.DateCreated, _ => ParcelSort.RecentlyUsed };
        var list = ParcelQueries.Apply(Store.Parcels, new ParcelQueryOptions(_search.Text, status, sort));
        if (list.Count == 0)
        {
            var empty = Ui.Stack(9); empty.Children.Add(Ui.Mono(string.IsNullOrWhiteSpace(_search.Text) && _filterText == "All" ? "NO PARCELS YET" : "NO MATCHES", 12, "#9BE28F", true)); empty.Children.Add(Ui.Text(string.IsNullOrWhiteSpace(_search.Text) && _filterText == "All" ? "Capture open windows or create a blank parcel to begin." : "Try a different search or clear the filters.", 13, false, "#8D9CA2"));
            var clear = Ui.Button("CLEAR FILTERS"); clear.Click += (_, _) => { _search.Text = string.Empty; _filter.SelectedIndex = 0; _sort.SelectedIndex = 0; }; empty.Children.Add(clear); _rows.Children.Add(Ui.Card(empty, 22)); return;
        }
        foreach (var parcel in list) _rows.Children.Add(Row(parcel));
    }

    private Border Row(Parcel parcel)
    {
        var copy = Ui.Stack(3); copy.Children.Add(Ui.Text(parcel.Name, 14, true)); copy.Children.Add(Ui.Text(string.IsNullOrWhiteSpace(parcel.Description) ? "No description" : parcel.Description, 11, false, "#8D9CA2"));
        var meta = Ui.Stack(3); meta.Children.Add(Ui.Mono(parcel.ItemSummary, 10, "#9BE28F", true)); meta.Children.Add(Ui.Mono($"UPDATED {Ui.Relative(parcel.UpdatedAt)}", 10));
        var action = Ui.Button(parcel.Status == ParcelStatus.Open ? "PACK AWAY" : "OPEN PARCEL", parcel.Status == ParcelStatus.Open); action.Click += (_, _) => Frame?.Navigate(typeof(ParcelDetailsPage), new ParcelDetailsRequest(parcel.Id, true));
        var more = Ui.IconButton("…", "Parcel actions"); var menu = new MenuFlyout();
        var openDetails = new MenuFlyoutItem { Text = "Open details" }; openDetails.Click += (_, _) => Frame?.Navigate(typeof(ParcelDetailsPage), parcel.Id);
        var edit = new MenuFlyoutItem { Text = "Edit parcel" }; edit.Click += async (_, _) => { await Dialogs.ShowEditParcelAsync(this, parcel); Refresh(); };
        var archive = new MenuFlyoutItem { Text = "Archive" }; archive.Click += async (_, _) => { if (await Dialogs.Confirm(this, "ARCHIVE PARCEL", "Move this saved record to Archive?", "ARCHIVE")) { try { await Store.ArchiveAsync(parcel); Refresh(); } catch (Exception exception) { Services.AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "ARCHIVE FAILED", "The parcel was not changed."); } } };
        menu.Items.Add(openDetails); menu.Items.Add(edit); menu.Items.Add(archive); more.Flyout = menu;
        var row = Ui.Row(copy, meta, Ui.Spacer(), Ui.Tag(parcel.StatusText, Ui.StateColor(parcel.Status)), action, more); row.VerticalAlignment = VerticalAlignment.Center;
        var border = new Border { Child = row, Padding = new Thickness(10, 8, 8, 8), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Resource("SurfaceBrush") };
        border.Tapped += (_, _) => Frame?.Navigate(typeof(ParcelDetailsPage), parcel.Id);
        return Ui.Interactive(border);
    }
}
