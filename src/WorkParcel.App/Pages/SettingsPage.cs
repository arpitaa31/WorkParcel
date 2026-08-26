using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel.Core.Browser;
using WorkParcel_App.Models;
using WorkParcel_App.Services;

namespace WorkParcel_App.Pages;

public sealed partial class SettingsPage : PageBase
{
    private readonly StackPanel _data = Ui.Stack(8);
    private readonly StackPanel _browser = Ui.Stack(8);

    public SettingsPage()
    {
        BrowserIntegrationService.Current.StateChanged += BrowserIntegration_StateChanged;
        var body = Body(15);
        body.Children.Add(Header("SETTINGS", "Local data, browser connection and privacy boundaries."));
        body.Children.Add(Section("APPEARANCE", Appearance()));
        body.Children.Add(Section("LOCAL DATA", _data));
        body.Children.Add(Section("BROWSER INTEGRATION", _browser));
        body.Children.Add(Section("PRIVACY", Privacy()));
        body.Children.Add(Section("ABOUT", About()));
        SetContent(body); LoadBrowser(); _ = LoadDataAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        BrowserIntegrationService.Current.StateChanged -= BrowserIntegration_StateChanged;
        base.OnNavigatedFrom(e);
    }

    private void BrowserIntegration_StateChanged(object? sender, EventArgs e) => DispatcherQueue?.TryEnqueue(LoadBrowser);

    private Border Section(string title, UIElement content) { var stack = Ui.Stack(10); stack.Children.Add(Ui.Mono(title, 11, "#9BE28F", true)); stack.Children.Add(content); return Ui.Card(stack, 15); }
    private UIElement Appearance()
    {
        var stack = Ui.Stack(7); stack.Children.Add(Ui.Text("Theme", 13, true)); var row = Ui.Row();
        foreach (var theme in new[] { "System", "Light", "Dark" }) { var button = Ui.Button(theme.ToUpperInvariant()); button.Click += async (_, _) => { await Store.SetThemeAsync(theme); RequestedTheme = theme == "Dark" ? ElementTheme.Dark : theme == "Light" ? ElementTheme.Light : ElementTheme.Default; }; row.Children.Add(button); }
        stack.Children.Add(row); return stack;
    }

    private void LoadBrowser()
    {
        _browser.Children.Clear();
        var chrome = BrowserIntegrationService.Current.GetStatus("chrome"); var edge = BrowserIntegrationService.Current.GetStatus("edge");
        if (chrome.Status == BrowserConnectionStatus.Connected && edge.Status == BrowserConnectionStatus.Connected) _browser.Children.Add(Ui.Text("CONNECTED - CHROME + EDGE", 12, true, "#9BE28F"));
        AddBrowserStatus("CHROME", "chrome", chrome);
        AddBrowserStatus("EDGE", "edge", edge);
        _browser.Children.Add(Ui.Mono("Setup is user-driven: load browser-extension as unpacked, copy each exact browser ID, then run tools\\Setup-BrowserHost.ps1. WorkParcel never installs extensions or bypasses prompts.", 10, "#8D9CA2"));
    }

    private void AddBrowserStatus(string label, string browser, BrowserConnectionInfo info)
    {
        var card = Ui.Stack(6);
        var stateColor = info.Status == BrowserConnectionStatus.Connected ? "#9BE28F" : "#F0B45B";
        card.Children.Add(Ui.Text($"{label}   {BrowserStatusText(info)}", 12, true, stateColor));
        card.Children.Add(Ui.Mono($"INSTALLED {(info.BrowserInstalled ? "YES" : "NO")}   ·   HOST {(info.HostInstalled ? "YES" : "NO")}   ·   EXTENSION {info.ExtensionVersion ?? "NOT DETECTED"}", 10));
        var protocol = info.ProtocolVersion == 0 ? "NOT DETECTED" : $"v{info.ProtocolVersion} {(info.ProtocolCompatible ? "COMPATIBLE" : "INCOMPATIBLE")}";
        card.Children.Add(Ui.Mono($"PROTOCOL {protocol}   ·   {info.WindowCount} WINDOWS   ·   {info.TabCount} TABS   ·   LAST {info.LastConnectedUtc?.ToLocalTime().ToString("g") ?? "NEVER"}", 10));
        var row = Ui.Row();
        AddAction(row, "SET UP " + label, () => ShowSetupHelp(browser));
        AddAction(row, "TEST CONNECTION", async () => await TestConnection(browser));
        AddAction(row, "REPAIR CONNECTION", () => ShowSetupHelp(browser));
        AddAction(row, "DISCONNECT", () => { BrowserIntegrationService.Current.Disconnect(browser); LoadBrowser(); });
        AddAction(row, "REMOVE CONNECTION", async () => { if (await Dialogs.Confirm(this, $"REMOVE {label} CONNECTION?", "This removes only WorkParcel's current-user native-host registration for this browser. The extension itself is not removed.", "REMOVE REGISTRATION")) { BrowserIntegrationService.Current.RemoveRegistration(browser); LoadBrowser(); } });
        card.Children.Add(row);
        var links = Ui.Row(); AddAction(links, "OPEN EXTENSION FOLDER", OpenExtensionFolder); AddAction(links, "VIEW SETUP HELP", () => ShowSetupHelp(browser)); card.Children.Add(links);
        _browser.Children.Add(Ui.Card(card, 10));
    }

