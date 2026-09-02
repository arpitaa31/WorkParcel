using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel.Core.Browser;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WorkParcel_App.Pages;

public sealed record ParcelDetailsRequest(Guid ParcelId, bool StartPrimaryAction = false);

public sealed partial class ParcelDetailsPage : PageBase
{
    private readonly ItemLaunchService _launcher = new();
    private readonly ItemAvailabilityService _availability = new();
    private readonly OpenWindowService _windows = new();
    private readonly DeskMemoryService _desk = new();
    private readonly WindowCloseRequestService _closer = new();
    private readonly WorkspacePickerService _pickers = new();
    private readonly ParcelItemFactory _factory = new();
    private StackPanel _itemsHost = null!;
    private TextBox _search = null!;
    private ComboBox _filter = null!;
    private ComboBox _sort = null!;
    private Parcel? _parcel;
    private readonly HashSet<Guid> _selectedItemIds = new();
    private bool _busy;
    private bool _removalInProgress;
    private int _searchVersion;

    public ParcelDetailsPage() { }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _selectedItemIds.Clear();
        if (e.Parameter is Guid id) _parcel = Store.Parcels.Concat(Store.Archived).FirstOrDefault(parcel => parcel.Id == id);
        else if (e.Parameter is Parcel parcel) _parcel = Store.Parcels.Concat(Store.Archived).FirstOrDefault(item => item.Id == parcel.Id);
        else if (e.Parameter is ParcelDetailsRequest request) _parcel = Store.Parcels.Concat(Store.Archived).FirstOrDefault(item => item.Id == request.ParcelId);
        Build(); if (_parcel is not null) _ = RefreshAvailabilityAsync(false);
        if (e.Parameter is ParcelDetailsRequest { StartPrimaryAction: true })
        {
            RoutedEventHandler? loaded = null; loaded = async (_, _) => { Loaded -= loaded; if (_parcel?.Status == ParcelStatus.Open) await PackAwayAsync(); else await OpenParcelAsync(); }; Loaded += loaded;
        }
    }

    private void Build()
    {
        // The page is refreshed after availability checks and data mutations. Never
        // reparent controls from the old visual tree; WinUI keeps the old tree alive
        // briefly while an async refresh is completing.
        if (Content is not null) Content = null;
        _itemsHost = Ui.Stack(2);
        _search = new TextBox { PlaceholderText = "Search parcel items", MinWidth = 220 };
        _filter = new ComboBox { MinWidth = 145, ItemsSource = new[] { "ALL", "APPS & WINDOWS", "LINKS", "FILES", "FOLDERS", "NOTES", "BROWSER TABS", "CHROME TABS", "EDGE TABS" }, SelectedIndex = 0 };
        _sort = new ComboBox { MinWidth = 130, ItemsSource = new[] { "DATE ADDED", "NAME", "TYPE", "SAVED POSITION" }, SelectedIndex = 0 };
        _search.TextChanged += (_, _) => QueueSearchRefresh();
        _filter.SelectionChanged += (_, _) => RefreshRows();
        _sort.SelectionChanged += (_, _) => RefreshRows();
        Ui.Interactive(_search, 1.004f); Ui.Interactive(_filter, 1.01f); Ui.Interactive(_sort, 1.01f);
        if (_parcel is null) { SetContent(Ui.Card(Ui.Text("Parcel not found.", 14), 20)); return; }
        var body = Body(15);
        body.Children.Add(BuildHeader());
        body.Children.Add(Ui.Rule());
        body.Children.Add(BuildTools());
        body.Children.Add(BuildDeskMemorySection());
        body.Children.Add(DropTarget());
        body.Children.Add(_itemsHost);
        RefreshRows();
        body.Children.Add(Ui.SectionHeader("PARCEL HISTORY", "Meaningful saved changes only.")); if (_parcel.History.Count == 0) body.Children.Add(Ui.Text("No history yet.", 12, false, "#8D9CA2")); foreach (var entry in _parcel.History.OrderByDescending(entry => entry.Timestamp).Take(50)) body.Children.Add(HistoryRow(entry)); SetContent(body);
    }

    private StackPanel BuildHeader()
    {
        var back = Ui.Button("<- BACK"); back.Click += (_, _) => { if (Frame?.CanGoBack == true) Frame.GoBack(); };
        var edit = Ui.Button("EDIT PARCEL"); edit.Click += async (_, _) => { await Dialogs.ShowEditParcelAsync(this, _parcel!); Build(); };
        var update = Ui.Button("UPDATE PARCEL"); update.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel!.Name, _parcel.Description, _parcel.Id));
        var action = Ui.Button(_parcel!.Status == ParcelStatus.Open ? "PACK AWAY" : "OPEN PARCEL", _parcel.Status == ParcelStatus.Open); action.Click += async (_, _) => { if (_parcel.Status == ParcelStatus.Open) await PackAwayAsync(); else await OpenParcelAsync(); };
        var menu = Ui.IconButton("...", "Parcel actions"); var flyout = new MenuFlyout(); var archive = new MenuFlyoutItem { Text = "Archive" }; archive.Click += async (_, _) => { if (await Dialogs.Confirm(this, "ARCHIVE PARCEL", "Move this saved record to Archive?", "ARCHIVE")) { await Store.ArchiveAsync(_parcel); if (Frame?.CanGoBack == true) Frame.GoBack(); } }; var delete = new MenuFlyoutItem { Text = "Delete permanently" }; delete.Click += async (_, _) => { if (await Dialogs.ConfirmPermanentDelete(this, _parcel)) { await Store.DeleteAsync(_parcel); if (Frame?.CanGoBack == true) Frame.GoBack(); } }; flyout.Items.Add(archive); flyout.Items.Add(delete); menu.Flyout = flyout;
        var header = Ui.Stack(10); header.Children.Add(Ui.Row(back, Ui.Spacer(), edit, update, action, menu)); header.Children.Add(Ui.Text(_parcel.Name, 28, true)); if (!string.IsNullOrWhiteSpace(_parcel.Description)) header.Children.Add(Ui.Text(_parcel.Description, 13, false, "#8D9CA2")); header.Children.Add(Ui.Row(Ui.Tag(_parcel.StatusText, Ui.StateColor(_parcel.Status)), Ui.Mono(_parcel.ItemSummary, 10), Ui.Mono($"{_parcel.AvailableItemCount} AVAILABLE", 10, "#9BE28F"), Ui.Mono($"CREATED {_parcel.CreatedAt:g}".ToUpperInvariant(), 10))); return header;
    }

    private Border BuildDeskMemorySection()
    {
        if (_parcel?.DeskLayout is null)
        {
            var empty = Ui.Stack(5);
            empty.Children.Add(Ui.SectionHeader("DESK MEMORY", "Remember the real placement of selected app windows across monitors."));
            empty.Children.Add(Ui.Text("No window layout is saved for this parcel yet. Capture Current Setup to add one.", 12, false, "#8D9CA2"));
            var capture = Ui.Button("CAPTURE CURRENT LAYOUT", true); capture.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel?.Name ?? string.Empty, _parcel?.Description ?? string.Empty, _parcel?.Id)); empty.Children.Add(capture);
            return Ui.Card(empty, 14);
        }

        var layout = _parcel.DeskLayout;
        var stack = Ui.Stack(8);
        stack.Children.Add(Ui.SectionHeader("DESK MEMORY", layout.IsEnabled ? "Window placement is saved locally with this parcel." : "Desk Memory is disabled for this parcel."));
        stack.Children.Add(DeskPreviewBuilder.Summary(layout));
        stack.Children.Add(DeskPreviewBuilder.Build(layout));
        try
        {
            var current = _desk.Provider.GetSnapshot();
            var plan = DeskMemoryLogic.BuildPlan(layout, current);
            var topology = DeskMemoryLogic.CompareTopology(layout.Monitors, current.Monitors);
            var topologyText = topology == DeskTopologyComparison.Exact ? "DISPLAY SETUP MATCHES" : $"DISPLAY SETUP: {topology.ToString().ToUpperInvariant()}";
            stack.Children.Add(Ui.Mono($"{topologyText} · {plan.Items.Count(item => item.Match.IsAutomatic)} READY · {plan.AmbiguousMatches.Count} AMBIGUOUS · {plan.Items.Count(item => item.Match.Live is null && item.Match.Confidence == DeskMatchConfidence.NoMatch)} MISSING", 10, topology == DeskTopologyComparison.Exact ? "#9BE28F" : "#F0B45B", true));
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); stack.Children.Add(Ui.Text("Current monitor status could not be checked. Restore will validate it again before moving anything.", 11, false, "#F0B45B")); }

        var actions = Ui.Row();
        var restore = Ui.Button("RESTORE DESK LAYOUT", true); restore.IsEnabled = layout.IsEnabled && layout.Windows.Any(window => window.IsEnabled); restore.Click += async (_, _) => await RestoreLayoutOnlyAsync(); actions.Children.Add(restore);
        var recapture = Ui.Button("RECAPTURE"); recapture.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel.Name, _parcel.Description, _parcel.Id)); actions.Children.Add(recapture);
        var edit = Ui.Button("EDIT LAYOUT"); edit.Click += async (_, _) => await EditDeskLayoutAsync(); actions.Children.Add(edit);
        var toggle = Ui.Button(layout.IsEnabled ? "DISABLE" : "ENABLE"); toggle.Click += async (_, _) => await ToggleDeskLayoutAsync(); actions.Children.Add(toggle);
        var remove = Ui.Button("REMOVE"); remove.Click += async (_, _) => await RemoveDeskLayoutAsync(); actions.Children.Add(remove);
        stack.Children.Add(actions);
        return Ui.Card(stack, 14);
    }

    private Grid BuildTools()
    {
        var grid = new Grid { ColumnSpacing = 9, RowSpacing = 9 };
        grid.Children.Add(_search);
        grid.Children.Add(_filter);
        grid.Children.Add(_sort);
        var refresh = Ui.Button(">> AVAILABILITY"); refresh.Click += async (_, _) => await RefreshAvailabilityAsync(true); grid.Children.Add(refresh);
        var add = Ui.Button("+ ADD ITEM"); add.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel!.Name, _parcel.Description, _parcel.Id)); grid.Children.Add(add);
        var capture = Ui.Button("CAPTURE OPEN TABS"); capture.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel!.Name, _parcel.Description, _parcel.Id)); grid.Children.Add(capture);
        var addLink = Ui.Button("ADD LINK MANUALLY"); addLink.Click += async (_, _) => await AddManualLinkAsync(); grid.Children.Add(addLink);
        var pasteLinks = Ui.Button("PASTE LINKS"); pasteLinks.Click += async (_, _) => await PasteLinksAsync(); grid.Children.Add(pasteLinks);
        var removeSelected = Ui.Button("REMOVE SELECTED"); removeSelected.Click += async (_, _) => await RemoveSelectedAsync(); grid.Children.Add(removeSelected);
        void Arrange(double width)
        {
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            if (width >= 1280)
            {
                for (var i = 0; i < 9; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                Place(_search, 0, 0); Place(_filter, 0, 1); Place(_sort, 0, 2); Place(refresh, 0, 3); Place(add, 0, 4); Place(capture, 0, 5); Place(addLink, 0, 6); Place(pasteLinks, 0, 7); Place(removeSelected, 0, 8);
            }
            else if (width >= 900)
            {
                for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Place(_search, 0, 0, 4); Place(_filter, 1, 0); Place(_sort, 1, 1); Place(refresh, 1, 2); Place(removeSelected, 1, 3);
                Place(add, 2, 0); Place(capture, 2, 1); Place(addLink, 2, 2); Place(pasteLinks, 2, 3);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Place(_search, 0, 0, 3); Place(_filter, 1, 0); Place(_sort, 1, 1); Place(refresh, 1, 2); Place(add, 2, 1); Place(capture, 2, 2);
                Place(addLink, 3, 0); Place(pasteLinks, 3, 1, 2); Place(removeSelected, 4, 1, 2);
            }
        }
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width); Arrange(1000); return grid;
    }

    private static void Place(FrameworkElement element, int row, int column, int span = 1) { Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span); }

    private void RefreshRows()
    {
        if (_parcel is null) return; _itemsHost.Children.Clear(); var group = (ParcelItemGroup)Math.Clamp(_filter.SelectedIndex, 0, 8); var sort = (ParcelItemSort)Math.Clamp(_sort.SelectedIndex, 0, 3);
        var list = ParcelItemQueries.Apply(_parcel.Items, new ParcelItemQuery(_search.Text, group, sort)); if (list.Count == 0) { var empty = Ui.Stack(5); empty.Children.Add(Ui.Mono(_parcel.ItemCount == 0 ? "EMPTY SETUP" : "NO MATCHES", 11, "#9BE28F", true)); empty.Children.Add(Ui.Text(_parcel.ItemCount == 0 ? "Use Add Item or Capture Open Windows to build this parcel." : "Try another search or filter.", 12, false, "#8D9CA2")); _itemsHost.Children.Add(Ui.Card(empty, 18)); return; }
        foreach (var windowGroup in list.Take(300).GroupBy(item => item.ItemType == ParcelItemType.BrowserTab ? $"{item.BrowserFamily ?? "browser"} / {item.BrowserWindowGroupId ?? "window-0"}" : "OTHER", StringComparer.OrdinalIgnoreCase))
        {
            if (windowGroup.Key == "OTHER") { foreach (var item in windowGroup) _itemsHost.Children.Add(ItemRow(item)); continue; }
            var groupHost = Ui.Stack(1); foreach (var item in windowGroup.OrderBy(item => item.BrowserTabIndex ?? int.MaxValue)) groupHost.Children.Add(ItemRow(item));
            _itemsHost.Children.Add(new Expander { Header = Ui.Mono($"{windowGroup.Key.ToUpperInvariant()}   {windowGroup.Count()} TABS", 10, "#9BE28F", true), IsExpanded = true, Content = groupHost, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        if (list.Count > 300) _itemsHost.Children.Add(Ui.Card(Ui.Text($"SHOWING 300 OF {list.Count} ITEMS - refine the search or filter to narrow this list.", 11, false, "#8D9CA2"), 11));
    }

    private async void QueueSearchRefresh() { var version = ++_searchVersion; await Task.Delay(180); if (version == _searchVersion) RefreshRows(); }

    private Border ItemRow(ParcelItem item)
    {
        var glyph = Ui.ItemIcon(item);
        var copy = Ui.Stack(2); copy.Children.Add(Ui.Text(item.DisplayName, 12, true)); var browserDetail = item.ItemType == ParcelItemType.BrowserTab ? $"{item.BrowserFamily?.ToUpperInvariant() ?? "BROWSER"} | {item.BrowserDomain ?? item.SecondaryDetail ?? "unknown domain"} | {item.BrowserWindowGroupId ?? "window-0"}{(string.IsNullOrWhiteSpace(item.BrowserTabGroupTitle) ? string.Empty : $" | {item.BrowserTabGroupTitle}")}{(item.BrowserPinned ? " | PINNED" : string.Empty)}" : null; copy.Children.Add(Ui.Mono(browserDetail ?? item.SecondaryDetail ?? (item.ItemType == ParcelItemType.Note ? "Saved inside WorkParcel" : item.Value), 9));
        var state = Ui.Stack(2); state.Children.Add(Ui.Mono(item.TypeLabel, 9, "#9BE28F", true)); state.Children.Add(Ui.Mono(item.AvailabilityLabel, 9, item.IsMissing || item.IsInaccessible ? "#F0B45B" : null)); state.Children.Add(Ui.Mono($"CHECKED {Ui.Relative(item.LastVerifiedAt)}", 8));
        var check = new CheckBox { IsChecked = _selectedItemIds.Contains(item.Id), VerticalAlignment = VerticalAlignment.Center, UseSystemFocusVisuals = true }; check.Checked += (_, _) => _selectedItemIds.Add(item.Id); check.Unchecked += (_, _) => _selectedItemIds.Remove(item.Id);
        var grid = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(check); Grid.SetColumn(glyph, 1); grid.Children.Add(glyph); Grid.SetColumn(copy, 2); grid.Children.Add(copy); Grid.SetColumn(state, 3); grid.Children.Add(state);
        var canRelink = item.IsMissing && item.ItemType is ParcelItemType.File or ParcelItemType.Folder or ParcelItemType.Application; var canOpen = item.LaunchEnabled && !item.IsMissing && !item.IsInaccessible; var open = Ui.Button(item.ItemType == ParcelItemType.Note ? "EDIT" : canRelink ? "LOCATE AGAIN" : canOpen ? "OPEN" : "UNAVAILABLE"); open.IsEnabled = item.ItemType == ParcelItemType.Note || canRelink || canOpen; open.Click += async (_, _) => { if (canRelink) await RelinkAsync(item); else if (item.ItemType == ParcelItemType.Note) await EditItemAsync(item); else await OpenOneAsync(item); }; Grid.SetColumn(open, 4); grid.Children.Add(open);
        var more = Ui.IconButton("...", "Item actions"); var menu = new MenuFlyout(); var edit = new MenuFlyoutItem { Text = "Edit" }; edit.Click += async (_, _) => await EditItemAsync(item); if (item.ItemType == ParcelItemType.BrowserTab) { var move = new MenuFlyoutItem { Text = "Move to saved window group" }; move.Click += async (_, _) => await MoveBrowserGroupAsync(item); menu.Items.Add(move); } var remove = new MenuFlyoutItem { Text = "Remove from parcel" }; remove.Click += async (_, _) => await RemoveItemAsync(item); menu.Items.Add(edit); if (item.HasChanged) { var accept = new MenuFlyoutItem { Text = "Accept changed file" }; accept.Click += async (_, _) => await AcceptChangedFileAsync(item); menu.Items.Add(accept); } menu.Items.Add(remove); more.Flyout = menu; Grid.SetColumn(more, 5); grid.Children.Add(more);
        return Ui.Interactive(new Border { Child = grid, Padding = new Thickness(9, 8, 7, 8), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Resource("SurfaceBrush") });
    }

    private async Task RefreshAvailabilityAsync(bool showResult)
    {
        if (_parcel is null || _busy || _removalInProgress) return; _busy = true;
        try { await Store.VerifyItemsAsync(_parcel); Build(); if (showResult) await Dialogs.ShowMessage(this, "AVAILABILITY CHECKED", $"{_parcel.AvailableItemCount} AVAILABLE | {_parcel.MissingItemCount} MISSING"); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); if (showResult) await Dialogs.ShowMessage(this, "CHECK INCOMPLETE", "Some items could not be verified. Their saved records were kept."); }
        finally { _busy = false; }
    }

    private Border DropTarget()
    {
        var target = Ui.Card(Ui.Mono("DROP FILES OR FOLDERS TO ATTACH", 10, "#8D9CA2", true), 10); target.AllowDrop = true;
        target.DragOver += (_, args) => { if (args.DataView.Contains(StandardDataFormats.StorageItems)) { args.AcceptedOperation = DataPackageOperation.Copy; args.DragUIOverride.Caption = "Add without moving the original"; } };
        target.DragEnter += (_, _) => target.BorderBrush = Ui.Resource("AccentBrush"); target.DragLeave += (_, _) => target.BorderBrush = Ui.Resource("BorderBrush"); target.Drop += async (_, args) => { target.BorderBrush = Ui.Resource("BorderBrush"); await AttachDropAsync(args.DataView); }; return target;
    }

    private async Task AttachDropAsync(DataPackageView data)
    {
        if (_parcel is null || !data.Contains(StandardDataFormats.StorageItems) || _busy) return; _busy = true; var items = new List<ParcelItem>(); var failed = 0;
        try
        {
            foreach (var entry in await data.GetStorageItemsAsync())
            {
                try
                {
                    ParcelItem? item = entry is StorageFile file ? await _factory.FileAsync(_parcel.Id, file.Path, _parcel.ItemCount + items.Count) : entry is StorageFolder folder ? _factory.Folder(_parcel.Id, folder.Path, _parcel.ItemCount + items.Count) : null;
                    if (item is null || ParcelItemIdentity.IsDuplicate(_parcel.Items.Concat(items), item)) failed++; else items.Add(item);
                }
                catch (Exception exception) { AppLogger.LogTechnicalError(exception); failed++; }
            }
            if (items.Count > 0) await Store.AddItemsAsync(_parcel, items); Build(); if (failed > 0) await Dialogs.ShowMessage(this, "DROP PARTLY ADDED", $"{items.Count} ADDED | {failed} DUPLICATE OR UNSUPPORTED");
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "DROP NOT SAVED", "The dropped items could not be written. Existing parcel records were kept."); }
        finally { _busy = false; }
    }

    private async Task AddManualLinkAsync()
    {
        if (_parcel is null || _busy) return;
        var title = new TextBox { Header = "DISPLAY NAME — OPTIONAL", MaxLength = 160 };
        var url = new TextBox { Header = "HTTP/HTTPS URL", PlaceholderText = "https://" };
        var note = new TextBox { Header = "SHORT NOTE — OPTIONAL", MaxLength = 240 };
        var stack = Ui.Stack(9); stack.Children.Add(title); stack.Children.Add(url); stack.Children.Add(note);
        var dialog = new ContentDialog { Title = "ADD LINK MANUALLY", Content = stack, PrimaryButtonText = "ADD LINK", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += (sender, args) => { if (!WebLinkRules.TryNormalize(url.Text, out _)) { args.Cancel = true; url.Description = "Enter a valid HTTP or HTTPS link."; } };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var item = _factory.WebLink(_parcel.Id, url.Text, title.Text, note.Text, _parcel.ItemCount);
            if (ParcelItemIdentity.IsDuplicate(_parcel.Items, item)) { await Dialogs.ShowMessage(this, "LINK ALREADY SAVED", "That URL is already saved in this parcel."); return; }
            await Store.AddItemsAsync(_parcel, new[] { item }); Build();
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "LINK WAS NOT SAVED", "The link could not be added. Check that it is a valid HTTP or HTTPS URL."); }
    }

    private async Task PasteLinksAsync()
    {
        if (_parcel is null || _busy) return;
        var input = new TextBox { Header = "ONE HTTP/HTTPS LINK PER LINE", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170 };
        var dialog = new ContentDialog { Title = "PASTE LINKS", Content = input, PrimaryButtonText = "CHECK LINKS", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        var parsed = WebLinkRules.ParseMany(input.Text);
        if (parsed.Valid.Count == 0) { await Dialogs.ShowMessage(this, "NO VALID LINKS", "Enter at least one HTTP or HTTPS link. Nothing was added."); return; }
        if (parsed.Invalid.Count > 0 && !await Dialogs.Confirm(this, "INVALID LINKS FOUND", $"These lines are not HTTP/HTTPS links and will not be saved:\n\n{string.Join("\n", parsed.Invalid.Take(8))}{(parsed.Invalid.Count > 8 ? "\n…" : string.Empty)}\n\nAdd the {parsed.Valid.Count} valid link{(parsed.Valid.Count == 1 ? string.Empty : "s")} only?", "ADD VALID LINKS")) return;
        var added = new List<ParcelItem>(); var duplicates = 0;
        foreach (var link in parsed.Valid)
        {
            var item = _factory.WebLink(_parcel.Id, link.AbsoluteUri, null, null, _parcel.ItemCount + added.Count);
            if (ParcelItemIdentity.IsDuplicate(_parcel.Items.Concat(added), item)) { duplicates++; continue; }
            added.Add(item);
        }
        if (added.Count == 0) { await Dialogs.ShowMessage(this, "LINKS ALREADY SAVED", $"{duplicates} link{(duplicates == 1 ? string.Empty : "s")} already exist{(duplicates == 1 ? "s" : string.Empty)} in this parcel."); return; }
        try { await Store.AddItemsAsync(_parcel, added); Build(); if (duplicates > 0) await Dialogs.ShowMessage(this, "LINKS PARTLY ADDED", $"{added.Count} ADDED · {duplicates} DUPLICATE{(duplicates == 1 ? string.Empty : "S")} SKIPPED"); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "LINKS WERE NOT SAVED", "The pasted links could not be written. Existing parcel records were kept."); }
    }

    private async Task OpenOneAsync(ParcelItem item)
    {
        try
        {
            await _availability.VerifyAsync(item); if (item.IsMissing || item.IsInaccessible) { RefreshRows(); await Dialogs.ShowMessage(this, "ITEM NOT AVAILABLE", "Locate or restore this item before opening it."); return; }
            if (item.ItemType == ParcelItemType.BrowserTab)
            {
                var browserResult = (await BrowserIntegrationService.Current.OpenTabsAsync(new[] { item }, item.BrowserFamily)).SingleOrDefault();
                if (browserResult is null || browserResult.Status is not ("Opened" or "AlreadyOpen")) await Dialogs.ShowMessage(this, "TAB DID NOT OPEN", browserResult?.Message ?? "The browser extension is not connected.");
                else
                {
                    item.BrowserLastOpenedAt = DateTime.Now;
                    try { await Store.UpdateItemAsync(_parcel!, item, ParcelHistoryEventType.ItemEdited, "Browser tab opened"); }
                    catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "TAB OPENED, RECORD NOT UPDATED", "The browser tab opened, but its last-opened timestamp could not be saved."); }
                }
                return;
            }
            var current = item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow ? await _windows.DetectAsync(_parcel!.Id) : Array.Empty<ParcelItem>(); var result = (await _launcher.OpenAsync(new[] { item }, current)).Single(); if (result.Status is not ItemOpenStatus.Opened and not ItemOpenStatus.AlreadyOpen) await Dialogs.ShowMessage(this, "ITEM DID NOT OPEN", result.Message);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "ITEM OPEN INCOMPLETE", "WorkParcel could not finish opening this item. The saved record was kept unchanged where possible."); }
    }

    private async Task OpenParcelAsync()
    {
        if (_parcel is null || _busy) return;
        var parcel = _parcel;
        IReadOnlyList<BrowserTabData> openBrowserTabs;
        try { openBrowserTabs = await BrowserIntegrationService.Current.ListTabsAsync(); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); openBrowserTabs = Array.Empty<BrowserTabData>(); }
        var checks = new Dictionary<ParcelItem, CheckBox>();
        var stack = Ui.Stack(5);
        var controls = Ui.Row(); var all = Ui.Button("SELECT ALL SUPPORTED"); var clear = Ui.Button("CLEAR ALL"); var selectedCount = Ui.Mono("0 SELECTED", 10, "#9BE28F", true); controls.Children.Add(all); controls.Children.Add(clear); controls.Children.Add(Ui.Spacer()); controls.Children.Add(selectedCount); stack.Children.Add(controls);
        var allowDuplicateCopies = new CheckBox { Content = "OPEN ANOTHER COPY WHEN A SAVED URL IS ALREADY OPEN", IsChecked = false }; stack.Children.Add(allowDuplicateCopies);
        var restoreLayout = new CheckBox { Content = "RESTORE DESK MEMORY AFTER OPEN", IsChecked = parcel.DeskLayout?.IsEnabled == true && Store.DeskMemorySettings.RestoreByDefault };
        restoreLayout.IsEnabled = parcel.DeskLayout?.Windows.Any(window => window.IsEnabled) == true;
        stack.Children.Add(restoreLayout);
        if (parcel.DeskLayout is not null && restoreLayout.IsEnabled && Store.DeskMemorySettings.ShowPreviewDuringOpen) stack.Children.Add(DeskPreviewBuilder.Build(parcel.DeskLayout, 680, 220));
        var restorableOpenTabs = openBrowserTabs.Where(tab => BrowserTabRules.IsAllowedForCapture(tab)).ToList();
        foreach (var itemGroup in parcel.Items.GroupBy(item => item.ItemType == ParcelItemType.BrowserTab ? $"{item.BrowserFamily?.ToUpperInvariant() ?? "BROWSER"} / {item.BrowserWindowGroupId ?? "window-0"}" : "OTHER", StringComparer.OrdinalIgnoreCase))
        {
            if (itemGroup.Key != "OTHER") stack.Children.Add(Ui.Mono(itemGroup.Key, 10, "#9BE28F", true));
            foreach (var item in itemGroup.OrderBy(item => item.BrowserTabIndex ?? int.MaxValue))
            {
                var browserUnsupported = item.ItemType == ParcelItemType.BrowserTab && !BrowserTabRules.TryGetRestorableUri(item.Value, out _);
                var supported = item.LaunchEnabled && !item.IsMissing && !item.IsInaccessible && item.ItemType != ParcelItemType.Note && !browserUnsupported;
                var alreadyOpen = item.ItemType == ParcelItemType.BrowserTab && restorableOpenTabs.Any(tab => string.Equals(tab.Browser, item.BrowserFamily, StringComparison.OrdinalIgnoreCase) && string.Equals(tab.Url, item.Value, StringComparison.Ordinal));
                var availability = browserUnsupported ? "UNSUPPORTED URL" : alreadyOpen ? "ALREADY OPEN" : item.AvailabilityLabel;
                var box = new CheckBox { Content = $"{item.DisplayName}   [{item.TypeLabel}]   {availability}", IsChecked = supported, IsEnabled = supported };
                checks[item] = box; stack.Children.Add(box);
            }
        }
        var dialog = new ContentDialog { Title = "OPEN PARCEL", Content = new ScrollViewer { Content = stack, MaxHeight = 600 }, PrimaryButtonText = "OPEN SELECTED ITEMS", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        void UpdateOpenButton() { var count = checks.Count(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true); dialog.IsPrimaryButtonEnabled = count > 0; selectedCount.Text = $"{count} SELECTED"; }
        foreach (var box in checks.Values) { box.Checked += (_, _) => UpdateOpenButton(); box.Unchecked += (_, _) => UpdateOpenButton(); }
        all.Click += (_, _) => { foreach (var pair in checks.Where(pair => pair.Value.IsEnabled)) pair.Value.IsChecked = true; UpdateOpenButton(); }; clear.Click += (_, _) => { foreach (var pair in checks) pair.Value.IsChecked = false; UpdateOpenButton(); }; dialog.Opened += (_, _) => UpdateOpenButton();
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        var selected = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); if (selected.Count == 0) return; _busy = true;
        try
        {
            var current = await _windows.DetectAsync(parcel.Id);
            var currentForReuse = Store.DeskMemorySettings.ReuseMatchingOpenWindows ? current : Array.Empty<ParcelItem>();
            var results = (await _launcher.OpenAsync(selected.Where(item => item.ItemType != ParcelItemType.BrowserTab), currentForReuse)).ToList();
            foreach (var browserGroup in selected.Where(item => item.ItemType == ParcelItemType.BrowserTab).GroupBy(item => item.BrowserFamily ?? "chrome", StringComparer.OrdinalIgnoreCase))
                foreach (var browserResult in await BrowserIntegrationService.Current.OpenTabsAsync(browserGroup, browserGroup.Key, allowDuplicateCopies.IsChecked == true))
                    results.Add(new(Guid.TryParse(browserResult.ItemKey, out var id) ? id : Guid.Empty, browserResult.Status switch { "Opened" => ItemOpenStatus.Opened, "AlreadyOpen" => ItemOpenStatus.AlreadyOpen, "Unsupported" => ItemOpenStatus.Unsupported, _ => ItemOpenStatus.Failed }, browserResult.Message));
            var opened = results.Count(result => result.Status is ItemOpenStatus.Opened or ItemOpenStatus.AlreadyOpen); var failed = results.Count - opened;
            if (opened > 0) await Store.SetStateAsync(parcel, ParcelStatus.Open, $"Parcel opened - {opened} opened or already open | {failed} skipped or failed");
            var openedBrowserItems = selected.Where(item => item.ItemType == ParcelItemType.BrowserTab).Where(item => results.Any(result => result.ItemId == item.Id && result.Status is ItemOpenStatus.Opened or ItemOpenStatus.AlreadyOpen)).ToList();
            if (openedBrowserItems.Count > 0) { try { await Store.MarkBrowserItemsOpenedAsync(parcel, openedBrowserItems); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); } }

            var deskLines = new List<string>();
            if (restoreLayout.IsChecked == true && parcel.DeskLayout is { IsEnabled: true } layout && opened > 0)
            {
                var allowed = selected.Where(item => item.ItemType == ParcelItemType.ApplicationWindow).Select(item => item.Id).ToHashSet();
                DeskAmbiguityResolverAsync? reviewMatches = Store.DeskMemorySettings.ReviewBeforeApplying ? ChooseAmbiguousWindowAsync : null;
                var deskResult = await _desk.RestoreAsync(layout, new DeskRestoreOptions(Store.DeskMemorySettings.RestoreMaximized, Store.DeskMemorySettings.RestoreMinimized, Store.DeskMemorySettings.EnableUndo, Store.DeskMemorySettings.RestorationTimeoutSeconds), allowed, reviewMatches);
                deskLines.Add($"DESK MEMORY: {deskResult.RestoredCount} RESTORED | {deskResult.Items.Count(item => item.Kind == DeskRestoreResultKind.Ambiguous)} AMBIGUOUS | {deskResult.Items.Count(item => item.Kind is DeskRestoreResultKind.TimedOut or DeskRestoreResultKind.Skipped)} NOT FOUND");
                if (deskResult.RestoredCount > 0) try { await Store.RecordDeskLayoutRestoredAsync(parcel, $"Desk layout restored — {deskResult.RestoredCount} window{(deskResult.RestoredCount == 1 ? string.Empty : "s")}"); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
                deskLines.AddRange(deskResult.Items.Where(item => item.Kind is DeskRestoreResultKind.Ambiguous or DeskRestoreResultKind.AccessDenied or DeskRestoreResultKind.Failed or DeskRestoreResultKind.TimedOut).Select(item => $"{item.Kind.ToString().ToUpperInvariant()}: {item.Message}"));
            }
            var lines = results.Select(result => $"{selected.FirstOrDefault(item => item.Id == result.ItemId)?.DisplayName ?? result.ItemId.ToString()}   {result.Status.ToString().ToUpperInvariant()}").Concat(deskLines);
            await Dialogs.ShowMessage(this, "OPEN PARCEL FINISHED", $"{opened} OPENED / ALREADY OPEN | {failed} SKIPPED OR FAILED\n\n{string.Join("\n", lines)}"); Build();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "OPEN PARCEL INCOMPLETE", "Some items may have opened, but WorkParcel could not finish the operation. The saved parcel was not deleted."); }
        finally { _busy = false; }
    }

    private async Task RestoreLayoutOnlyAsync()
    {
        if (_parcel?.DeskLayout is not { IsEnabled: true } layout || _busy) return;
        try
        {
            var current = _desk.Provider.GetSnapshot();
            var plan = DeskMemoryLogic.BuildPlan(layout, current);
            var checks = new Dictionary<DeskRestorePlanItem, CheckBox>();
            var stack = Ui.Stack(5);
            stack.Children.Add(Ui.Text("This preview only moves windows that are already open. WorkParcel will not launch anything.", 12, false, "#8D9CA2"));
            stack.Children.Add(Ui.Mono($"{plan.Items.Count(item => item.Match.IsAutomatic)} HIGH-CONFIDENCE · {plan.AmbiguousMatches.Count} NEED REVIEW · {plan.Items.Count(item => item.Match.Confidence == DeskMatchConfidence.NoMatch)} NOT FOUND", 10, "#9BE28F", true));
            foreach (var item in plan.Items)
            {
                var automatic = item.Match.IsAutomatic && item.Match.Live is not null;
                var ambiguous = item.Match.Confidence is DeskMatchConfidence.Ambiguous or DeskMatchConfidence.Possible;
                var box = new CheckBox
                {
                    Content = $"{item.Saved.CapturedTitle}   [{(automatic ? "MATCHED" : ambiguous ? "AMBIGUOUS" : item.Match.Confidence.ToString().ToUpperInvariant())}]   {item.Match.Explanation}",
                    IsChecked = automatic,
                    IsEnabled = automatic || ambiguous,
                    UseSystemFocusVisuals = true
                };
                checks[item] = box; stack.Children.Add(box);
            }
            var dialog = new ContentDialog { Title = "RESTORE DESK LAYOUT", Content = new ScrollViewer { Content = stack, MaxHeight = 520 }, PrimaryButtonText = "RESTORE SELECTED", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
            var selectedIds = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key.Saved.ParcelItemId).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
            if (selectedIds.Count == 0) { await Dialogs.ShowMessage(this, "NOTHING SELECTED", "Select at least one matched window to restore."); return; }
            _busy = true;
            var settings = Store.DeskMemorySettings;
            var result = await _desk.RestoreLayoutOnlyAsync(layout, new DeskRestoreOptions(settings.RestoreMaximized, settings.RestoreMinimized, settings.EnableUndo, settings.RestorationTimeoutSeconds), selectedIds, ChooseAmbiguousWindowAsync);
            if (result.RestoredCount > 0) try { await Store.RecordDeskLayoutRestoredAsync(_parcel, $"Desk layout restored — {result.RestoredCount} window{(result.RestoredCount == 1 ? string.Empty : "s")}"); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
            var failures = result.Items.Where(item => item.Kind is not (DeskRestoreResultKind.RestoredExactly or DeskRestoreResultKind.RestoredScaled or DeskRestoreResultKind.MovedToFallbackMonitor or DeskRestoreResultKind.ReusedExistingWindow)).Select(item => $"{item.Kind.ToString().ToUpperInvariant()}: {item.Message}").ToList();
            var message = $"{result.RestoredCount} WINDOW{(result.RestoredCount == 1 ? string.Empty : "S")} RESTORED" + (failures.Count == 0 ? string.Empty : $"\n\n{string.Join("\n", failures.Take(12))}");
            await Dialogs.ShowMessage(this, "DESK RESTORE FINISHED", message);
            if (result.RestoredCount > 0 && settings.EnableUndo && await Dialogs.Confirm(this, "UNDO DESK RESTORE?", "Return the windows moved by this operation to their previous placement?", "UNDO"))
            {
                var undo = await _desk.UndoLastAsync();
                await Dialogs.ShowMessage(this, "DESK RESTORE UNDONE", $"{undo.RestoredCount} WINDOW{(undo.RestoredCount == 1 ? string.Empty : "S")} RETURNED");
            }
            Build();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "DESK RESTORE INCOMPLETE", "No application was launched. Some windows may have been restored; check the result and try again."); }
        finally { _busy = false; }
    }

    private async Task<nint?> ChooseAmbiguousWindowAsync(DeskWindowMatch match, IReadOnlyList<DeskLiveWindow> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return null;
        var choices = candidates.Select(candidate => $"{candidate.Title} · {candidate.ProcessName} · {candidate.Bounds.Width}×{candidate.Bounds.Height}").ToList();
        var picker = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var stack = Ui.Stack(6); stack.Children.Add(Ui.Text($"Choose the current window that should receive '{match.Saved.CapturedTitle}'. No other candidate will be moved.", 12, false, "#8D9CA2")); stack.Children.Add(picker);
        var dialog = new ContentDialog { Title = "AMBIGUOUS WINDOW MATCH", Content = stack, PrimaryButtonText = "USE SELECTED WINDOW", CloseButtonText = "SKIP", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return picker.SelectedIndex >= 0 && picker.SelectedIndex < candidates.Count ? candidates[picker.SelectedIndex].Handle : null;
    }

    private async Task EditDeskLayoutAsync()
    {
        if (_parcel?.DeskLayout is not { } layout || _busy) return;
        var edited = CloneDeskLayout(layout);
        var rows = new List<(DeskWindowLayout Window, CheckBox Enabled, ComboBox Monitor, ComboBox State, TextBox Left, TextBox Top, TextBox Width, TextBox Height)>();
        var stack = Ui.Stack(6);
        stack.Children.Add(Ui.Text("Disable individual windows, choose a fallback monitor, edit normal bounds or change saved state. These edits never touch the external application.", 12, false, "#8D9CA2"));
        foreach (var window in edited.Windows)
        {
            var enabled = new CheckBox { Content = window.CapturedTitle, IsChecked = window.IsEnabled, UseSystemFocusVisuals = true };
            var monitor = new ComboBox { Header = "MONITOR", ItemsSource = edited.Monitors, DisplayMemberPath = nameof(DeskMonitorLayout.FriendlyName), SelectedItem = edited.Monitors.FirstOrDefault(candidate => candidate.Id == window.SavedMonitorId), MinWidth = 180 };
            var state = new ComboBox { Header = "STATE", ItemsSource = Enum.GetValues<DeskWindowState>(), SelectedItem = window.WindowState, MinWidth = 120 };
            var left = new TextBox { Header = "LEFT", Text = window.NormalBounds.Left.ToString(), Width = 90 }; var top = new TextBox { Header = "TOP", Text = window.NormalBounds.Top.ToString(), Width = 90 }; var width = new TextBox { Header = "WIDTH", Text = window.NormalBounds.Width.ToString(), Width = 90 }; var height = new TextBox { Header = "HEIGHT", Text = window.NormalBounds.Height.ToString(), Width = 90 };
            var reset = Ui.Button("RESET BOUNDS"); var original = window.NormalBounds; reset.Click += (_, _) => { left.Text = original.Left.ToString(); top.Text = original.Top.ToString(); width.Text = original.Width.ToString(); height.Text = original.Height.ToString(); };
            var row = Ui.Stack(5); row.Children.Add(enabled); row.Children.Add(Ui.Row(monitor, state, left, top, width, height, reset)); stack.Children.Add(Ui.Card(row, 10)); rows.Add((window, enabled, monitor, state, left, top, width, height));
        }
        var dialog = new ContentDialog { Title = "EDIT DESK LAYOUT", Content = new ScrollViewer { Content = stack, MaxHeight = 620 }, PrimaryButtonText = "SAVE LAYOUT", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            foreach (var row in rows)
            {
                if (!int.TryParse(row.Left.Text, out var left) || !int.TryParse(row.Top.Text, out var top) || !int.TryParse(row.Width.Text, out var width) || !int.TryParse(row.Height.Text, out var height) || width <= 0 || height <= 0)
                {
                    args.Cancel = true; return;
                }
                row.Window.IsEnabled = row.Enabled.IsChecked == true; row.Window.SavedMonitorId = (row.Monitor.SelectedItem as DeskMonitorLayout)?.Id ?? row.Window.SavedMonitorId; row.Window.WindowState = row.State.SelectedItem is DeskWindowState selectedState ? selectedState : DeskWindowState.Normal; row.Window.NormalBounds = new DeskRect(left, top, width, height); row.Window.AbsoluteBounds = row.Window.NormalBounds;
            }
        };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        try { await Store.SaveDeskLayoutAsync(_parcel, edited, "Desk layout edited"); Build(); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "LAYOUT WAS NOT SAVED", "The previous Desk Memory remains unchanged."); }
    }

    private async Task ToggleDeskLayoutAsync()
    {
        if (_parcel?.DeskLayout is not { } layout || _busy) return;
        var edited = CloneDeskLayout(layout); edited.IsEnabled = !layout.IsEnabled;
        try { await Store.SaveDeskLayoutAsync(_parcel, edited, edited.IsEnabled ? "Desk layout enabled" : "Desk layout disabled"); Build(); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "DESK MEMORY SETTING WAS NOT SAVED", "The previous Desk Memory setting remains unchanged."); }
    }

    private async Task RemoveDeskLayoutAsync()
    {
        if (_parcel?.DeskLayout is null || !await Dialogs.Confirm(this, "REMOVE DESK MEMORY?", "Only the saved window-layout records will be removed. Parcel items and external resources stay untouched.", "REMOVE LAYOUT")) return;
        try { await Store.RemoveDeskLayoutAsync(_parcel); Build(); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "LAYOUT WAS NOT REMOVED", "The saved Desk Memory records are still present."); }
    }

    private static DeskLayoutSnapshot CloneDeskLayout(DeskLayoutSnapshot source, ISet<Guid>? allowedWindowItemIds = null)
    {
        var clone = new DeskLayoutSnapshot { Id = source.Id, ParcelId = source.ParcelId, Name = source.Name, CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, IsCurrent = source.IsCurrent, IsEnabled = source.IsEnabled, TopologySignature = source.TopologySignature };
        foreach (var monitor in source.Monitors) clone.Monitors.Add(new DeskMonitorLayout { Id = monitor.Id, LayoutSnapshotId = monitor.LayoutSnapshotId, ParcelId = monitor.ParcelId, DeviceIdentifier = monitor.DeviceIdentifier, FriendlyName = monitor.FriendlyName, IsPrimary = monitor.IsPrimary, Bounds = monitor.Bounds, WorkArea = monitor.WorkArea, RelativeArrangement = monitor.RelativeArrangement, DpiX = monitor.DpiX, DpiY = monitor.DpiY, Orientation = monitor.Orientation, CaptureOrder = monitor.CaptureOrder, CreatedAt = monitor.CreatedAt });
        foreach (var window in source.Windows.Where(window => allowedWindowItemIds is null || window.ParcelItemId is Guid itemId && allowedWindowItemIds.Contains(itemId))) clone.Windows.Add(new DeskWindowLayout { Id = window.Id, LayoutSnapshotId = window.LayoutSnapshotId, ParcelId = window.ParcelId, ParcelItemId = window.ParcelItemId, ExecutableIdentity = window.ExecutableIdentity, ApplicationIdentifier = window.ApplicationIdentifier, ProcessName = window.ProcessName, CapturedTitle = window.CapturedTitle, NormalizedTitle = window.NormalizedTitle, WindowClassName = window.WindowClassName, SavedMonitorId = window.SavedMonitorId, AbsoluteBounds = window.AbsoluteBounds, NormalBounds = window.NormalBounds, RelativeLeft = window.RelativeLeft, RelativeTop = window.RelativeTop, RelativeWidth = window.RelativeWidth, RelativeHeight = window.RelativeHeight, WindowState = window.WindowState, ZOrderRank = window.ZOrderRank, SourceDpiX = window.SourceDpiX, SourceDpiY = window.SourceDpiY, MonitorDpiX = window.MonitorDpiX, MonitorDpiY = window.MonitorDpiY, IsTopmost = window.IsTopmost, CoordinatesArePhysicalPixels = window.CoordinatesArePhysicalPixels, IsEnabled = window.IsEnabled, IsSupported = window.IsSupported, MatchMetadata = window.MatchMetadata, LastMatchConfidence = window.LastMatchConfidence, CreatedAt = window.CreatedAt, UpdatedAt = window.UpdatedAt });
        return clone;
    }

    private async Task OpenParcelLegacyAsync()
    {
        if (_parcel is null || _busy) return;
        IReadOnlyList<BrowserTabData> openBrowserTabs;
        try { openBrowserTabs = await BrowserIntegrationService.Current.ListTabsAsync(); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); openBrowserTabs = Array.Empty<BrowserTabData>(); }
        var checks = new Dictionary<ParcelItem, CheckBox>(); var stack = Ui.Stack(5); var controls = Ui.Row(); var all = Ui.Button("SELECT ALL SUPPORTED"); var clear = Ui.Button("CLEAR ALL"); var selectedCount = Ui.Mono("0 SELECTED", 10, "#9BE28F", true); controls.Children.Add(all); controls.Children.Add(clear); controls.Children.Add(Ui.Spacer()); controls.Children.Add(selectedCount); stack.Children.Add(controls); var allowDuplicateCopies = new CheckBox { Content = "OPEN ANOTHER COPY WHEN A SAVED URL IS ALREADY OPEN", IsChecked = false }; stack.Children.Add(allowDuplicateCopies);
        var restorableOpenTabs = openBrowserTabs.Where(tab => BrowserTabRules.IsAllowedForCapture(tab)).ToList();
        foreach (var itemGroup in _parcel.Items.GroupBy(item => item.ItemType == ParcelItemType.BrowserTab ? $"{item.BrowserFamily?.ToUpperInvariant() ?? "BROWSER"} / {item.BrowserWindowGroupId ?? "window-0"}" : "OTHER", StringComparer.OrdinalIgnoreCase)) { if (itemGroup.Key != "OTHER") stack.Children.Add(Ui.Mono(itemGroup.Key, 10, "#9BE28F", true)); foreach (var item in itemGroup.OrderBy(item => item.BrowserTabIndex ?? int.MaxValue)) { var browserUnsupported = item.ItemType == ParcelItemType.BrowserTab && !BrowserTabRules.TryGetRestorableUri(item.Value, out _); var supported = item.LaunchEnabled && !item.IsMissing && !item.IsInaccessible && item.ItemType != ParcelItemType.Note && !browserUnsupported; var alreadyOpen = item.ItemType == ParcelItemType.BrowserTab && restorableOpenTabs.Any(tab => string.Equals(tab.Browser, item.BrowserFamily, StringComparison.OrdinalIgnoreCase) && string.Equals(tab.Url, item.Value, StringComparison.Ordinal)); var availability = browserUnsupported ? "UNSUPPORTED URL" : alreadyOpen ? "ALREADY OPEN" : item.AvailabilityLabel; var box = new CheckBox { Content = $"{item.DisplayName}   [{item.TypeLabel}]   {availability}", IsChecked = supported, IsEnabled = supported }; checks[item] = box; stack.Children.Add(box); } }
        var dialog = new ContentDialog { Title = "OPEN PARCEL", Content = new ScrollViewer { Content = stack, MaxHeight = 420 }, PrimaryButtonText = "OPEN SELECTED ITEMS", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        void UpdateOpenButton() { var count = checks.Count(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true); dialog.IsPrimaryButtonEnabled = count > 0; selectedCount.Text = $"{count} SELECTED"; }
        foreach (var box in checks.Values) { box.Checked += (_, _) => UpdateOpenButton(); box.Unchecked += (_, _) => UpdateOpenButton(); }
        all.Click += (_, _) => { foreach (var pair in checks.Where(pair => pair.Value.IsEnabled)) pair.Value.IsChecked = true; UpdateOpenButton(); }; clear.Click += (_, _) => { foreach (var pair in checks) pair.Value.IsChecked = false; UpdateOpenButton(); }; dialog.Opened += (_, _) => UpdateOpenButton();
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return; var selected = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); if (selected.Count == 0) return; _busy = true;
        try
        {
            var current = await _windows.DetectAsync(_parcel.Id); var results = (await _launcher.OpenAsync(selected.Where(item => item.ItemType != ParcelItemType.BrowserTab), current)).ToList();
            foreach (var browserGroup in selected.Where(item => item.ItemType == ParcelItemType.BrowserTab).GroupBy(item => item.BrowserFamily ?? "chrome", StringComparer.OrdinalIgnoreCase)) foreach (var browserResult in await BrowserIntegrationService.Current.OpenTabsAsync(browserGroup, browserGroup.Key, allowDuplicateCopies.IsChecked == true)) results.Add(new(Guid.TryParse(browserResult.ItemKey, out var id) ? id : Guid.Empty, browserResult.Status switch { "Opened" => ItemOpenStatus.Opened, "AlreadyOpen" => ItemOpenStatus.AlreadyOpen, "Unsupported" => ItemOpenStatus.Unsupported, _ => ItemOpenStatus.Failed }, browserResult.Message));
            var opened = results.Count(result => result.Status is ItemOpenStatus.Opened or ItemOpenStatus.AlreadyOpen); var failed = results.Count - opened; if (opened > 0) await Store.SetStateAsync(_parcel, ParcelStatus.Open, $"Parcel opened - {opened} opened or already open | {failed} skipped or failed");
            var openedBrowserItems = selected.Where(item => item.ItemType == ParcelItemType.BrowserTab).Where(item => results.Any(result => result.ItemId == item.Id && result.Status is ItemOpenStatus.Opened or ItemOpenStatus.AlreadyOpen)).ToList();
            if (openedBrowserItems.Count > 0) { try { await Store.MarkBrowserItemsOpenedAsync(_parcel, openedBrowserItems); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); } }
            var lines = results.Select(result => $"{selected.FirstOrDefault(item => item.Id == result.ItemId)?.DisplayName ?? result.ItemId.ToString()}   {result.Status.ToString().ToUpperInvariant()}"); await Dialogs.ShowMessage(this, "OPEN PARCEL FINISHED", $"{opened} OPENED / ALREADY OPEN | {failed} SKIPPED OR FAILED\n\n{string.Join("\n", lines)}"); Build();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "OPEN PARCEL INCOMPLETE", "Some items may have opened, but WorkParcel could not finish the operation. The saved parcel was not deleted."); }
        finally { _busy = false; }
    }

    private async Task PackAwayAsync()
    {
        if (_parcel is null || _busy) return;
        var parcel = _parcel; _busy = true;
        try
        {
            var current = await _windows.DetectAsync(parcel.Id); var candidates = parcel.Items.ToList();
            foreach (var savedItem in candidates.Where(item => item.ItemType == ParcelItemType.ApplicationWindow)) { savedItem.RuntimeWindowHandle = nint.Zero; savedItem.RuntimeProcessId = 0; }
            foreach (var savedItem in candidates.Where(item => item.ItemType == ParcelItemType.BrowserTab)) { savedItem.BrowserSessionTabId = null; savedItem.BrowserSessionWindowId = null; savedItem.BrowserConnectionId = null; savedItem.CloseSupported = false; }
            var matchedWindows = OpenWindowService.MatchSavedItems(candidates, current);
            var matchedHandles = new HashSet<nint>();
            foreach (var match in matchedWindows.Where(match => match.Live is not null))
            {
                var live = current.FirstOrDefault(item => item.RuntimeWindowHandle == match.Live!.Handle);
                if (live is null) continue;
                match.Item.RuntimeWindowHandle = live.RuntimeWindowHandle; match.Item.RuntimeProcessId = live.RuntimeProcessId; match.Item.CloseSupported = true; match.Item.WindowClassName = live.WindowClassName; matchedHandles.Add(live.RuntimeWindowHandle);
            }
            foreach (var window in current.Where(window => !matchedHandles.Contains(window.RuntimeWindowHandle))) candidates.Add(window);
            var browserTabs = await BrowserIntegrationService.Current.ListTabsAsync();
            var matchedBrowserIds = new HashSet<Guid>(); var liveToSavedGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tab in browserTabs.Where(tab => BrowserTabRules.IsAllowedForCapture(tab)))
            {
                var candidate = BrowserIntegrationService.FromTab(parcel.Id, tab, candidates.Count, null);
                var saved = BrowserIntegrationService.FindSavedTab(candidates, tab, matchedBrowserIds, liveToSavedGroups);
                if (saved is not null) { BrowserIntegrationService.ApplyLiveTab(saved, tab, updateSavedWindowGroup: true); matchedBrowserIds.Add(saved.Id); }
                else { candidate.CloseSupported = tab.CanRestore; candidates.Add(candidate); matchedBrowserIds.Add(candidate.Id); }
            }
            var checks = new Dictionary<ParcelItem, CheckBox>(); var stack = Ui.Stack(5); stack.Children.Add(Ui.Text("Choose what remains saved. Saving does not close anything unless you choose the close option.", 12, false, "#8D9CA2"));
            var updateDeskMemory = new CheckBox { Content = "UPDATE DESK MEMORY", IsChecked = parcel.DeskLayout?.IsEnabled == true || candidates.Any(item => item.ItemType == ParcelItemType.ApplicationWindow && item.RuntimeWindowHandle != nint.Zero), IsEnabled = candidates.Any(item => item.ItemType == ParcelItemType.ApplicationWindow && item.RuntimeWindowHandle != nint.Zero) }; stack.Children.Add(updateDeskMemory);
            var summary = Ui.Mono(string.Empty, 10, "#9BE28F", true); stack.Children.Add(summary);
            foreach (var item in candidates)
            {
                var saved = parcel.Items.Any(existing => existing.Id == item.Id); var closeReady = item.ItemType == ParcelItemType.BrowserTab ? item.CloseSupported && !string.IsNullOrWhiteSpace(item.BrowserSessionTabId) && !string.IsNullOrWhiteSpace(item.BrowserSessionWindowId) : WindowCloseRequestService.IsSafeCloseTarget(item); var source = saved ? "SAVED" : "NEW DETECTED"; var closeState = closeReady ? "CLOSE READY" : "CANNOT CLOSE";
                var box = new CheckBox { Content = $"{source}   {item.DisplayName}   [{item.TypeLabel}]   {item.AvailabilityLabel}   [{closeState}]", IsChecked = saved }; checks[item] = box; stack.Children.Add(box);
            }
            void UpdateSummary() { var selectedNow = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); var selectedNowIds = selectedNow.Select(item => item.Id).ToHashSet(); var additions = selectedNow.Count(item => parcel.Items.All(saved => saved.Id != item.Id)); var removalsNow = parcel.Items.Count(item => !selectedNowIds.Contains(item.Id)); var missing = selectedNow.Count(item => item.IsMissing || item.IsInaccessible); summary.Text = $"{selectedNow.Count} SAVED  |  {additions} NEW  |  {removalsNow} REMOVED  |  {missing} UNAVAILABLE"; }
            foreach (var box in checks.Values) { box.Checked += (_, _) => UpdateSummary(); box.Unchecked += (_, _) => UpdateSummary(); } UpdateSummary();
            var closeableAppCount = candidates.Count(item => item.ItemType == ParcelItemType.ApplicationWindow && WindowCloseRequestService.IsSafeCloseTarget(item));
            var closeableTabCount = candidates.Count(item => item.ItemType == ParcelItemType.BrowserTab && item.CloseSupported && !string.IsNullOrWhiteSpace(item.BrowserSessionTabId) && !string.IsNullOrWhiteSpace(item.BrowserSessionWindowId));
            var closeButtonEnabled = closeableAppCount > 0 || BrowserIntegrationService.Current.TabClosingEnabled && closeableTabCount > 0;
            var secondaryText = BrowserIntegrationService.Current.TabClosingEnabled ? "SAVE AND CLOSE SELECTED" : closeableAppCount > 0 ? "SAVE AND CLOSE APPLICATION WINDOWS" : "BROWSER TAB CLOSING UNAVAILABLE";
            var dialog = new ContentDialog { Title = "PACK AWAY", Content = new ScrollViewer { Content = stack, MaxHeight = 520 }, PrimaryButtonText = "SAVE SELECTION", SecondaryButtonText = secondaryText, CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, IsSecondaryButtonEnabled = closeButtonEnabled };
            var choice = await Ui.ShowDialog(dialog); if (choice == ContentDialogResult.None) return;
            var selected = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); var selectedIds = selected.Select(item => item.Id).ToHashSet(); var removals = parcel.Items.Count(item => !selectedIds.Contains(item.Id));
            if (removals > 0 && !await Dialogs.Confirm(this, "REMOVE SAVED ITEM RECORDS?", $"{removals} WorkParcel record{(removals == 1 ? string.Empty : "s")} will be removed. External resources stay untouched.", "REMOVE AND SAVE")) return;
            var closeItems = _closer.BuildPlan(selected); var closeTabs = selected.Where(item => item.ItemType == ParcelItemType.BrowserTab && item.CloseSupported && !string.IsNullOrWhiteSpace(item.BrowserSessionTabId) && !string.IsNullOrWhiteSpace(item.BrowserSessionWindowId)).ToList();

            DeskLayoutSnapshot? deskLayout = null; var clearDeskLayout = false; var deskMessage = "DESK MEMORY NOT UPDATED";
            if (updateDeskMemory.IsChecked == true && selected.Any(item => item.ItemType == ParcelItemType.ApplicationWindow))
            {
                try
                {
                    var capture = await _desk.CaptureAsync(parcel.Id, selected.Where(item => item.ItemType == ParcelItemType.ApplicationWindow).ToList());
                    deskLayout = capture.Snapshot; deskMessage = $"DESK MEMORY CAPTURED ({deskLayout.Windows.Count} WINDOWS)";
                    if (capture.Failures.Count > 0 && !await Dialogs.Confirm(this, "SOME WINDOWS WERE NOT CAPTURED", $"{capture.Failures.Count} selected window{(capture.Failures.Count == 1 ? string.Empty : "s")} could not be measured. Save the available Desk Memory and continue?", "SAVE AVAILABLE LAYOUT")) return;
                }
                catch (Exception exception)
                {
                    AppLogger.LogTechnicalError(exception);
                    if (!await Dialogs.Confirm(this, "DESK MEMORY UPDATE FAILED", "The window layout could not be captured. Continue saving the parcel without changing its Desk Memory?", "CONTINUE WITHOUT UPDATE")) return;
                }
            }
            if (deskLayout is null && parcel.DeskLayout is not null)
            {
                var selectedWindowIds = selected.Where(item => item.ItemType == ParcelItemType.ApplicationWindow).Select(item => item.Id).ToHashSet();
                deskLayout = CloneDeskLayout(parcel.DeskLayout, selectedWindowIds);
                if (deskLayout.Windows.Count == 0) { deskLayout = null; clearDeskLayout = true; deskMessage = "DESK MEMORY REMOVED"; }
                else deskMessage = "DESK MEMORY KEPT FOR SAVED WINDOWS";
            }
            await Store.ReplaceItemsAsync(parcel, selected, choice == ContentDialogResult.Secondary ? $"Parcel packed - {selected.Count} items saved; graceful close requested; {deskMessage.ToLowerInvariant()}" : $"Parcel packed - {selected.Count} items saved; {deskMessage.ToLowerInvariant()}", deskLayout: deskLayout, clearDeskLayout: clearDeskLayout);
            var closed = 0; var stillOpen = 0;
            if (choice == ContentDialogResult.Secondary)
            {
                var tabSummary = $"{closeTabs.Count} browser tab{(closeTabs.Count == 1 ? string.Empty : "s")}"; var windowSummary = closeItems.Count == 0 ? string.Empty : $" and {closeItems.Count} application window{(closeItems.Count == 1 ? string.Empty : "s")}";
                var warning = $"WorkParcel will ask to close {tabSummary}{windowSummary} after saving. Some webpages may contain unsaved work. Review before closing.";
                if (!await Dialogs.Confirm(this, "CONFIRM CLOSE AFTER SAVE", warning, "SAVE AND CLOSE")) { await Dialogs.ShowMessage(this, "PARCEL SAVED", $"{selected.Count} ITEM{(selected.Count == 1 ? string.Empty : "S")} SAVED. No resource was closed."); Build(); return; }
                var closeResults = await _closer.RequestCloseAsync(closeItems); closed = closeResults.Count(result => result.Status == CloseRequestStatus.Closed); stillOpen = closeResults.Count(result => result.Status is CloseRequestStatus.StillOpen or CloseRequestStatus.Failed or CloseRequestStatus.Unsupported);
                foreach (var browserGroup in closeTabs.GroupBy(item => item.BrowserFamily ?? "chrome", StringComparer.OrdinalIgnoreCase)) { var browserResults = await BrowserIntegrationService.Current.CloseTabsAsync(browserGroup, browserGroup.Key); closed += browserResults.Count(result => result.Status == "Closed"); stillOpen += browserResults.Count(result => result.Status is "StillOpen" or "Stale" or "ConnectionLost" or "ConnectionError" or "Failed" or "Disabled" or "Unsupported"); }
            }
            await Dialogs.ShowMessage(this, "PARCEL PACKED", $"{selected.Count} ITEM{(selected.Count == 1 ? string.Empty : "S")} SAVED | {deskMessage} | {closed} RESOURCES CLOSED | {stillOpen} STILL OPEN"); Build();
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "PACK AWAY INCOMPLETE", "The selection could not be saved. No application was force-closed."); }
        finally { _busy = false; }
    }

    private async Task PackAwayLegacyAsync()
    {
        if (_parcel is null || _busy) return; _busy = true;
        try
        {
            var current = await _windows.DetectAsync(_parcel.Id); var candidates = _parcel.Items.ToList();
            foreach (var window in current) { var saved = candidates.FirstOrDefault(item => item.NormalizedIdentity is not null && string.Equals(item.NormalizedIdentity, window.NormalizedIdentity, StringComparison.OrdinalIgnoreCase)); if (saved is not null) { saved.RuntimeWindowHandle = window.RuntimeWindowHandle; saved.CloseSupported = true; } else candidates.Add(window); }
            var browserTabs = await BrowserIntegrationService.Current.ListTabsAsync(); foreach (var tab in browserTabs.Where(tab => BrowserTabRules.IsAllowedForCapture(tab))) { var candidate = BrowserIntegrationService.FromTab(_parcel.Id, tab, candidates.Count, null); var saved = candidates.FirstOrDefault(item => item.ItemType == ParcelItemType.BrowserTab && string.Equals(item.NormalizedIdentity, candidate.NormalizedIdentity, StringComparison.Ordinal)); if (saved is not null) { saved.BrowserSessionTabId = tab.SessionTabId; saved.BrowserSessionWindowId = tab.SessionWindowId; saved.BrowserConnectionId = tab.ConnectionId; saved.BrowserTabIndex = tab.TabIndex; saved.BrowserPinned = tab.Pinned; saved.BrowserActive = tab.Active; saved.BrowserTabGroupId = BrowserTabRules.StableGroupIdentity(tab.Browser, tab.WindowGroupKey, tab.GroupTitle, tab.GroupColor); saved.BrowserTabGroupTitle = tab.GroupTitle; saved.BrowserTabGroupColor = tab.GroupColor; saved.LaunchEnabled = tab.CanRestore; saved.IsInaccessible = !tab.CanRestore; saved.CloseSupported = true; } else { candidate.CloseSupported = true; candidates.Add(candidate); } }
            var checks = new Dictionary<ParcelItem, CheckBox>(); var stack = Ui.Stack(5); stack.Children.Add(Ui.Text("Choose what remains saved. Saving does not close anything unless you choose the close option.", 12, false, "#8D9CA2")); var summary = Ui.Mono(string.Empty, 10, "#9BE28F", true); stack.Children.Add(summary);
            foreach (var item in candidates)
            {
                var saved = _parcel.Items.Any(existing => existing.Id == item.Id);
                var closeReady = item.ItemType == ParcelItemType.BrowserTab ? item.CloseSupported && !string.IsNullOrWhiteSpace(item.BrowserSessionTabId) && !string.IsNullOrWhiteSpace(item.BrowserSessionWindowId) : item.RuntimeWindowHandle != nint.Zero && item.CloseSupported;
                var source = saved ? "SAVED" : "NEW DETECTED";
                var closeState = closeReady ? "CLOSE READY" : "CANNOT CLOSE";
                var box = new CheckBox { Content = $"{source}   {item.DisplayName}   [{item.TypeLabel}]   {item.AvailabilityLabel}   [{closeState}]", IsChecked = true };
                checks[item] = box; stack.Children.Add(box);
            }
            void UpdateSummary() { var selectedNow = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); var selectedNowIds = selectedNow.Select(item => item.Id).ToHashSet(); var additions = selectedNow.Count(item => _parcel.Items.All(saved => saved.Id != item.Id)); var removalsNow = _parcel.Items.Count(item => !selectedNowIds.Contains(item.Id)); var missing = selectedNow.Count(item => item.IsMissing || item.IsInaccessible); summary.Text = $"{selectedNow.Count} SAVED  |  {additions} NEW  |  {removalsNow} REMOVED  |  {missing} UNAVAILABLE"; }
            foreach (var box in checks.Values) { box.Checked += (_, _) => UpdateSummary(); box.Unchecked += (_, _) => UpdateSummary(); } UpdateSummary();
            var dialog = new ContentDialog { Title = "PACK AWAY", Content = new ScrollViewer { Content = stack, MaxHeight = 420 }, PrimaryButtonText = "SAVE SELECTION", SecondaryButtonText = BrowserIntegrationService.Current.TabClosingEnabled ? "SAVE AND CLOSE SELECTED" : "TAB CLOSING DISABLED", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, IsSecondaryButtonEnabled = BrowserIntegrationService.Current.TabClosingEnabled };
            var choice = await Ui.ShowDialog(dialog); if (choice == ContentDialogResult.None) return; var selected = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); var selectedIds = selected.Select(item => item.Id).ToHashSet(); var removals = _parcel.Items.Count(item => !selectedIds.Contains(item.Id));
            if (removals > 0 && !await Dialogs.Confirm(this, "REMOVE SAVED ITEM RECORDS?", $"{removals} WorkParcel record{(removals == 1 ? string.Empty : "s")} will be removed. External resources stay untouched.", "REMOVE AND SAVE")) return;
            var closeItems = selected.Where(item => item.RuntimeWindowHandle != nint.Zero && item.CloseSupported).ToList(); var closeTabs = selected.Where(item => item.ItemType == ParcelItemType.BrowserTab && item.CloseSupported && !string.IsNullOrWhiteSpace(item.BrowserSessionTabId) && !string.IsNullOrWhiteSpace(item.BrowserSessionWindowId)).ToList();
            // The close confirmation is intentionally shown after the saved selection is committed below.
            await Store.ReplaceItemsAsync(_parcel, selected, choice == ContentDialogResult.Secondary ? $"Parcel packed - {selected.Count} items saved; graceful close requested" : $"Parcel packed - {selected.Count} items saved"); var closed = 0; var stillOpen = 0;
            if (choice == ContentDialogResult.Secondary)
            {
                var tabSummary = $"{closeTabs.Count} browser tab{(closeTabs.Count == 1 ? string.Empty : "s")}";
                var windowSummary = closeItems.Count == 0 ? string.Empty : $" and {closeItems.Count} application window{(closeItems.Count == 1 ? string.Empty : "s")}";
                var warning = $"WorkParcel will ask to close {tabSummary}{windowSummary} after saving. Some webpages may contain unsaved work. WorkParcel cannot detect every unsaved form or online editor. Review your selected tabs before closing.";
                if (!await Dialogs.Confirm(this, "CONFIRM CLOSE AFTER SAVE", warning, "SAVE AND CLOSE")) { await Dialogs.ShowMessage(this, "PARCEL SAVED", $"{selected.Count} ITEM{(selected.Count == 1 ? string.Empty : "S")} SAVED. No browser tab or application window was closed."); Build(); return; }
            }
            if (choice == ContentDialogResult.Secondary) { var closeResults = await _closer.RequestCloseAsync(closeItems); closed = closeResults.Count(result => result.Status == CloseRequestStatus.Closed); stillOpen = closeResults.Count(result => result.Status == CloseRequestStatus.StillOpen || result.Status == CloseRequestStatus.Failed); foreach (var browserGroup in closeTabs.GroupBy(item => item.BrowserFamily ?? "chrome", StringComparer.OrdinalIgnoreCase)) { var browserResults = await BrowserIntegrationService.Current.CloseTabsAsync(browserGroup, browserGroup.Key); closed += browserResults.Count(result => result.Status == "Closed"); stillOpen += browserResults.Count(result => result.Status is "StillOpen" or "Stale" or "ConnectionLost" or "ConnectionError" or "Failed" or "Disabled" or "Unsupported"); } }
            await Dialogs.ShowMessage(this, "PARCEL PACKED", $"{selected.Count} ITEM{(selected.Count == 1 ? string.Empty : "S")} SAVED | {closed} RESOURCES CLOSED | {stillOpen} STILL OPEN"); Build();
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "PACK AWAY INCOMPLETE", "The selection could not be saved. No application was force-closed."); }
        finally { _busy = false; }
    }

    private async Task EditItemAsync(ParcelItem item)
    {
        var backup = ItemBackup.Take(item);
        if (_parcel is null) return; var name = new TextBox { Header = "DISPLAY NAME", Text = item.DisplayName, MaxLength = 160 }; var extra = new TextBox { Header = item.ItemType == ParcelItemType.Note ? "NOTE" : item.ItemType == ParcelItemType.WebLink ? "HTTP/HTTPS URL" : "DETAIL", Text = item.ItemType == ParcelItemType.Note ? item.NoteContent : item.ItemType == ParcelItemType.WebLink ? item.Value : item.SecondaryDetail, AcceptsReturn = item.ItemType == ParcelItemType.Note, TextWrapping = TextWrapping.Wrap, Height = item.ItemType == ParcelItemType.Note ? 130 : double.NaN }; var stack = Ui.Stack(9); stack.Children.Add(name); stack.Children.Add(extra); var dialog = new ContentDialog { Title = "EDIT ITEM", Content = stack, PrimaryButtonText = "SAVE CHANGES", CloseButtonText = "CANCEL", XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += (sender, args) => { if (string.IsNullOrWhiteSpace(name.Text) || item.ItemType == ParcelItemType.WebLink && !WebLinkRules.TryNormalize(extra.Text, out _)) { args.Cancel = true; extra.Description = "Enter the required valid value."; } };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return; item.DisplayName = name.Text.Trim(); if (item.ItemType == ParcelItemType.Note) item.NoteContent = (extra.Text ?? string.Empty).Trim(); else if (item.ItemType == ParcelItemType.WebLink && WebLinkRules.TryNormalize(extra.Text, out var uri)) { item.Value = uri.AbsoluteUri; item.NormalizedIdentity = uri.AbsoluteUri; } else item.SecondaryDetail = (extra.Text ?? string.Empty).Trim();
        try { await Store.UpdateItemAsync(_parcel, item); Build(); } catch (Exception exception) { backup.Restore(item); AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "ITEM WAS NOT SAVED", "The change could not be written. The previous item details were restored."); Build(); }
    }

    private async Task RemoveItemAsync(ParcelItem item)
    {
        var parcel = _parcel;
        if (parcel is null || _removalInProgress || _busy) return;
        _removalInProgress = true;
        try
        {
            ConfirmedOperationResult result;
            try
            {
                result = await Dialogs.ConfirmAndRunAsync(this, "REMOVE ITEM FROM PARCEL?", "Only the WorkParcel record will be removed. The original file, folder, application or link will not be deleted.", "REMOVE RECORD", () => Store.RemoveItemAsync(parcel, item));
            }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                await Dialogs.ShowMessage(this, "REMOVAL FAILED", "The database could not remove this record. It is still in this parcel, and the original resource was not changed. Try again.");
                return;
            }

            if (!result.Confirmed) return;
            if (!result.Succeeded)
            {
                await Dialogs.ShowMessage(this, "ITEM WAS NOT REMOVED", "The saved record is still in this parcel. Try again.");
                return;
            }

            _selectedItemIds.Remove(item.Id);
            try { Build(); }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); RefreshRows(); }
            await Dialogs.ShowMessage(this, "ITEM REMOVED", "The WorkParcel record was removed. The original resource was not changed.");
        }
        finally { _removalInProgress = false; }
    }

    private async Task RemoveSelectedAsync()
    {
        var parcel = _parcel;
        if (parcel is null || _removalInProgress || _busy) return;
        var selected = parcel.Items.Where(item => _selectedItemIds.Contains(item.Id)).ToList(); if (selected.Count == 0) { await Dialogs.ShowMessage(this, "NO ITEMS SELECTED", "Select one or more saved items first."); return; }
        _removalInProgress = true;
        try
        {
            ConfirmedOperationResult result;
            try
            {
                result = await Dialogs.ConfirmAndRunAsync(this, "REMOVE SELECTED ITEMS?", $"Remove {selected.Count} saved item{(selected.Count == 1 ? string.Empty : "s")} from WorkParcel? Original files, folders, applications, tabs and links stay untouched.", "REMOVE RECORDS", async () => await Store.RemoveItemsAsync(parcel, selected) == selected.Count);
            }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                await Dialogs.ShowMessage(this, "REMOVAL FAILED", "The database could not remove the selected records. They are still in this parcel, and the original resources were not changed. Try again.");
                return;
            }

            if (!result.Confirmed) return;
            if (!result.Succeeded)
            {
                await Dialogs.ShowMessage(this, "ITEMS WERE NOT REMOVED", "The selected records are still in this parcel. Try again.");
                return;
            }

            _selectedItemIds.Clear();
            try { Build(); }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); RefreshRows(); }
            await Dialogs.ShowMessage(this, "ITEMS REMOVED", $"{selected.Count} saved record{(selected.Count == 1 ? string.Empty : "s")} removed. Original resources were not changed.");
        }
        finally { _removalInProgress = false; }
    }

    private async Task MoveBrowserGroupAsync(ParcelItem item)
    {
        if (_parcel is null || item.ItemType != ParcelItemType.BrowserTab) return;
        var group = new TextBox { Header = "SAVED WINDOW GROUP", Text = item.BrowserWindowGroupId ?? "window-0", MaxLength = 120, PlaceholderText = "window-0" };
        var dialog = new ContentDialog { Title = "MOVE BROWSER TAB", Content = group, PrimaryButtonText = "MOVE TAB", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(group.Text)) { args.Cancel = true; group.Description = "Enter a saved window group."; } };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        var previousGroup = item.BrowserWindowGroupId; var previousIdentity = item.NormalizedIdentity; var previousTabGroupId = item.BrowserTabGroupId;
        var next = group.Text.Trim(); item.BrowserWindowGroupId = next; item.NormalizedIdentity = BrowserTabRules.Identity(item.BrowserFamily ?? "chrome", next, item.Value); item.BrowserTabGroupId = BrowserTabRules.StableGroupIdentity(item.BrowserFamily ?? "chrome", next, item.BrowserTabGroupTitle, item.BrowserTabGroupColor); item.UpdatedAt = DateTime.Now;
        try { await Store.UpdateItemAsync(_parcel, item, ParcelHistoryEventType.ItemEdited, "Browser tab moved to another saved window group"); Build(); } catch (Exception exception) { item.BrowserWindowGroupId = previousGroup; item.NormalizedIdentity = previousIdentity; item.BrowserTabGroupId = previousTabGroupId; AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "TAB WAS NOT MOVED", "The saved tab stayed in its previous window group."); }
    }

    private async Task RelinkAsync(ParcelItem item)
    {
        if (_parcel is null) return; var backup = ItemBackup.Take(item); try
        {
            ParcelItem replacement; if (item.ItemType == ParcelItemType.File) { var paths = await _pickers.PickFilesAsync(); if (paths.Count != 1) return; replacement = await _factory.FileAsync(_parcel.Id, paths[0], item.SortOrder); } else if (item.ItemType == ParcelItemType.Folder) { var path = await _pickers.PickFolderAsync(); if (path is null) return; replacement = _factory.Folder(_parcel.Id, path, item.SortOrder); } else { var path = await _pickers.PickApplicationAsync(); if (path is null) return; replacement = _factory.Application(_parcel.Id, path, item.SortOrder); }
            ParcelItemRelinker.Apply(item, replacement); await Store.UpdateItemAsync(_parcel, item, ParcelHistoryEventType.ItemRelinked, $"{item.TypeLabel} re-linked"); Build();
        }
        catch (Exception exception) { backup.Restore(item); AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "RE-LINK FAILED", "The replacement could not be saved. The original item record was restored."); Build(); }
    }

    private async Task AcceptChangedFileAsync(ParcelItem item)
    {
        if (_parcel is null || item.ItemType != ParcelItemType.File || !await Dialogs.Confirm(this, "ACCEPT CHANGED FILE?", "WorkParcel will update the saved size, modified time and fingerprint. It does not judge whether the file is safe.", "ACCEPT VERSION")) return;
        var backup = ItemBackup.Take(item); try { var updated = await _factory.FileAsync(_parcel.Id, item.Value, item.SortOrder); item.FileSize = updated.FileSize; item.FileModifiedAt = updated.FileModifiedAt; item.Fingerprint = updated.Fingerprint; item.HasChanged = false; item.LastVerifiedAt = DateTime.Now; await Store.UpdateItemAsync(_parcel, item, ParcelHistoryEventType.ChangedFileAccepted, "Changed file version accepted"); Build(); }
        catch (Exception exception) { backup.Restore(item); AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "VERSION WAS NOT ACCEPTED", "WorkParcel kept the previous file metadata. Check that the file is still accessible and try again."); Build(); }
    }

    private static Border HistoryRow(ParcelHistoryEntry entry) => new() { Child = Ui.Row(Ui.Mono(entry.Timestamp.ToString("HH:mm"), 10, "#9BE28F", true), Ui.Tag(entry.EventType.ToString().ToUpperInvariant(), "#8D9CA2"), Ui.Text(entry.Summary, 12)), Padding = new Thickness(8, 6, 8, 6), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };

    private sealed record ItemBackup(string Name, string Value, string? Identity, string? Detail, string? Note, string? Executable, string? Working, long? Size, DateTime? Modified, string? Fingerprint, bool Missing, bool Inaccessible, bool Changed, DateTime Updated)
    {
        public static ItemBackup Take(ParcelItem item) => new(item.DisplayName, item.Value, item.NormalizedIdentity, item.SecondaryDetail, item.NoteContent, item.ExecutablePath, item.WorkingDirectory, item.FileSize, item.FileModifiedAt, item.Fingerprint, item.IsMissing, item.IsInaccessible, item.HasChanged, item.UpdatedAt);
        public void Restore(ParcelItem item) { item.DisplayName = Name; item.Value = Value; item.NormalizedIdentity = Identity; item.SecondaryDetail = Detail; item.NoteContent = Note; item.ExecutablePath = Executable; item.WorkingDirectory = Working; item.FileSize = Size; item.FileModifiedAt = Modified; item.Fingerprint = Fingerprint; item.IsMissing = Missing; item.IsInaccessible = Inaccessible; item.HasChanged = Changed; item.UpdatedAt = Updated; }
    }
}
