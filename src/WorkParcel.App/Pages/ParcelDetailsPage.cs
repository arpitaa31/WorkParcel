using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _selectedItemIds.Clear();
        _parcel = e.Parameter switch
        {
            Guid id => FindParcel(id),
            Parcel parcel => FindParcel(parcel.Id),
            ParcelDetailsRequest request => FindParcel(request.ParcelId),
            _ => null
        };
        Build();
        if (_parcel is not null) _ = RefreshAvailabilityAsync(false);
        if (e.Parameter is ParcelDetailsRequest { StartPrimaryAction: true })
        {
            RoutedEventHandler? loaded = null;
            loaded = async (_, _) =>
            {
                Loaded -= loaded;
                if (_parcel?.Status == ParcelStatus.Open) await PackAwayAsync();
                else await OpenParcelAsync();
            };
            Loaded += loaded;
        }
    }

    private static Parcel? FindParcel(Guid id) => WorkspaceStore.Current.Parcels.Concat(WorkspaceStore.Current.Archived).FirstOrDefault(parcel => parcel.Id == id);

    private void Build()
    {
        Content = null;
        _itemsHost = Ui.Stack(2);
        _search = new TextBox { PlaceholderText = "Search parcel items", MinWidth = 220 };
        _filter = new ComboBox { MinWidth = 145, ItemsSource = new[] { "ALL", "APPS & WINDOWS", "WEB LINKS", "FILES", "FOLDERS", "NOTES" }, SelectedIndex = 0 };
        _sort = new ComboBox { MinWidth = 130, ItemsSource = new[] { "DATE ADDED", "NAME", "TYPE" }, SelectedIndex = 0 };
        _search.TextChanged += (_, _) => QueueSearchRefresh();
        _filter.SelectionChanged += (_, _) => RefreshRows();
        _sort.SelectionChanged += (_, _) => RefreshRows();
        Ui.Interactive(_search, 1.004f);
        Ui.Interactive(_filter, 1.01f);
        Ui.Interactive(_sort, 1.01f);

        if (_parcel is null)
        {
            SetContent(Ui.Card(Ui.Text("Parcel not found.", 14), 20));
            return;
        }

        var body = Body(15);
        body.Children.Add(BuildHeader());
        body.Children.Add(Ui.Rule());
        body.Children.Add(BuildTools());
        body.Children.Add(DropTarget());
        body.Children.Add(_itemsHost);
        RefreshRows();
        body.Children.Add(Ui.SectionHeader("PARCEL HISTORY", "Meaningful saved changes only."));
        if (_parcel.History.Count == 0) body.Children.Add(Ui.Text("No history yet.", 12, false, "#8D9CA2"));
        foreach (var entry in _parcel.History.OrderByDescending(entry => entry.Timestamp).Take(50)) body.Children.Add(HistoryRow(entry));
        SetContent(body);
    }

    private StackPanel BuildHeader()
    {
        var back = Ui.Button("<- BACK");
        back.Click += (_, _) => { if (Frame?.CanGoBack == true) Frame.GoBack(); };
        var edit = Ui.Button("EDIT PARCEL");
        edit.Click += async (_, _) => { await Dialogs.ShowEditParcelAsync(this, _parcel!); Build(); };
        var update = Ui.Button("UPDATE PARCEL");
        update.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel!.Name, _parcel.Description, _parcel.Id));
        var action = Ui.Button(_parcel!.Status == ParcelStatus.Open ? "PACK AWAY" : "OPEN PARCEL", _parcel.Status == ParcelStatus.Open);
        action.Click += async (_, _) => { if (_parcel.Status == ParcelStatus.Open) await PackAwayAsync(); else await OpenParcelAsync(); };
        var menu = Ui.IconButton("...", "Parcel actions");
        var flyout = new MenuFlyout();
        var archive = new MenuFlyoutItem { Text = "Archive" };
        archive.Click += async (_, _) =>
        {
            if (await Dialogs.Confirm(this, "ARCHIVE PARCEL", "Move this saved record to Archive?", "ARCHIVE"))
            {
                await Store.ArchiveAsync(_parcel!);
                if (Frame?.CanGoBack == true) Frame.GoBack();
            }
        };
        var delete = new MenuFlyoutItem { Text = "Delete permanently" };
        delete.Click += async (_, _) =>
        {
            if (await Dialogs.ConfirmPermanentDelete(this, _parcel!))
            {
                await Store.DeleteAsync(_parcel!);
                if (Frame?.CanGoBack == true) Frame.GoBack();
            }
        };
        flyout.Items.Add(archive);
        flyout.Items.Add(delete);
        menu.Flyout = flyout;

        var header = Ui.Stack(10);
        header.Children.Add(Ui.Row(back, Ui.Spacer(), edit, update, action, menu));
        header.Children.Add(Ui.Text(_parcel.Name, 28, true));
        if (!string.IsNullOrWhiteSpace(_parcel.Description)) header.Children.Add(Ui.Text(_parcel.Description, 13, false, "#8D9CA2"));
        header.Children.Add(Ui.Row(Ui.Tag(_parcel.StatusText, Ui.StateColor(_parcel.Status)), Ui.Mono(_parcel.ItemSummary, 10), Ui.Mono($"{_parcel.AvailableItemCount} AVAILABLE", 10, "#9BE28F"), Ui.Mono($"CREATED {_parcel.CreatedAt:g}".ToUpperInvariant(), 10)));
        return header;
    }

    private Grid BuildTools()
    {
        var grid = new Grid { ColumnSpacing = 9, RowSpacing = 9 };
        var refresh = Ui.Button(">> AVAILABILITY");
        refresh.Click += async (_, _) => await RefreshAvailabilityAsync(true);
        var add = Ui.Button("+ ADD ITEM");
        add.Click += (_, _) => Frame?.Navigate(typeof(CapturePage), new CaptureSeed(_parcel!.Name, _parcel.Description, _parcel.Id));
        var addLink = Ui.Button("ADD LINK MANUALLY");
        addLink.Click += async (_, _) => await AddManualLinkAsync();
        var pasteLinks = Ui.Button("PASTE LINKS");
        pasteLinks.Click += async (_, _) => await PasteLinksAsync();
        var removeSelected = Ui.Button("REMOVE SELECTED");
        removeSelected.Click += async (_, _) => await RemoveSelectedAsync();
        var controls = new FrameworkElement[] { _search, _filter, _sort, refresh, add, addLink, pasteLinks, removeSelected };
        foreach (var control in controls) grid.Children.Add(control);

        void Arrange(double width)
        {
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            var columns = width >= 1280 ? 8 : width >= 900 ? 4 : 3;
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var rows = width >= 1280 ? 1 : width >= 900 ? 3 : 4;
            for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (width >= 1280)
            {
                for (var i = 0; i < controls.Length; i++) Place(controls[i], 0, i);
            }
            else if (width >= 900)
            {
                Place(_search, 0, 0, 4);
                Place(_filter, 1, 0); Place(_sort, 1, 1); Place(refresh, 1, 2); Place(removeSelected, 1, 3);
                Place(add, 2, 0); Place(addLink, 2, 1); Place(pasteLinks, 2, 2);
            }
            else
            {
                Place(_search, 0, 0, 3);
                Place(_filter, 1, 0); Place(_sort, 1, 1); Place(refresh, 1, 2);
                Place(add, 2, 0); Place(addLink, 2, 1); Place(pasteLinks, 2, 2);
                Place(removeSelected, 3, 1, 2);
            }
        }

        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        Arrange(1000);
        return grid;
    }

    private static void Place(FrameworkElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
    }

    private void RefreshRows()
    {
        if (_parcel is null) return;
        _itemsHost.Children.Clear();
        var group = (ParcelItemGroup)Math.Clamp(_filter.SelectedIndex, 0, 5);
        var sort = (ParcelItemSort)Math.Clamp(_sort.SelectedIndex, 0, 2);
        var list = ParcelItemQueries.Apply(_parcel.Items, new ParcelItemQuery(_search.Text, group, sort));
        if (list.Count == 0)
        {
            var empty = Ui.Stack(5);
            empty.Children.Add(Ui.Mono(_parcel.ItemCount == 0 ? "EMPTY SETUP" : "NO MATCHES", 11, "#9BE28F", true));
            empty.Children.Add(Ui.Text(_parcel.ItemCount == 0 ? "Use Add Item or add Web Links to build this parcel." : "Try another search or filter.", 12, false, "#8D9CA2"));
            _itemsHost.Children.Add(Ui.Card(empty, 18));
            return;
        }
        foreach (var item in list.Take(300)) _itemsHost.Children.Add(ItemRow(item));
        if (list.Count > 300) _itemsHost.Children.Add(Ui.Card(Ui.Text($"SHOWING 300 OF {list.Count} ITEMS - refine the search or filter to narrow this list.", 11, false, "#8D9CA2"), 11));
    }

    private async void QueueSearchRefresh()
    {
        var version = ++_searchVersion;
        await Task.Delay(180);
        if (version == _searchVersion) RefreshRows();
    }

    private Border ItemRow(ParcelItem item)
    {
        var glyph = Ui.ItemIcon(item);
        var copy = Ui.Stack(2);
        copy.Children.Add(Ui.Text(item.DisplayName, 12, true));
        copy.Children.Add(Ui.Mono(item.SecondaryDetail ?? (item.ItemType == ParcelItemType.Note ? "Saved inside WorkParcel" : item.Value), 9));
        var state = Ui.Stack(2);
        state.Children.Add(Ui.Mono(item.TypeLabel, 9, "#9BE28F", true));
        state.Children.Add(Ui.Mono(item.AvailabilityLabel, 9, item.IsMissing || item.IsInaccessible ? "#F0B45B" : null));
        state.Children.Add(Ui.Mono($"CHECKED {Ui.Relative(item.LastVerifiedAt)}", 8));
        var check = new CheckBox { IsChecked = _selectedItemIds.Contains(item.Id), VerticalAlignment = VerticalAlignment.Center, UseSystemFocusVisuals = true };
        check.Checked += (_, _) => _selectedItemIds.Add(item.Id);
        check.Unchecked += (_, _) => _selectedItemIds.Remove(item.Id);
        var grid = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        grid.Children.Add(check);
        Grid.SetColumn(glyph, 1); grid.Children.Add(glyph);
        Grid.SetColumn(copy, 2); grid.Children.Add(copy);
        Grid.SetColumn(state, 3); grid.Children.Add(state);
        var canRelink = item.IsMissing && item.ItemType is ParcelItemType.File or ParcelItemType.Folder or ParcelItemType.Application;
        var canOpen = item.LaunchEnabled && !item.IsMissing && !item.IsInaccessible;
        var open = Ui.Button(item.ItemType == ParcelItemType.Note ? "EDIT" : canRelink ? "LOCATE AGAIN" : canOpen ? "OPEN" : "UNAVAILABLE");
        open.IsEnabled = item.ItemType == ParcelItemType.Note || canRelink || canOpen;
        open.Click += async (_, _) => { if (canRelink) await RelinkAsync(item); else if (item.ItemType == ParcelItemType.Note) await EditItemAsync(item); else await OpenOneAsync(item); };
        Grid.SetColumn(open, 4); grid.Children.Add(open);
        var more = Ui.IconButton("...", "Item actions");
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit" }; edit.Click += async (_, _) => await EditItemAsync(item);
        var remove = new MenuFlyoutItem { Text = "Remove from parcel" }; remove.Click += async (_, _) => await RemoveItemAsync(item);
        menu.Items.Add(edit);
        if (item.HasChanged)
        {
            var accept = new MenuFlyoutItem { Text = "Accept changed file" }; accept.Click += async (_, _) => await AcceptChangedFileAsync(item); menu.Items.Add(accept);
        }
        menu.Items.Add(remove);
        more.Flyout = menu;
        Grid.SetColumn(more, 5); grid.Children.Add(more);
        return Ui.Interactive(new Border { Child = grid, Padding = new Thickness(9, 8, 7, 8), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Resource("SurfaceBrush") });
    }

    private async Task RefreshAvailabilityAsync(bool showResult)
    {
        if (_parcel is null || _busy || _removalInProgress) return;
        _busy = true;
        try
        {
            await Store.VerifyItemsAsync(_parcel);
            Build();
            if (showResult) await Dialogs.ShowMessage(this, "AVAILABILITY CHECKED", $"{_parcel.AvailableItemCount} AVAILABLE | {_parcel.MissingItemCount} MISSING");
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            if (showResult) await Dialogs.ShowMessage(this, "CHECK INCOMPLETE", "Some items could not be verified. Their saved records were kept.");
        }
        finally { _busy = false; }
    }

    private Border DropTarget()
    {
        var target = Ui.Card(Ui.Mono("DROP FILES OR FOLDERS TO ATTACH", 10, "#8D9CA2", true), 10); target.AllowDrop = true;
        target.DragOver += (_, args) => { if (args.DataView.Contains(StandardDataFormats.StorageItems)) { args.AcceptedOperation = DataPackageOperation.Copy; args.DragUIOverride.Caption = "Add without moving the original"; } };
        target.DragEnter += (_, _) => target.BorderBrush = Ui.Resource("AccentBrush");
        target.DragLeave += (_, _) => target.BorderBrush = Ui.Resource("BorderBrush");
        target.Drop += async (_, args) => { target.BorderBrush = Ui.Resource("BorderBrush"); await AttachDropAsync(args.DataView); };
        return target;
    }

    private async Task AttachDropAsync(DataPackageView data)
    {
        if (_parcel is null || !data.Contains(StandardDataFormats.StorageItems) || _busy) return;
        _busy = true;
        var items = new List<ParcelItem>(); var failed = 0;
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
            if (items.Count > 0) await Store.AddItemsAsync(_parcel, items);
            Build();
            if (failed > 0) await Dialogs.ShowMessage(this, "DROP PARTLY ADDED", $"{items.Count} ADDED | {failed} DUPLICATE OR UNSUPPORTED");
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "DROP NOT SAVED", "The dropped items could not be written. Existing parcel records were kept."); }
        finally { _busy = false; }
    }

    private async Task AddManualLinkAsync()
    {
        if (_parcel is null || _busy) return;
        var title = new TextBox { Header = "DISPLAY NAME - OPTIONAL", MaxLength = 160 };
        var url = new TextBox { Header = "HTTP/HTTPS URL", PlaceholderText = "https://" };
        var note = new TextBox { Header = "SHORT NOTE - OPTIONAL", MaxLength = 240 };
        var stack = Ui.Stack(9); stack.Children.Add(title); stack.Children.Add(url); stack.Children.Add(note);
        var dialog = new ContentDialog { Title = "ADD WEB LINK", Content = stack, PrimaryButtonText = "ADD LINK", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += (_, args) => { if (!WebLinkRules.TryNormalize(url.Text, out var ignoredUri)) { args.Cancel = true; url.Description = "Enter a valid HTTP or HTTPS link."; } };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var item = _factory.WebLink(_parcel.Id, url.Text, title.Text, note.Text, _parcel.ItemCount);
            if (ParcelItemIdentity.IsDuplicate(_parcel.Items, item)) { await Dialogs.ShowMessage(this, "LINK ALREADY SAVED", "That URL is already saved in this parcel."); return; }
            await Store.AddItemsAsync(_parcel, new[] { item });
            Build();
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "LINK WAS NOT SAVED", "The link could not be added. Check that it is a valid HTTP or HTTPS URL."); }
    }

    private async Task PasteLinksAsync()
    {
        if (_parcel is null || _busy) return;
        var input = new TextBox { Header = "ONE HTTP/HTTPS LINK PER LINE", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170 };
        var dialog = new ContentDialog { Title = "ADD WEB LINKS", Content = input, PrimaryButtonText = "ADD LINKS", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        var parsed = WebLinkRules.ParseMany(input.Text);
        if (parsed.Valid.Count == 0) { await Dialogs.ShowMessage(this, "NO VALID LINKS", "Enter at least one HTTP or HTTPS link. Nothing was added."); return; }
        if (parsed.Invalid.Count > 0 && !await Dialogs.Confirm(this, "INVALID LINKS FOUND", $"These lines are not HTTP/HTTPS links and will not be saved:\n\n{string.Join("\n", parsed.Invalid.Select((line, index) => $"Line {parsed.InvalidLineNumbers[index]}: {line}").Take(8))}{(parsed.Invalid.Count > 8 ? "\n..." : string.Empty)}\n\nAdd the {parsed.Valid.Count} valid link{(parsed.Valid.Count == 1 ? string.Empty : "s")} only?", "ADD VALID LINKS")) return;
        var added = new List<ParcelItem>(); var duplicates = 0;
        foreach (var link in parsed.Valid)
        {
            var item = _factory.WebLink(_parcel.Id, link.AbsoluteUri, null, null, _parcel.ItemCount + added.Count);
            if (ParcelItemIdentity.IsDuplicate(_parcel.Items.Concat(added), item)) { duplicates++; continue; }
            added.Add(item);
        }
        if (added.Count == 0) { await Dialogs.ShowMessage(this, "LINKS ALREADY SAVED", $"{duplicates} link{(duplicates == 1 ? string.Empty : "s")} already exist{(duplicates == 1 ? "s" : string.Empty)} in this parcel."); return; }
        try { await Store.AddItemsAsync(_parcel, added); Build(); if (duplicates > 0) await Dialogs.ShowMessage(this, "LINKS PARTLY ADDED", $"{added.Count} ADDED | {duplicates} DUPLICATE{(duplicates == 1 ? string.Empty : "S")} SKIPPED"); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "LINKS WERE NOT SAVED", "The pasted links could not be written. Existing parcel records were kept."); }
    }

    private async Task OpenOneAsync(ParcelItem item)
    {
        try
        {
            await _availability.VerifyAsync(item);
            if (item.IsMissing || item.IsInaccessible) { RefreshRows(); await Dialogs.ShowMessage(this, "ITEM NOT AVAILABLE", "Locate or restore this item before opening it."); return; }
            var current = item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow ? await _windows.DetectAsync(_parcel!.Id) : Array.Empty<ParcelItem>();
            var result = (await _launcher.OpenAsync(new[] { item }, current)).Single();
            if (result.Status is not ItemOpenStatus.Opened and not ItemOpenStatus.AlreadyOpen) await Dialogs.ShowMessage(this, "ITEM DID NOT OPEN", result.Message);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "ITEM OPEN INCOMPLETE", "WorkParcel could not finish opening this item. The saved record was kept unchanged where possible."); }
    }

    private async Task OpenParcelAsync()
    {
        if (_parcel is null || _busy) return;
        var parcel = _parcel;
        var checks = new Dictionary<ParcelItem, CheckBox>();
        var stack = Ui.Stack(5);
        var controls = Ui.Row(); var all = Ui.Button("SELECT ALL SUPPORTED"); var clear = Ui.Button("CLEAR ALL"); var selectedCount = Ui.Mono("0 SELECTED", 10, "#9BE28F", true);
        controls.Children.Add(all); controls.Children.Add(clear); controls.Children.Add(Ui.Spacer()); controls.Children.Add(selectedCount); stack.Children.Add(controls);
        stack.Children.Add(Ui.Text("Web links open in the Windows default browser. WorkParcel does not restore browser sessions, tab history, or exact window positions.", 12, false, "#8D9CA2"));
        foreach (var item in parcel.Items)
        {
            var validLink = item.ItemType != ParcelItemType.WebLink || WebLinkRules.TryNormalize(item.Value, out _);
            var supported = item.LaunchEnabled && !item.IsMissing && !item.IsInaccessible && item.ItemType != ParcelItemType.Note && validLink;
            var availability = !validLink ? "INVALID WEB LINK" : item.AvailabilityLabel;
            var box = new CheckBox { Content = $"{item.DisplayName}   [{item.TypeLabel}]   {availability}", IsChecked = supported, IsEnabled = supported };
            checks[item] = box; stack.Children.Add(box);
        }
        var dialog = new ContentDialog { Title = "OPEN PARCEL", Content = new ScrollViewer { Content = stack, MaxHeight = 600 }, PrimaryButtonText = "OPEN SELECTED ITEMS", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        void UpdateOpenButton() { var count = checks.Count(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true); dialog.IsPrimaryButtonEnabled = count > 0; selectedCount.Text = $"{count} SELECTED"; }
        foreach (var box in checks.Values) { box.Checked += (_, _) => UpdateOpenButton(); box.Unchecked += (_, _) => UpdateOpenButton(); }
        all.Click += (_, _) => { foreach (var pair in checks.Where(pair => pair.Value.IsEnabled)) pair.Value.IsChecked = true; UpdateOpenButton(); };
        clear.Click += (_, _) => { foreach (var pair in checks) pair.Value.IsChecked = false; UpdateOpenButton(); };
        dialog.Opened += (_, _) => UpdateOpenButton();
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        var selected = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); if (selected.Count == 0) return;
        _busy = true;
        try
        {
            var current = await _windows.DetectAsync(parcel.Id);
            var results = (await _launcher.OpenAsync(selected, current)).ToList();
            var opened = results.Count(result => result.Status is ItemOpenStatus.Opened or ItemOpenStatus.AlreadyOpen); var failed = results.Count - opened;
            if (opened > 0) await Store.SetStateAsync(parcel, ParcelStatus.Open, $"Parcel opened - {opened} opened or already open | {failed} skipped or failed");
            var lines = results.Select(result => $"{selected.FirstOrDefault(item => item.Id == result.ItemId)?.DisplayName ?? result.ItemId.ToString()}   {result.Status.ToString().ToUpperInvariant()}");
            await Dialogs.ShowMessage(this, "OPEN PARCEL FINISHED", $"{opened} OPENED / ALREADY OPEN | {failed} SKIPPED OR FAILED\n\n{string.Join("\n", lines)}");
            Build();
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
            var current = await _windows.DetectAsync(parcel.Id);
            var candidates = parcel.Items.ToList();
            foreach (var savedItem in candidates.Where(item => item.ItemType == ParcelItemType.ApplicationWindow)) { savedItem.RuntimeWindowHandle = nint.Zero; savedItem.RuntimeProcessId = 0; }
            var matchedWindows = OpenWindowService.MatchSavedItemsByIdentity(candidates, current);
            var matchedHandles = new HashSet<nint>();
            foreach (var match in matchedWindows.Where(match => match.Live is not null))
            {
                var live = current.FirstOrDefault(item => item.RuntimeWindowHandle == match.Live!.Handle);
                if (live is null) continue;
                match.Item.RuntimeWindowHandle = live.RuntimeWindowHandle; match.Item.RuntimeProcessId = live.RuntimeProcessId; match.Item.CloseSupported = true; match.Item.WindowClassName = live.WindowClassName; matchedHandles.Add(live.RuntimeWindowHandle);
            }
            foreach (var window in current.Where(window => !matchedHandles.Contains(window.RuntimeWindowHandle))) candidates.Add(window);

            var checks = new Dictionary<ParcelItem, CheckBox>();
            var stack = Ui.Stack(5);
            stack.Children.Add(Ui.Text("Choose what remains saved. Saving does not close anything. Saved web links are records only and never close a browser.", 12, false, "#8D9CA2"));
            var summary = Ui.Mono(string.Empty, 10, "#9BE28F", true); stack.Children.Add(summary);
            foreach (var item in candidates)
            {
                var saved = parcel.Items.Any(existing => existing.Id == item.Id);
                var closeReady = WindowCloseRequestService.IsSafeCloseTarget(item);
                var source = saved ? "SAVED" : "NEW APP WINDOW";
                var closeState = closeReady ? "CLOSE READY" : "CANNOT CLOSE";
                var box = new CheckBox { Content = $"{source}   {item.DisplayName}   [{item.TypeLabel}]   {item.AvailabilityLabel}   [{closeState}]", IsChecked = saved };
                checks[item] = box; stack.Children.Add(box);
            }
            void UpdateSummary()
            {
                var selectedNow = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
                var selectedNowIds = selectedNow.Select(item => item.Id).ToHashSet();
                var additions = selectedNow.Count(item => parcel.Items.All(saved => saved.Id != item.Id));
                var removalsNow = parcel.Items.Count(item => !selectedNowIds.Contains(item.Id));
                var missing = selectedNow.Count(item => item.IsMissing || item.IsInaccessible);
                summary.Text = $"{selectedNow.Count} SAVED  |  {additions} NEW  |  {removalsNow} REMOVED  |  {missing} UNAVAILABLE";
            }
            foreach (var box in checks.Values) { box.Checked += (_, _) => UpdateSummary(); box.Unchecked += (_, _) => UpdateSummary(); }
            UpdateSummary();
            var closeableWindowCount = candidates.Count(item => item.ItemType == ParcelItemType.ApplicationWindow && WindowCloseRequestService.IsSafeCloseTarget(item));
            var dialog = new ContentDialog { Title = "PACK AWAY", Content = new ScrollViewer { Content = stack, MaxHeight = 520 }, PrimaryButtonText = "SAVE SELECTION", SecondaryButtonText = "SAVE AND CLOSE SELECTED WINDOWS", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, IsSecondaryButtonEnabled = closeableWindowCount > 0 };
            var choice = await Ui.ShowDialog(dialog); if (choice == ContentDialogResult.None) return;
            var selected = checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(); var selectedIds = selected.Select(item => item.Id).ToHashSet(); var removals = parcel.Items.Count(item => !selectedIds.Contains(item.Id));
            if (removals > 0 && !await Dialogs.Confirm(this, "REMOVE SAVED ITEM RECORDS?", $"{removals} WorkParcel record{(removals == 1 ? string.Empty : "s")} will be removed. External resources stay untouched.", "REMOVE AND SAVE")) return;
            var closeItems = _closer.BuildPlan(selected);
            var closed = 0; var stillOpen = 0;
            if (choice == ContentDialogResult.Secondary)
            {
                var browserWindows = closeItems.Count(IsBrowserWindow);
                var browserWarning = browserWindows == 0 ? string.Empty : $" This includes {browserWindows} selected Chrome or Edge window{(browserWindows == 1 ? string.Empty : "s")}; closing a browser window may close all tabs in that window.";
                var warning = $"WorkParcel will ask Windows to close {closeItems.Count} selected application window{(closeItems.Count == 1 ? string.Empty : "s")} before marking the parcel packed.{browserWarning} Some applications may have unsaved work. Review each window before closing.";
                if (!await Dialogs.Confirm(this, "CONFIRM CLOSE AFTER SAVE", warning, "SAVE AND CLOSE"))
                {
                    await Store.ReplaceItemsAsync(parcel, selected, $"Parcel packed - {selected.Count} items saved");
                    await Dialogs.ShowMessage(this, "PARCEL SAVED", $"{selected.Count} ITEM{(selected.Count == 1 ? string.Empty : "S")} SAVED. No resource was closed.");
                    Build();
                    return;
                }
                var closeResults = await _closer.RequestCloseAsync(closeItems);
                closed = closeResults.Count(result => result.Status == CloseRequestStatus.Closed);
                stillOpen = closeResults.Count(result => result.Status is CloseRequestStatus.StillOpen or CloseRequestStatus.Failed or CloseRequestStatus.Unsupported);
            }
            await Store.ReplaceItemsAsync(parcel, selected, choice == ContentDialogResult.Secondary ? $"Parcel packed - {selected.Count} items saved; close requested" : $"Parcel packed - {selected.Count} items saved");
            await Dialogs.ShowMessage(this, "PARCEL PACKED", $"{selected.Count} ITEM{(selected.Count == 1 ? string.Empty : "S")} SAVED | {closed} WINDOWS CLOSED | {stillOpen} STILL OPEN");
            Build();
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "PACK AWAY INCOMPLETE", "The selection could not be saved. No application was force-closed."); }
        finally { _busy = false; }
    }

    private static bool IsBrowserWindow(ParcelItem item) => item.ItemType == ParcelItemType.ApplicationWindow && item.ProcessName is "chrome" or "msedge";

    private async Task EditItemAsync(ParcelItem item)
    {
        if (_parcel is null) return;
        var backup = ItemBackup.Take(item);
        var name = new TextBox { Header = "DISPLAY NAME", Text = item.DisplayName, MaxLength = 160 };
        var extra = new TextBox { Header = item.ItemType == ParcelItemType.Note ? "NOTE" : item.ItemType == ParcelItemType.WebLink ? "HTTP/HTTPS URL" : "DETAIL", Text = item.ItemType == ParcelItemType.Note ? item.NoteContent : item.ItemType == ParcelItemType.WebLink ? item.Value : item.SecondaryDetail, AcceptsReturn = item.ItemType == ParcelItemType.Note, TextWrapping = TextWrapping.Wrap, Height = item.ItemType == ParcelItemType.Note ? 130 : double.NaN };
        var stack = Ui.Stack(9); stack.Children.Add(name); stack.Children.Add(extra);
        var dialog = new ContentDialog { Title = item.ItemType == ParcelItemType.WebLink ? "EDIT WEB LINK" : "EDIT ITEM", Content = stack, PrimaryButtonText = "SAVE CHANGES", CloseButtonText = "CANCEL", XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (item.ItemType == ParcelItemType.WebLink)
            {
                if (!WebLinkRules.TryNormalize(extra.Text, out var ignoredUri)) { args.Cancel = true; extra.Description = "Enter a valid HTTP or HTTPS link."; }
            }
            else if (string.IsNullOrWhiteSpace(name.Text)) { args.Cancel = true; name.Description = "Enter a display name."; }
        };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        if (item.ItemType == ParcelItemType.WebLink && WebLinkRules.TryNormalize(extra.Text, out var uri))
        {
            item.DisplayName = string.IsNullOrWhiteSpace(name.Text) ? uri.Host : name.Text.Trim();
            item.Value = uri.AbsoluteUri;
            item.NormalizedIdentity = WebLinkRules.Identity(uri);
        }
        else
        {
            item.DisplayName = name.Text.Trim();
            if (item.ItemType == ParcelItemType.Note) item.NoteContent = (extra.Text ?? string.Empty).Trim(); else item.SecondaryDetail = (extra.Text ?? string.Empty).Trim();
        }
        try { await Store.UpdateItemAsync(_parcel, item); Build(); }
        catch (Exception exception) { backup.Restore(item); AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "ITEM WAS NOT SAVED", "The change could not be written. The previous item details were restored."); Build(); }
    }

    private async Task RemoveItemAsync(ParcelItem item)
    {
        var parcel = _parcel;
        if (parcel is null || _removalInProgress || _busy) return;
        _removalInProgress = true;
        try
        {
            ConfirmedOperationResult result;
            try { result = await Dialogs.ConfirmAndRunAsync(this, "REMOVE ITEM FROM PARCEL?", "Only the WorkParcel record will be removed. The original file, folder, application or link will not be deleted.", "REMOVE RECORD", () => Store.RemoveItemAsync(parcel, item)); }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "REMOVAL FAILED", "The database could not remove this record. It is still in this parcel, and the original resource was not changed. Try again."); return; }
            if (!result.Confirmed) return;
            if (!result.Succeeded) { await Dialogs.ShowMessage(this, "ITEM WAS NOT REMOVED", "The saved record is still in this parcel. Try again."); return; }
            _selectedItemIds.Remove(item.Id);
            Build();
            await Dialogs.ShowMessage(this, "ITEM REMOVED", "The WorkParcel record was removed immediately. The original resource was not changed.");
        }
        finally { _removalInProgress = false; }
    }

    private async Task RemoveSelectedAsync()
    {
        var parcel = _parcel;
        if (parcel is null || _removalInProgress || _busy) return;
        var selected = parcel.Items.Where(item => _selectedItemIds.Contains(item.Id)).ToList();
        if (selected.Count == 0) { await Dialogs.ShowMessage(this, "NO ITEMS SELECTED", "Select one or more saved items first."); return; }
        _removalInProgress = true;
        try
        {
            ConfirmedOperationResult result;
            try { result = await Dialogs.ConfirmAndRunAsync(this, "REMOVE SELECTED ITEMS?", $"Remove {selected.Count} saved item{(selected.Count == 1 ? string.Empty : "s")} from WorkParcel? Original files, folders, applications and links stay untouched.", "REMOVE RECORDS", async () => await Store.RemoveItemsAsync(parcel, selected) == selected.Count); }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "REMOVAL FAILED", "The database could not remove the selected records. They are still in this parcel, and the original resources were not changed. Try again."); return; }
            if (!result.Confirmed) return;
            if (!result.Succeeded) { await Dialogs.ShowMessage(this, "ITEMS WERE NOT REMOVED", "The selected records are still in this parcel. Try again."); return; }
            _selectedItemIds.Clear();
            Build();
            await Dialogs.ShowMessage(this, "ITEMS REMOVED", $"{selected.Count} saved record{(selected.Count == 1 ? string.Empty : "s")} removed. Original resources were not changed.");
        }
        finally { _removalInProgress = false; }
    }

    private async Task RelinkAsync(ParcelItem item)
    {
        if (_parcel is null) return;
        var backup = ItemBackup.Take(item);
        try
        {
            ParcelItem replacement;
            if (item.ItemType == ParcelItemType.File)
            {
                var paths = await _pickers.PickFilesAsync(); if (paths.Count != 1) return; replacement = await _factory.FileAsync(_parcel.Id, paths[0], item.SortOrder);
            }
            else if (item.ItemType == ParcelItemType.Folder)
            {
                var path = await _pickers.PickFolderAsync(); if (path is null) return; replacement = _factory.Folder(_parcel.Id, path, item.SortOrder);
            }
            else
            {
                var path = await _pickers.PickApplicationAsync(); if (path is null) return; replacement = _factory.Application(_parcel.Id, path, item.SortOrder);
            }
            ParcelItemRelinker.Apply(item, replacement); await Store.UpdateItemAsync(_parcel, item, ParcelHistoryEventType.ItemRelinked, $"{item.TypeLabel} re-linked"); Build();
        }
        catch (Exception exception) { backup.Restore(item); AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "RE-LINK FAILED", "The replacement could not be saved. The original item record was restored."); Build(); }
    }

    private async Task AcceptChangedFileAsync(ParcelItem item)
    {
        if (_parcel is null || item.ItemType != ParcelItemType.File || !await Dialogs.Confirm(this, "ACCEPT CHANGED FILE?", "WorkParcel will update the saved size, modified time and fingerprint. It does not judge whether the file is safe.", "ACCEPT VERSION")) return;
        var backup = ItemBackup.Take(item);
        try
        {
            var updated = await _factory.FileAsync(_parcel.Id, item.Value, item.SortOrder); item.FileSize = updated.FileSize; item.FileModifiedAt = updated.FileModifiedAt; item.Fingerprint = updated.Fingerprint; item.HasChanged = false; item.LastVerifiedAt = DateTime.Now; await Store.UpdateItemAsync(_parcel, item, ParcelHistoryEventType.ChangedFileAccepted, "Changed file version accepted"); Build();
        }
        catch (Exception exception) { backup.Restore(item); AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "VERSION WAS NOT ACCEPTED", "WorkParcel kept the previous file metadata. Check that the file is still accessible and try again."); Build(); }
    }

    private static Border HistoryRow(ParcelHistoryEntry entry) => new() { Child = Ui.Row(Ui.Mono(entry.Timestamp.ToString("HH:mm"), 10, "#9BE28F", true), Ui.Tag(entry.EventType.ToString().ToUpperInvariant(), "#8D9CA2"), Ui.Text(entry.Summary, 12)), Padding = new Thickness(8, 6, 8, 6), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };

    private sealed record ItemBackup(string Name, string Value, string? Identity, string? Detail, string? Note, string? Executable, string? Working, long? Size, DateTime? Modified, string? Fingerprint, bool Missing, bool Inaccessible, bool Changed, DateTime Updated)
    {
        public static ItemBackup Take(ParcelItem item) => new(item.DisplayName, item.Value, item.NormalizedIdentity, item.SecondaryDetail, item.NoteContent, item.ExecutablePath, item.WorkingDirectory, item.FileSize, item.FileModifiedAt, item.Fingerprint, item.IsMissing, item.IsInaccessible, item.HasChanged, item.UpdatedAt);
        public void Restore(ParcelItem item) { item.DisplayName = Name; item.Value = Value; item.NormalizedIdentity = Identity; item.SecondaryDetail = Detail; item.NoteContent = Note; item.ExecutablePath = Executable; item.WorkingDirectory = Working; item.FileSize = Size; item.FileModifiedAt = Modified; item.Fingerprint = Fingerprint; item.IsMissing = Missing; item.IsInaccessible = Inaccessible; item.HasChanged = Changed; item.UpdatedAt = Updated; }
    }
}