    private void AddAction(StackPanel row, string label, Action action) { var button = Ui.Button(label); button.Click += (_, _) => action(); row.Children.Add(button); }
    private void AddAction(StackPanel row, string label, Func<Task> action) { var button = Ui.Button(label); button.Click += async (_, _) => await action(); row.Children.Add(button); }
    private static string BrowserStatusText(BrowserConnectionInfo info) => info.Status switch
    {
        BrowserConnectionStatus.Connected => $"CONNECTED — {info.Browser.ToUpperInvariant()}" + (string.IsNullOrWhiteSpace(info.ExtensionVersion) ? string.Empty : $" v{info.ExtensionVersion}"),
        BrowserConnectionStatus.NativeHostNotInstalled => "NATIVE HOST NOT INSTALLED",
        BrowserConnectionStatus.ExtensionNotDetected => "EXTENSION NOT DETECTED",
        BrowserConnectionStatus.ConnectionLost => "APP CONNECTION LOST",
        BrowserConnectionStatus.ConnectionError => "CONNECTION ERROR",
        BrowserConnectionStatus.VersionMismatch => "VERSION MISMATCH",
        BrowserConnectionStatus.BrowserNotFound => "BROWSER NOT FOUND",
        BrowserConnectionStatus.BrowserNotRunning => "BROWSER NOT RUNNING",
        BrowserConnectionStatus.Disabled => "DISABLED IN PRIVACY SETTINGS",
        _ => "CONNECTING"
    };

