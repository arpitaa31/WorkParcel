using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using WorkParcel.Core.Browser;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WorkParcel_App.Pages;

public sealed record CaptureSeed(string Name, string Description, Guid? ExistingParcelId = null);

public sealed partial class CapturePage : PageBase
{
    private readonly OpenWindowService _windows = new();
    private readonly WorkspacePickerService _pickers = new();
    private readonly ParcelItemFactory _factory = new();
    private readonly List<ParcelItem> _draft = new();
    private readonly HashSet<Guid> _selected = new();
    private readonly HashSet<Guid> _original = new();
    private readonly HashSet<Guid> _detected = new();
    private readonly HashSet<Guid> _detectedBrowser = new();
    private readonly TextBox _name = new() { Header = "PARCEL NAME", PlaceholderText = "What should this setup be called?", MaxLength = 80 };
    private readonly TextBox _description = new() { Header = "DESCRIPTION - OPTIONAL", PlaceholderText = "What is this setup for?", MaxLength = 240, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
    private readonly TextBox _search = new() { PlaceholderText = "Search selected and detected items" };
    private readonly TextBlock _selectionStatus = Ui.Mono("0 SELECTED", 10, "#9BE28F", true);
    private readonly TextBlock _workStatus = Ui.Mono("READY", 10);
    private readonly StackPanel _itemHost = Ui.Stack(9);
    private CaptureSeed? _seed;
    private Parcel? _editing;
    private CancellationTokenSource? _loadCancellation;
    private int _step = 1;
    private bool _loadedItems;
    private bool _busy;
    private int _searchVersion;

    public CapturePage()
    {
        BrowserIntegrationService.Current.StateChanged += BrowserIntegration_StateChanged;
        _search.TextChanged += Search_TextChanged;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => QueueSearchRefresh();

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        BrowserIntegrationService.Current.StateChanged -= BrowserIntegration_StateChanged;
        BrowserIntegrationService.Current.StateChanged += BrowserIntegration_StateChanged;
        _draft.Clear(); _selected.Clear(); _original.Clear(); _detected.Clear(); _detectedBrowser.Clear(); _loadedItems = false; _step = 1; _busy = false;
        _seed = e.Parameter as CaptureSeed;
        _editing = _seed?.ExistingParcelId is Guid id ? Store.Parcels.Concat(Store.Archived).FirstOrDefault(parcel => parcel.Id == id) : null;
        _name.Text = _editing?.Name ?? _seed?.Name ?? string.Empty; _description.Text = _editing?.Description ?? _seed?.Description ?? string.Empty;
        if (_editing is not null) foreach (var item in _editing.Items) { _draft.Add(item); _selected.Add(item.Id); _original.Add(item.Id); }
        Build();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = null;
        BrowserIntegrationService.Current.StateChanged -= BrowserIntegration_StateChanged;
        base.OnNavigatedFrom(e);
    }

    private void BrowserIntegration_StateChanged(object? sender, EventArgs e)
    {
        if (_step != 2 || _busy) return;
        DispatcherQueue?.TryEnqueue(RebuildItemSections);
    }

    private void Build()
    {
        Ui.Interactive(_name, 1.004f); Ui.Interactive(_description, 1.004f); Ui.Interactive(_search, 1.004f);
        var body = Body(16); body.Children.Add(TopBar()); body.Children.Add(StepRail()); body.Children.Add(Ui.Rule());
        if (_step == 1) BuildName(body); else if (_step == 2) BuildSelection(body); else BuildReview(body);
        SetContent(body);
    }

    private Grid TopBar()
    {
        var grid = new Grid { ColumnSpacing = 16 }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = Ui.Button("<- BACK"); back.Click += (_, _) => { if (_step > 1) { _step--; Build(); } else if (Frame?.CanGoBack == true) Frame.GoBack(); };
        grid.Children.Add(back); var copy = Ui.Stack(2); copy.Children.Add(Ui.Mono(_editing is null ? "CAPTURE CURRENT SETUP" : "UPDATE EXISTING PARCEL", 11, "#9BE28F", true)); copy.Children.Add(Ui.Text(_editing is null ? "Choose exactly what belongs in this parcel." : $"Updating {_editing.Name}", 12, false, "#8D9CA2")); Grid.SetColumn(copy, 1); grid.Children.Add(copy); return grid;
    }

    private StackPanel StepRail()
    {
        var row = Ui.Row(); row.Children.Add(Ui.Tag("1  NAME", _step == 1 ? "#9BE28F" : "#8D9CA2")); row.Children.Add(Ui.Mono("--", 10));
        row.Children.Add(Ui.Tag("2  SELECT ITEMS", _step == 2 ? "#9BE28F" : "#8D9CA2")); row.Children.Add(Ui.Mono("--", 10)); row.Children.Add(Ui.Tag("3  REVIEW", _step == 3 ? "#9BE28F" : "#8D9CA2")); return row;
    }

    private void BuildName(StackPanel body)
    {
        body.Children.Add(Ui.Text(_editing is null ? "Name this parcel" : "Check the parcel details", 26, true)); body.Children.Add(Ui.Text("The name and description stay inside WorkParcel.", 13, false, "#8D9CA2"));
        var fields = Ui.Stack(12); fields.Children.Add(_name); fields.Children.Add(_description); body.Children.Add(Ui.Card(fields, 18));
        var next = Ui.Button("NEXT: SELECT ITEMS  ->", true); next.HorizontalAlignment = HorizontalAlignment.Right; next.Click += async (_, _) => await GoToSelectionAsync(); body.Children.Add(next);
    }

    private async Task GoToSelectionAsync()
    {
        _name.Description = string.IsNullOrWhiteSpace(_name.Text) ? "A parcel name is required." : null; if (!string.IsNullOrWhiteSpace(_name.Description?.ToString())) return;
        _step = 2; Build(); if (!_loadedItems) await RefreshWindowsAsync();
    }

    private void BuildSelection(StackPanel body)
    {
        body.Children.Add(Ui.Text("Select workspace items", 26, true));
        var tools = new Grid { ColumnSpacing = 9 }; tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(_search); var refresh = Ui.Button(">> REFRESH WINDOWS"); refresh.Click += async (_, _) => await RefreshWindowsAsync(); Grid.SetColumn(refresh, 1); tools.Children.Add(refresh); body.Children.Add(tools);
        var adds = new Grid { ColumnSpacing = 7, RowSpacing = 7 }; var addApp = Ui.Button("+ APPLICATION"); addApp.Click += async (_, _) => await AddApplicationAsync(); var addFiles = Ui.Button("+ FILES"); addFiles.Click += async (_, _) => await AddFilesAsync(); var addFolder = Ui.Button("+ FOLDER"); addFolder.Click += async (_, _) => await AddFolderAsync(); var addLink = Ui.Button("ADD LINK MANUALLY"); addLink.Click += async (_, _) => await AddLinkAsync(); var pasteLinks = Ui.Button("PASTE LINKS"); pasteLinks.Click += async (_, _) => await PasteLinksAsync(); var addNote = Ui.Button("+ NOTE"); addNote.Click += async (_, _) => await AddNoteAsync();
        var addButtons = new[] { addApp, addFiles, addFolder, addLink, pasteLinks, addNote }; foreach (var button in addButtons) { button.HorizontalAlignment = HorizontalAlignment.Stretch; adds.Children.Add(button); } ArrangeAddButtons(adds, addButtons, 900); adds.SizeChanged += (_, args) => ArrangeAddButtons(adds, addButtons, args.NewSize.Width); body.Children.Add(adds);
        var selectRow = Ui.Row(); var all = Ui.Button("SELECT ALL"); all.Click += (_, _) => { foreach (var item in FilteredDraft()) _selected.Add(item.Id); RebuildItemSections(); }; var none = Ui.Button("CLEAR ALL"); none.Click += (_, _) => { _selected.Clear(); RebuildItemSections(); }; selectRow.Children.Add(all); selectRow.Children.Add(none); selectRow.Children.Add(Ui.Spacer()); selectRow.Children.Add(_selectionStatus); selectRow.Children.Add(_workStatus); body.Children.Add(selectRow);
        body.Children.Add(DropTarget()); body.Children.Add(_itemHost);
        RebuildItemSections(); var next = Ui.Button("NEXT: REVIEW  ->", true); next.HorizontalAlignment = HorizontalAlignment.Right; next.Click += async (_, _) => await GoToReviewAsync(); body.Children.Add(next);
    }

    private async Task GoToReviewAsync()
    {
        _step = 3;
        Build();
    }

    private static void ArrangeAddButtons(Grid grid, IReadOnlyList<Button> buttons, double width)
    {
        var columns = width >= 780 ? 6 : width >= 500 ? 3 : 2; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Math.Ceiling(buttons.Count / (double)columns); i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < buttons.Count; i++) { Grid.SetRow(buttons[i], i / columns); Grid.SetColumn(buttons[i], i % columns); }
    }

    private void RebuildItemSections()
    {
        _itemHost.Children.Clear(); UpdateSelectedText(); AddCategory("APPLICATIONS & WINDOWS", _draft.Where(item => item.ItemType is ParcelItemType.ApplicationWindow or ParcelItemType.Application));
        var browserInfo = Ui.Stack(3); var chrome = BrowserIntegrationService.Current.GetConnectionState("chrome"); var edge = BrowserIntegrationService.Current.GetConnectionState("edge"); browserInfo.Children.Add(Ui.Mono($"CHROME   {BrowserStateText(chrome)}   |   EDGE   {BrowserStateText(edge)}", 10, chrome.State == BrowserConnectionState.Connected || edge.State == BrowserConnectionState.Connected ? "#9BE28F" : "#F0B45B", true)); browserInfo.Children.Add(Ui.Text("Only non-private HTTP/HTTPS tabs are offered. Browser-internal pages stay out of the parcel.", 11, false, "#8D9CA2")); _itemHost.Children.Add(Ui.Card(browserInfo, 12));
        var connectChrome = Ui.Button("CONNECT A BROWSER"); connectChrome.Click += (_, _) => Frame?.Navigate(typeof(SettingsPage)); browserInfo.Children.Add(connectChrome);
        AddBrowserTree(); AddCategory("LINKS", _draft.Where(item => item.ItemType == ParcelItemType.WebLink)); AddCategory("FILES & FOLDERS", _draft.Where(item => item.ItemType is ParcelItemType.File or ParcelItemType.Folder)); AddCategory("NOTES", _draft.Where(item => item.ItemType == ParcelItemType.Note));
    }