    private async Task TestConnection(string browser)
    {
        try { var tabs = await BrowserIntegrationService.Current.ListTabsAsync(browser); var supported = tabs.Count(tab => BrowserTabRules.IsAllowedForCapture(tab)); await Dialogs.ShowMessage(this, "BROWSER CONNECTION", $"{browser.ToUpperInvariant()} returned {supported} supported HTTP/HTTPS tab{(supported == 1 ? string.Empty : "s")}. Private and browser-internal pages are excluded from capture."); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "CONNECTION ERROR", "The extension or native host did not respond. Check the exact extension ID and setup script."); }
        LoadBrowser();
    }

    private void ShowSetupHelp(string browser) => _ = Dialogs.ShowMessage(this, $"SET UP {browser.ToUpperInvariant()}", $"1. Load browser-extension as unpacked in {browser} extensions.\n2. Copy the exact 32-character extension ID.\n3. Run tools\\Setup-BrowserHost.ps1 -Browser {browser.ToUpperInvariant()} -HostExecutablePath <host.exe> -{(browser == "chrome" ? "Chrome" : "Edge")}ExtensionId <id>.\n4. Restart the browser if it cached the old host manifest.");
    private static void OpenExtensionFolder()
    {
        try
        {
            var candidates = new List<string>();
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                candidates.Add(Path.Combine(directory.FullName, "browser-extension"));
            var path = candidates.FirstOrDefault(Directory.Exists);
            if (path is null) throw new DirectoryNotFoundException("The browser-extension folder was not found beside the app or in its source tree.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
    }

    private UIElement Privacy()
    {
        var stack = Ui.Stack(6);
        stack.Children.Add(Ui.Text("Browser capture stores metadata only: URL, title, domain, browser family, window/group position, pinned/active state and a browser-provided favicon reference. It does not read page contents, history, cookies, passwords, downloads, forms or keystrokes.", 12, false, "#8D9CA2"));
        stack.Children.Add(Ui.Text("Private/incognito tabs and browser-internal pages are excluded by default. Closing tabs is disabled unless you explicitly choose SAVE AND CLOSE in Pack Away; WorkParcel validates browser, connection, window and URL identity and never closes a browser window or process.", 12, false, "#8D9CA2"));
        stack.Children.Add(Ui.Text("The local SQLite database stays in the WorkParcel data folder. Disconnect Chrome or Edge above to stop its bridge; REMOVE CONNECTION removes only that browser's current-user native-host registration.", 12, false, "#8D9CA2"));
        stack.Children.Add(new CheckBox { Content = "EXCLUDE PRIVATE BROWSING (ALWAYS ON)", IsChecked = true, IsEnabled = false, IsThreeState = false });
        var controls = Ui.Row();
        var bridge = Ui.Button(BrowserIntegrationService.Current.IsEnabled ? "DISABLE BROWSER CONNECTION" : "ENABLE BROWSER CONNECTION"); bridge.Click += (_, _) => { BrowserIntegrationService.Current.SetEnabled(!BrowserIntegrationService.Current.IsEnabled); BuildPrivacy(); }; controls.Children.Add(bridge);
        var closing = Ui.Button(BrowserIntegrationService.Current.TabClosingEnabled ? "DISABLE TAB CLOSING" : "ENABLE TAB CLOSING"); closing.Click += (_, _) => { BrowserIntegrationService.Current.SetTabClosingEnabled(!BrowserIntegrationService.Current.TabClosingEnabled); BuildPrivacy(); }; controls.Children.Add(closing);
        var clear = Ui.Button("CLEAR ICON CACHE"); clear.Click += async (_, _) => await ClearIconCacheAsync(); controls.Children.Add(clear); stack.Children.Add(controls);
        var saved = Store.Parcels.Concat(Store.Archived).SelectMany(parcel => parcel.Items).Count(item => item.ItemType == ParcelItemType.BrowserTab); stack.Children.Add(Ui.Mono($"SAVED BROWSER TABS   {saved}   |   PRIVATE TABS EXCLUDED BY DEFAULT", 10, "#9BE28F", true));
        return stack;
    }

    private void BuildPrivacy()
    {
        // The settings page is rebuilt so the toggles reflect the effective policy immediately.
        var body = Body(15); body.Children.Add(Header("SETTINGS", "Local data, browser connection and privacy boundaries.")); body.Children.Add(Section("APPEARANCE", Appearance())); body.Children.Add(Section("LOCAL DATA", _data)); body.Children.Add(Section("BROWSER INTEGRATION", _browser)); body.Children.Add(Section("PRIVACY", Privacy())); body.Children.Add(Section("ABOUT", About())); SetContent(body); LoadBrowser();
    }

    private async Task ClearIconCacheAsync()
    {
        try
        {
            var path = Store.Paths.IconCacheDirectory;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            Store.Paths.EnsureDirectories();
            await Dialogs.ShowMessage(this, "ICON CACHE CLEARED", "Local file and application icons were removed. Browser tabs continue to use a safe fallback badge; no page content was downloaded.");
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "CACHE NOT CLEARED", "The favicon cache could not be removed. Your saved tabs were not changed."); }
    }

    private UIElement About() { var stack = Ui.Stack(4); stack.Children.Add(Ui.Text("WorkParcel", 14, true)); stack.Children.Add(Ui.Mono("LOCAL PARCEL UTILITY", 10)); stack.Children.Add(Ui.Text("Save a setup. Pack it away. Open it when you return.", 12, false, "#8D9CA2")); return stack; }

    private async Task LoadDataAsync()
    {
        try
        {
            var counts = await Store.GetCountsAsync(); _data.Children.Clear(); _data.Children.Add(Ui.Text($"DATABASE STATUS   {Store.DatabaseStatus}", 12, true)); _data.Children.Add(Ui.Text($"DATABASE SIZE   {FormatSize(Store.DatabaseSize)}", 12)); _data.Children.Add(Ui.Text($"PARCELS   {counts.Parcels}", 12)); _data.Children.Add(Ui.Text($"TODAY ITEMS   {counts.TodayItems}", 12)); _data.Children.Add(Ui.Mono($"DATA FOLDER   {Store.Paths.DataDirectory}", 10)); _data.Children.Add(Ui.Mono($"LAST INITIALIZED   {Store.LastSuccessfulInitializationUtc?.ToLocalTime():g}", 10));
            var row = Ui.Row(); var open = Ui.Button("OPEN DATA FOLDER"); open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(Store.Paths.DataDirectory) { UseShellExecute = true }); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); } }; var backup = Ui.Button("BACK UP DATA"); backup.Click += async (_, _) => { try { await Store.BackupAsync(); await Dialogs.ShowMessage(this, "BACKUP CREATED", "A new SQLite backup was saved without overwriting an earlier backup."); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "BACKUP FAILED", "The local database could not be backed up."); } }; row.Children.Add(open); row.Children.Add(backup); _data.Children.Add(row);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); _data.Children.Clear(); _data.Children.Add(Ui.Text("Local data information is unavailable.", 12, false, "#F0B45B")); }
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";
}