    private void AddBrowserTree()
    {
        var tabs = _draft.Where(item => item.ItemType == ParcelItemType.BrowserTab && MatchesSearch(item)).ToList();
        var root = Ui.Stack(4);
        foreach (var browser in tabs.GroupBy(item => item.BrowserFamily ?? "unknown", StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
        {
            var browserStack = Ui.Stack(3);
            var windowNumber = 0;
            foreach (var window in browser.GroupBy(item => item.BrowserWindowGroupId ?? "window-0", StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
            {
                var windowStack = Ui.Stack(2); var windowItems = window.ToList(); var windowLabel = $"{browser.Key.ToUpperInvariant()} WINDOW {++windowNumber}"; var controls = Ui.Row();
                var select = Ui.Button("SELECT WINDOW"); select.Click += (_, _) => { foreach (var item in windowItems) _selected.Add(item.Id); RebuildItemSections(); };
                var clear = Ui.Button("CLEAR WINDOW"); clear.Click += (_, _) => { foreach (var item in windowItems) _selected.Remove(item.Id); RebuildItemSections(); };
                controls.Children.Add(Ui.Mono($"{windowLabel}   {windowItems.Count} TAB{(windowItems.Count == 1 ? string.Empty : "S")}", 10, "#9BE28F", true)); controls.Children.Add(Ui.Spacer()); controls.Children.Add(select); controls.Children.Add(clear); windowStack.Children.Add(controls);
                foreach (var group in windowItems.GroupBy(item => item.BrowserTabGroupTitle ?? "UNGROUPED", StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key)) { var groupStack = Ui.Stack(1); groupStack.Children.Add(Ui.Mono(group.Key.ToUpperInvariant(), 9, "#8D9CA2", true)); foreach (var item in group.OrderBy(item => item.BrowserTabIndex ?? int.MaxValue)) groupStack.Children.Add(SelectionRow(item)); windowStack.Children.Add(groupStack); }
                browserStack.Children.Add(new Expander { Header = Ui.Mono(windowLabel, 10, "#C8D2D5", true), IsExpanded = true, Content = windowStack });
            }
            root.Children.Add(new Expander { Header = Ui.Mono($"{browser.Key.ToUpperInvariant()}   {browser.Count()} TABS", 11, "#9BE28F", true), IsExpanded = true, Content = browserStack });
        }
        if (tabs.Count == 0) root.Children.Add(Ui.Text("No supported browser tabs detected. Private and browser-internal pages are excluded.", 11, false, "#8D9CA2"));
        _itemHost.Children.Add(new Expander { Header = Ui.Mono($"BROWSER TAB TREE   {tabs.Count}", 11, null, true), IsExpanded = true, Content = root, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    private void AddCategory(string name, IEnumerable<ParcelItem> source)
    {
        var items = source.Where(MatchesSearch).ToList(); var stack = Ui.Stack(3); if (items.Count == 0) stack.Children.Add(Ui.Text("Nothing here yet.", 11, false, "#8D9CA2")); else foreach (var item in items) stack.Children.Add(SelectionRow(item));
        _itemHost.Children.Add(new Expander { Header = Ui.Mono($"{name}   {items.Count}", 11, null, true), IsExpanded = items.Count > 0, Content = stack, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    private Border SelectionRow(ParcelItem item)
    {
        var check = new CheckBox { IsChecked = _selected.Contains(item.Id), VerticalAlignment = VerticalAlignment.Center, UseSystemFocusVisuals = true }; check.Checked += (_, _) => { _selected.Add(item.Id); UpdateSelectedText(); }; check.Unchecked += (_, _) => { _selected.Remove(item.Id); UpdateSelectedText(); };
        var copy = Ui.Stack(2); copy.Children.Add(Ui.Text(item.DisplayName, 12, true)); copy.Children.Add(Ui.Mono($"{item.TypeLabel}   {SelectionDetail(item)}   {item.AvailabilityLabel}", 9, item.IsMissing ? "#F0B45B" : null));
        var row = new Grid { ColumnSpacing = 9 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.Children.Add(check); var icon = Ui.ItemIcon(item); Grid.SetColumn(icon, 1); row.Children.Add(icon); Grid.SetColumn(copy, 2); row.Children.Add(copy);
        if (!_original.Contains(item.Id) && !_detected.Contains(item.Id)) { var remove = Ui.Button("REMOVE"); remove.Click += (_, _) => { _draft.Remove(item); _selected.Remove(item.Id); RebuildItemSections(); }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(remove, 3); row.Children.Add(remove); }
        return Ui.Interactive(new Border { Child = row, Background = Ui.Resource("SurfaceBrush"), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(9, 8, 9, 8) });
    }

    private static string SelectionDetail(ParcelItem item) => item.ItemType == ParcelItemType.BrowserTab ? $"{item.BrowserFamily?.ToUpperInvariant() ?? "BROWSER"} | {item.BrowserDomain ?? item.SecondaryDetail ?? "unknown domain"} | {item.Value}{(item.BrowserPinned ? " | PINNED" : string.Empty)}" : item.SecondaryDetail ?? item.Value;

    private void BuildReview(StackPanel body)
    {
        var chosen = _draft.Where(item => _selected.Contains(item.Id)).ToList(); body.Children.Add(Ui.Text("Review the parcel", 26, true)); body.Children.Add(Ui.Text("Only these checked items will be saved.", 13, false, "#8D9CA2"));
        var summary = Ui.Stack(4); summary.Children.Add(Ui.Mono($"{chosen.Count} ITEM{(chosen.Count == 1 ? string.Empty : "S")} SELECTED", 12, "#9BE28F", true)); summary.Children.Add(Ui.Text(_name.Text.Trim(), 15, true)); if (!string.IsNullOrWhiteSpace(_description.Text)) summary.Children.Add(Ui.Text(_description.Text.Trim(), 12, false, "#8D9CA2")); body.Children.Add(Ui.Card(summary, 16));
        foreach (var group in chosen.GroupBy(item => item.ItemType)) { body.Children.Add(Ui.SectionHeader(group.Key.ToString().ToUpperInvariant())); foreach (var item in group) { var status = _original.Contains(item.Id) ? "ALREADY SAVED" : "NEW"; var copy = Ui.Stack(2); copy.Children.Add(Ui.Text(item.DisplayName, 12, true)); copy.Children.Add(Ui.Mono(ReviewDetail(item), 9)); var row = Ui.Row(Ui.Tag(status, status == "NEW" ? "#9BE28F" : "#8D9CA2"), copy, Ui.Spacer(), Ui.Mono(item.AvailabilityLabel, 9, item.IsMissing ? "#F0B45B" : null)); body.Children.Add(new Border { Child = row, Padding = new Thickness(8), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) }); } }
        if (chosen.Count == 0) body.Children.Add(Ui.Card(Ui.Text("This will create or update an empty parcel.", 12, false, "#8D9CA2"), 14));
        var actions = Ui.Row(); var back = Ui.Button("<- MODIFY SELECTION"); back.Click += (_, _) => { _step = 2; Build(); }; var save = Ui.Button(_editing is null ? "CREATE PARCEL" : "UPDATE PARCEL", true); save.Click += async (_, _) => await SaveAsync(save); actions.Children.Add(back); actions.Children.Add(Ui.Spacer()); actions.Children.Add(save); body.Children.Add(actions);
    }

    private async Task RefreshWindowsAsync()
    {
        if (_busy) return; _busy = true; _workStatus.Text = "DETECTING WINDOWS..."; _loadCancellation?.Cancel(); _loadCancellation = new CancellationTokenSource();
        try
        {
            var windowsTask = _windows.DetectAsync(_editing?.Id ?? Guid.Empty, _loadCancellation.Token);
            var browserTask = BrowserIntegrationService.Current.ListTabsAsync(cancellationToken: _loadCancellation.Token);
            await Task.WhenAll(windowsTask, browserTask); var windows = await windowsTask; var browserTabs = await browserTask;
            foreach (var old in _draft.Where(item => _detected.Contains(item.Id) || _detectedBrowser.Contains(item.Id)).ToList()) { _draft.Remove(old); _selected.Remove(old.Id); _detected.Remove(old.Id); _detectedBrowser.Remove(old.Id); }
            foreach (var savedItem in _draft.Where(item => item.ItemType == ParcelItemType.ApplicationWindow && !_detected.Contains(item.Id))) { savedItem.RuntimeWindowHandle = nint.Zero; savedItem.RuntimeProcessId = 0; }
            var matchedWindowHandles = new HashSet<nint>();
            foreach (var match in OpenWindowService.MatchSavedItemsByIdentity(_draft, windows).Where(match => match.Live is not null))
            {
                var live = windows.FirstOrDefault(item => item.RuntimeWindowHandle == match.Live!.Handle);
                if (live is null) continue;
                match.Item.RuntimeWindowHandle = live.RuntimeWindowHandle; match.Item.RuntimeProcessId = live.RuntimeProcessId; match.Item.CloseSupported = true; match.Item.WindowClassName = live.WindowClassName; matchedWindowHandles.Add(live.RuntimeWindowHandle);
            }
            foreach (var window in windows.Where(window => !matchedWindowHandles.Contains(window.RuntimeWindowHandle))) { _draft.Add(window); _selected.Add(window.Id); _detected.Add(window.Id); }
            var skippedTabs = 0; var matchedBrowserIds = new HashSet<Guid>(); var browserGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var savedItem in _draft.Where(item => item.ItemType == ParcelItemType.BrowserTab)) { savedItem.BrowserSessionTabId = null; savedItem.BrowserSessionWindowId = null; savedItem.BrowserConnectionId = null; savedItem.CloseSupported = false; } foreach (var tab in browserTabs) { if (!BrowserTabRules.IsAllowedForCapture(tab)) { skippedTabs++; continue; } var saved = BrowserIntegrationService.FindSavedTab(_draft, tab, matchedBrowserIds, browserGroups); if (saved is not null) { matchedBrowserIds.Add(saved.Id); BrowserIntegrationService.ApplyLiveTab(saved, tab); continue; } var candidate = BrowserIntegrationService.FromTab(_editing?.Id ?? Guid.Empty, tab, _draft.Count, null); _draft.Add(candidate); _selected.Add(candidate.Id); _detectedBrowser.Add(candidate.Id); matchedBrowserIds.Add(candidate.Id); }
            _loadedItems = true; _workStatus.Text = $"{windows.Count} WINDOWS | {browserTabs.Count - skippedTabs} TABS"; RebuildItemSections();
        }
        catch (OperationCanceledException) { _workStatus.Text = "REFRESH CANCELED"; }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); _workStatus.Text = "WINDOW DETECTION FAILED - RETRY"; }
        finally { _busy = false; }
    }

    private static string BrowserStateText(BrowserConnectionStateInfo info) => info.State switch { BrowserConnectionState.Connected => $"CONNECTED | {info.Details.WindowCount} WINDOW{(info.Details.WindowCount == 1 ? string.Empty : "S")} / {info.Details.TabCount} TAB{(info.Details.TabCount == 1 ? string.Empty : "S")}", BrowserConnectionState.ConnectionFailed => "CONNECTION NEEDS ATTENTION", BrowserConnectionState.BrowserMissing => "BROWSER NOT FOUND", BrowserConnectionState.Disabled => "TAB CAPTURE OFF", BrowserConnectionState.ExtensionRequired => "SETUP IN PROGRESS", BrowserConnectionState.DesktopConnectionRequired => "SETUP IN PROGRESS", BrowserConnectionState.ReadyToTest => "READY TO TEST", _ => "NOT CONNECTED" };

    private async Task AddApplicationAsync() { var path = await _pickers.PickApplicationAsync(); if (path is null) return; try { AddDraft(_factory.Application(_editing?.Id ?? Guid.Empty, path, _draft.Count)); } catch (Exception e) { await ShowItemError(e); } }
    private async Task AddFolderAsync() { var path = await _pickers.PickFolderAsync(); if (path is null) return; try { AddDraft(_factory.Folder(_editing?.Id ?? Guid.Empty, path, _draft.Count)); } catch (Exception e) { await ShowItemError(e); } }
    private async Task AddFilesAsync() { var paths = await _pickers.PickFilesAsync(); if (paths.Count == 0) return; _workStatus.Text = "READING FILE METADATA..."; var failed = 0; var duplicates = 0; foreach (var path in paths) { try { AddDraft(await _factory.FileAsync(_editing?.Id ?? Guid.Empty, path, _draft.Count)); } catch (DuplicateParcelItemException) { duplicates++; } catch (Exception exception) { AppLogger.LogTechnicalError(exception); failed++; } } _workStatus.Text = $"{paths.Count - failed - duplicates} READY | {duplicates} DUPLICATE | {failed} UNAVAILABLE"; }

    private Border DropTarget()
    {
        var target = Ui.Card(Ui.Mono("DROP FILES OR FOLDERS HERE", 10, "#8D9CA2", true), 11); target.AllowDrop = true;
        target.DragOver += (_, args) => { if (args.DataView.Contains(StandardDataFormats.StorageItems)) { args.AcceptedOperation = DataPackageOperation.Copy; args.DragUIOverride.Caption = "Attach to this parcel"; args.DragUIOverride.IsCaptionVisible = true; } };
        target.DragEnter += (_, _) => target.BorderBrush = Ui.Resource("AccentBrush"); target.DragLeave += (_, _) => target.BorderBrush = Ui.Resource("BorderBrush");
        target.Drop += async (_, args) => { target.BorderBrush = Ui.Resource("BorderBrush"); await AddDroppedAsync(args.DataView); }; return target;
    }

    private async Task AddDroppedAsync(DataPackageView data)
    {
        if (!data.Contains(StandardDataFormats.StorageItems)) return; var failed = 0; var added = 0; _workStatus.Text = "PROCESSING DROP...";
        try
        {
            foreach (var entry in await data.GetStorageItemsAsync())
            {
                try { if (entry is StorageFile file) AddDraft(await _factory.FileAsync(_editing?.Id ?? Guid.Empty, file.Path, _draft.Count), false); else if (entry is StorageFolder folder) AddDraft(_factory.Folder(_editing?.Id ?? Guid.Empty, folder.Path, _draft.Count), false); else { failed++; continue; } added++; }
                catch (Exception exception) { AppLogger.LogTechnicalError(exception); failed++; }
            }
            _workStatus.Text = $"{added} ATTACHED | {failed} SKIPPED"; RebuildItemSections();
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); _workStatus.Text = "DROP COULD NOT BE READ"; }
    }

    private async Task AddLinkAsync()
    {
        var title = new TextBox { Header = "DISPLAY NAME - OPTIONAL" }; var url = new TextBox { Header = "HTTP/HTTPS URL", PlaceholderText = "https://" }; var note = new TextBox { Header = "SHORT NOTE - OPTIONAL" }; var stack = Ui.Stack(9); stack.Children.Add(title); stack.Children.Add(url); stack.Children.Add(note);
        var dialog = new ContentDialog { Title = "ADD WEB LINK", Content = stack, PrimaryButtonText = "ADD LINK", CloseButtonText = "CANCEL", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Primary }; dialog.PrimaryButtonClick += (sender, args) => { if (!WebLinkRules.TryNormalize(url.Text, out _)) { args.Cancel = true; url.Description = "Enter a valid HTTP or HTTPS link."; } };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return; try { AddDraft(_factory.WebLink(_editing?.Id ?? Guid.Empty, url.Text, title.Text, note.Text, _draft.Count)); } catch (Exception e) { await ShowItemError(e); }
    }

    private async Task PasteLinksAsync()
    {
        var input = new TextBox { Header = "ONE HTTP/HTTPS LINK PER LINE", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170 }; var dialog = new ContentDialog { Title = "PASTE LINKS", Content = input, PrimaryButtonText = "CHECK LINKS", CloseButtonText = "CANCEL", XamlRoot = XamlRoot };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return; var parsed = WebLinkRules.ParseMany(input.Text);
        if (parsed.Valid.Count == 0) { await Dialogs.ShowMessage(this, "NO VALID LINKS", "Enter at least one HTTP or HTTPS link. Nothing was added."); return; }
        if (parsed.Invalid.Count > 0 && !await Dialogs.Confirm(this, "INVALID LINKS FOUND", $"These lines are not HTTP/HTTPS links and will not be saved:\n\n{string.Join("\n", parsed.Invalid.Take(8))}{(parsed.Invalid.Count > 8 ? "\n..." : string.Empty)}\n\nAdd the {parsed.Valid.Count} valid link{(parsed.Valid.Count == 1 ? string.Empty : "s")} only?", "ADD VALID LINKS")) return;
        var duplicates = 0; foreach (var link in parsed.Valid) { try { AddDraft(_factory.WebLink(_editing?.Id ?? Guid.Empty, link.AbsoluteUri, null, null, _draft.Count), false); } catch (DuplicateParcelItemException) { duplicates++; } }
        if (duplicates > 0) await Dialogs.ShowMessage(this, "DUPLICATE LINKS SKIPPED", $"{duplicates} link{(duplicates == 1 ? string.Empty : "s")} already existed in this parcel."); RebuildItemSections();
    }

    private async Task AddNoteAsync()
    {
        var title = new TextBox { Header = "SHORT TITLE - OPTIONAL" }; var content = new TextBox { Header = "NOTE", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140 }; var stack = Ui.Stack(9); stack.Children.Add(title); stack.Children.Add(content);
        var dialog = new ContentDialog { Title = "ADD NOTE", Content = stack, PrimaryButtonText = "ADD NOTE", CloseButtonText = "CANCEL", XamlRoot = XamlRoot }; dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(content.Text)) { args.Cancel = true; content.Description = "Write something before adding the note."; } };
        if (await Ui.ShowDialog(dialog) == ContentDialogResult.Primary) try { AddDraft(_factory.Note(_editing?.Id ?? Guid.Empty, title.Text, content.Text, _draft.Count)); } catch (Exception e) { await ShowItemError(e); }
    }

    private void AddDraft(ParcelItem item, bool rebuild = true) { if (ParcelItemIdentity.IsDuplicate(_draft, item)) throw new DuplicateParcelItemException($"{item.DisplayName} is already in this parcel."); _draft.Add(item); _selected.Add(item.Id); if (rebuild) RebuildItemSections(); }

    private async Task SaveAsync(Button button)
    {
        if (_busy) return;
        var chosen = _draft.Where(item => _selected.Contains(item.Id)).ToList();
        if (_editing is not null)
        {
            var removals = _original.Count(id => !_selected.Contains(id));
            if (removals > 0 && !await Dialogs.Confirm(this, "REMOVE SAVED ITEM RECORDS?", $"{removals} item record{(removals == 1 ? string.Empty : "s")} will be removed from WorkParcel. Original files, folders and applications stay untouched.", "REMOVE AND UPDATE")) return;
        }

        _busy = true;
        button.IsEnabled = false;
        button.Content = Ui.Mono("SAVING...", 11, "#0B0E10", true);
        try
        {
            Parcel parcel;
            if (_editing is null)
            {
                parcel = await Store.CreateWithItemsAsync(_name.Text, _description.Text, chosen);
            }
            else
            {
                parcel = _editing;
                var added = chosen.Count(item => !_original.Contains(item.Id));
                var removed = _original.Count(id => !_selected.Contains(id));
                await Store.ReplaceItemsAsync(parcel, chosen, $"Parcel updated - {added} added | {removed} removed", ParcelHistoryEventType.Updated);
            }

            var changed = _editing is null ? chosen.Count : chosen.Count(item => !_original.Contains(item.Id));
            await Dialogs.ShowMessage(this, _editing is null ? "PARCEL CREATED" : "PARCEL UPDATED",
                _editing is null ? $"{changed} ITEM{(changed == 1 ? string.Empty : "S")} SAVED" : $"{changed} ITEM{(changed == 1 ? string.Empty : "S")} ADDED");
            Frame?.Navigate(typeof(ParcelDetailsPage), parcel.Id);
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            await Dialogs.ShowMessage(this, "PARCEL WAS NOT SAVED", "Your selection is still here. Check the item details and try again.");
            button.IsEnabled = true;
        }
        finally { _busy = false; }
    }

    private IEnumerable<ParcelItem> FilteredDraft() => _draft.Where(MatchesSearch);
    private static string ReviewDetail(ParcelItem item) => item.ItemType == ParcelItemType.ApplicationWindow ? $"{item.SecondaryDetail ?? item.Value} | {(item.LaunchEnabled ? "REOPENS APP" : "AUTOMATIC REOPEN UNSUPPORTED")}" : item.SecondaryDetail ?? item.Value;
    private async void QueueSearchRefresh() { var version = ++_searchVersion; await Task.Delay(180); if (version == _searchVersion && _step == 2) RebuildItemSections(); }
    private bool MatchesSearch(ParcelItem item) => string.IsNullOrWhiteSpace(_search.Text) || item.DisplayName.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || item.Value.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || (item.SecondaryDetail?.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false) || (item.BrowserFamily?.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false) || (item.BrowserWindowGroupId?.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false) || (item.BrowserTabGroupTitle?.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
    private void UpdateSelectedText() => _selectionStatus.Text = $"{_selected.Count} SELECTED | {_draft.Count} TOTAL";
    private async Task ShowItemError(Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, exception is DuplicateParcelItemException ? "ITEM ALREADY ADDED" : "ITEM COULD NOT BE ADDED", exception is DuplicateParcelItemException ? "That item is already in this parcel." : "WorkParcel could not read that item. It may be missing, inaccessible or unsupported."); }
}
