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
    private readonly StackPanel _browser = Ui.Stack(10);
    private int _browserRefreshVersion;
    private bool _stateSubscribed;
    private bool _windowActivationSubscribed;
    private bool _browserActionInProgress;
    private bool _updatingPrivacy;
    private CheckBox? _captureToggle;
    private CheckBox? _tabClosingToggle;

    public SettingsPage()
    {
        SubscribeToBrowserState();
        BuildPage();
        GotFocus += (_, _) => _ = RefreshBrowserAsync();
        _ = RefreshBrowserAsync();
        _ = LoadDataAsync();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SubscribeToBrowserState();
        SubscribeToWindowActivation();
        _ = RefreshBrowserAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (_stateSubscribed)
        {
            BrowserIntegrationService.Current.StateChanged -= BrowserIntegration_StateChanged;
            _stateSubscribed = false;
        }
        if (_windowActivationSubscribed && (Application.Current as App)?.MainWindow is { } window)
        {
            window.Activated -= MainWindow_Activated;
            _windowActivationSubscribed = false;
        }
        base.OnNavigatedFrom(e);
    }

    private void SubscribeToBrowserState()
    {
        if (_stateSubscribed) return;
        BrowserIntegrationService.Current.StateChanged += BrowserIntegration_StateChanged;
        _stateSubscribed = true;
    }

    private void SubscribeToWindowActivation()
    {
        if (_windowActivationSubscribed || (Application.Current as App)?.MainWindow is not { } window) return;
        window.Activated += MainWindow_Activated;
        _windowActivationSubscribed = true;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) _ = RefreshBrowserAsync();
    }

    private void BrowserIntegration_StateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _updatingPrivacy = true;
            if (_captureToggle is not null) _captureToggle.IsChecked = BrowserIntegrationService.Current.IsEnabled;
            if (_tabClosingToggle is not null) _tabClosingToggle.IsChecked = BrowserIntegrationService.Current.TabClosingEnabled;
            _updatingPrivacy = false;
            _ = RefreshBrowserAsync();
        });
    }

    private void BuildPage()
    {
        var body = Body(15);
        body.Children.Add(Header("SETTINGS", "Make WorkParcel fit the way you work."));
        body.Children.Add(Section("APPEARANCE", Appearance()));
        body.Children.Add(Section("LOCAL DATA", _data));
        body.Children.Add(Section("BROWSER TABS", BrowserTabs()));
        body.Children.Add(Section("DESK MEMORY", DeskMemory()));
        body.Children.Add(Section("TAB PRIVACY", TabPrivacy()));
        body.Children.Add(Section("ABOUT", About()));
        SetContent(body);
    }

    private Border Section(string title, UIElement content)
    {
        var stack = Ui.Stack(10);
        stack.Children.Add(Ui.Mono(title, 11, "#9BE28F", true));
        stack.Children.Add(content);
        return Ui.Card(stack, 15);
    }

    private UIElement Appearance()
    {
        var stack = Ui.Stack(7);
        stack.Children.Add(Ui.Text("Choose the look that is most comfortable for you.", 13));
        var row = Ui.Row();
        foreach (var theme in new[] { "System", "Light", "Dark" })
        {
            var button = Ui.Button(theme.ToUpperInvariant());
            button.Click += async (_, _) =>
            {
                await Store.SetThemeAsync(theme);
                RequestedTheme = theme == "Dark" ? ElementTheme.Dark : theme == "Light" ? ElementTheme.Light : ElementTheme.Default;
            };
            row.Children.Add(button);
        }
        stack.Children.Add(row);
        return stack;
    }

    private UIElement BrowserTabs()
    {
        var stack = Ui.Stack(10);
        stack.Children.Add(Ui.Text("Connect a browser so WorkParcel can show your open tabs when you capture a setup.", 12, false, "#8D9CA2"));
        _browser.Children.Clear();
        _browser.Children.Add(Ui.Text("Checking browser connection…", 12, false, "#8D9CA2"));
        stack.Children.Add(_browser);
        return stack;
    }

    private async Task RefreshBrowserAsync()
    {
        var version = Interlocked.Increment(ref _browserRefreshVersion);
        BrowserConnectionStateInfo[] states;
        try
        {
            // Browser installation and process checks can touch the registry and
            // enumerate processes. Keep those checks away from the UI thread.
            states = await Task.Run(() => new[]
            {
                BrowserIntegrationService.Current.GetConnectionState("chrome"),
                BrowserIntegrationService.Current.GetConnectionState("edge")
            });
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            return;
        }

        if (version != Volatile.Read(ref _browserRefreshVersion)) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (version == Volatile.Read(ref _browserRefreshVersion)) RenderBrowser(states);
        });
    }

    private void RenderBrowser(IEnumerable<BrowserConnectionStateInfo> states)
    {
        _browser.Children.Clear();
        foreach (var state in states) _browser.Children.Add(BrowserCard(state));
    }

    private UIElement BrowserCard(BrowserConnectionStateInfo state)
    {
        var details = state.Details;
        var browser = details.Browser.ToLowerInvariant();
        var name = BrowserName(browser);
        var card = Ui.Stack(8);
        var heading = Ui.Row();
        heading.Children.Add(BrowserMark(browser));
        var title = Ui.Stack(3);
        title.Children.Add(Ui.Text(name, 16, true));
        title.Children.Add(Ui.Tag(StateTitle(state.State, name), StateColor(state.State)));
        heading.Children.Add(title);
        card.Children.Add(heading);
        card.Children.Add(Ui.Text(StateDescription(state.State, details, name), 12, false, "#8D9CA2"));

        if (state.State == BrowserConnectionState.Connected)
        {
            var checkedAt = details.LastConnectedUtc?.ToLocalTime().LocalDateTime;
            card.Children.Add(Ui.Mono($"{details.TabCount} tabs available   ·   {details.WindowCount} browser windows found   ·   Last checked {Ui.Relative(checkedAt)}", 10, "#9BE28F"));
        }

        var actions = Ui.Row();
        var primaryText = PrimaryAction(state.State, name);
        if (primaryText is not null)
        {
            var primary = Ui.Button(primaryText, true);
            primary.Click += async (_, _) =>
            {
                if (_browserActionInProgress) return;
                _browserActionInProgress = true;
                primary.IsEnabled = false;
                try { await RunPrimaryActionAsync(browser, state.State); }
                finally { _browserActionInProgress = false; primary.IsEnabled = true; }
            };
            actions.Children.Add(primary);
        }

        if (state.State == BrowserConnectionState.Connected)
        {
            var disconnect = Ui.LinkButton("Disconnect");
            disconnect.Click += (_, _) =>
            {
                BrowserIntegrationService.Current.Disconnect(browser);
                _ = RefreshBrowserAsync();
            };
            actions.Children.Add(disconnect);
        }
        else if (state.State == BrowserConnectionState.ConnectionFailed)
        {
            var detailsLink = Ui.LinkButton("View details");
            detailsLink.Click += async (_, _) => await ShowDiagnosticsAsync(browser, state);
            actions.Children.Add(detailsLink);
        }
        else if (state.State != BrowserConnectionState.BrowserMissing)
        {
            var how = Ui.LinkButton("How does this work?");
            how.Click += async (_, _) => await ShowBrowserHowItWorksAsync(browser);
            actions.Children.Add(how);
        }
        card.Children.Add(actions);

        var advanced = new Expander { Header = Ui.Text("ADVANCED DETAILS", 11, true, "#8D9CA2"), Content = AdvancedDetails(browser, state), IsExpanded = false };
        card.Children.Add(advanced);
        return Ui.Card(card, 12, 3);
    }

    private static Border BrowserMark(string browser)
    {
        var color = browser == "edge" ? "#67C7FF" : "#9BE28F";
        var label = browser == "edge" ? "EDG" : "CHR";
        return new Border { Width = 42, Height = 42, Background = Ui.Resource("SubtleSurfaceBrush"), BorderBrush = Ui.Brush(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = Ui.Mono(label, 11, color, true), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }

    private UIElement AdvancedDetails(string browser, BrowserConnectionStateInfo state)
    {
        var info = state.Details;
        var stack = Ui.Stack(6);
        AddDetail(stack, "Browser installed", info.BrowserInstalled ? "Yes" : "No");
        AddDetail(stack, "Extension detected", info.ExtensionDetected ? "Yes" : "Not detected");
        AddDetail(stack, "Desktop connection", info.HostInstalled ? "Registered" : "Not registered");
        AddDetail(stack, "Protocol status", info.ProtocolVersion == 0 ? "Not detected" : $"v{info.ProtocolVersion} — {(info.ProtocolCompatible ? "compatible" : "incompatible")}");
        AddDetail(stack, "Extension version", info.ExtensionVersion ?? "Not detected");
        AddDetail(stack, "Registered extension IDs", HostRegistrationService.GetRegisteredExtensionIds(browser) ?? "Not available");
        AddDetail(stack, "Connection ID", info.ConnectionId ?? "Not connected");
        AddDetail(stack, "Last successful connection", info.LastConnectedUtc?.ToLocalTime().ToString("g") ?? "Never");
        AddDetail(stack, "Detected windows and tabs", $"{info.WindowCount} windows / {info.TabCount} tabs");
        if (!string.IsNullOrWhiteSpace(info.LastError)) AddDetail(stack, "Last diagnostic", info.LastError!);

        if (state.State != BrowserConnectionState.BrowserMissing)
        {
            var actions = Ui.Stack(4);
            AddLinkAction(actions, "Refresh status", () => RefreshBrowserAsync());
            AddLinkAction(actions, "Test connection", () => TestBrowserConnectionAsync(browser, true));
            AddLinkAction(actions, "Repair connection", () => ShowGuidedSetupAsync(browser));
            AddLinkAction(actions, "View setup help", () => ShowSetupHelpAsync(browser));
            AddLinkAction(actions, "Open extension folder", () => { OpenExtensionFolder(); return Task.CompletedTask; });
            AddLinkAction(actions, "Open diagnostics", () => ShowDiagnosticsAsync(browser, state));
            if (info.HostInstalled) AddLinkAction(actions, "Remove connection", () => RemoveRegistrationAsync(browser));
            stack.Children.Add(actions);
            stack.Children.Add(Ui.Text("Disconnect temporarily stops browser communication. Remove connection deletes only WorkParcel's desktop registration. Neither action deletes the browser, extension, tabs or parcels.", 11, false, "#8D9CA2"));
        }
        return stack;
    }

    private static void AddDetail(StackPanel stack, string label, string value)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Ui.Text(label, 11, true, "#8D9CA2"));
        var valueText = Ui.Mono(value, 11);
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        stack.Children.Add(row);
    }

    private static void AddLinkAction(StackPanel stack, string label, Func<Task> action)
    {
        var button = Ui.LinkButton(label);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Click += async (_, _) => await action();
        stack.Children.Add(button);
    }

    private async Task RunPrimaryActionAsync(string browser, BrowserConnectionState state)
    {
        switch (state)
        {
            case BrowserConnectionState.Connected:
                await TestBrowserConnectionAsync(browser, true);
                break;
            case BrowserConnectionState.Disabled:
                BrowserIntegrationService.Current.SetEnabled(true);
                await RefreshBrowserAsync();
                break;
            default:
                await ShowGuidedSetupAsync(browser);
                break;
        }
    }

    private async Task ShowGuidedSetupAsync(string browser)
    {
        var current = await DetectBrowserAsync(browser);
        var guidedStep = BrowserSetupStepLogic.For(current);
        var name = BrowserName(browser);
        var content = Ui.Stack(10);
        var dialog = new ContentDialog
        {
            Title = $"CONNECT {name.ToUpperInvariant()}",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
            PrimaryButtonText = GuidedAction(guidedStep, current.State, name, repairRequested: false),
            CloseButtonText = "CANCEL",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var busy = false;
        var repairRequested = false;

        void Render()
        {
            content.Children.Clear();
            content.Children.Add(Ui.Text("This lets WorkParcel find the tabs you choose to save. It does not read page contents, passwords or browsing history.", 12, false, "#8D9CA2"));
            content.Children.Add(GuidedProgress(guidedStep));
            AddGuidedExplanation(content, browser, current, guidedStep);
            dialog.PrimaryButtonText = GuidedAction(guidedStep, current.State, name, repairRequested);
            dialog.IsPrimaryButtonEnabled = current.State is not BrowserConnectionState.BrowserMissing;
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy) return;
            if (current.State is BrowserConnectionState.Connected or BrowserConnectionState.BrowserMissing)
            {
                dialog.Hide();
                return;
            }

            busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                await RunGuidedActionAsync(browser, current, guidedStep, repairRequested);
                current = await DetectBrowserAsync(browser);
                if (guidedStep == BrowserSetupStep.TestConnection && current.State == BrowserConnectionState.ConnectionFailed) repairRequested = true;
                guidedStep = BrowserSetupStepLogic.For(current);
                Render();
                if (current.State == BrowserConnectionState.Connected) await RefreshBrowserAsync();
            }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                content.Children.Add(Ui.Text("WorkParcel could not complete that step. The connection was not assumed to be ready; try the step again.", 12, false, "#EF7777"));
            }
            finally { busy = false; if (current.State is not BrowserConnectionState.BrowserMissing) dialog.IsPrimaryButtonEnabled = true; }
        };

        Render();
        await Ui.ShowDialog(dialog);
        await RefreshBrowserAsync();
    }

    private static StackPanel GuidedProgress(BrowserSetupStep current)
    {
        var stack = Ui.Stack(4);
        stack.Children.Add(Ui.Mono(current == BrowserSetupStep.Complete ? "SETUP COMPLETE" : $"STEP {(int)current} OF 3", 10, current == BrowserSetupStep.Complete ? "#9BE28F" : "#F0B45B", true));
        stack.Children.Add(Ui.Text(StepText(1), 12, current == BrowserSetupStep.InstallExtension, current == BrowserSetupStep.InstallExtension ? "#F0B45B" : "#8D9CA2"));
        stack.Children.Add(Ui.Text(StepText(2), 12, current == BrowserSetupStep.ConnectDesktop, current == BrowserSetupStep.ConnectDesktop ? "#F0B45B" : "#8D9CA2"));
        stack.Children.Add(Ui.Text(StepText(3), 12, current == BrowserSetupStep.TestConnection, current == BrowserSetupStep.TestConnection ? "#F0B45B" : "#8D9CA2"));
        return stack;
    }

    private static string StepText(int step) => step switch
    {
        1 => "1. Install the WorkParcel browser extension",
        2 => "2. Connect it to the desktop app",
        _ => "3. Test the connection"
    };

    private void AddGuidedExplanation(StackPanel content, string browser, BrowserConnectionStateInfo state, BrowserSetupStep guidedStep)
    {
        var name = BrowserName(browser);
        if (guidedStep == BrowserSetupStep.Complete)
        {
            content.Children.Add(Ui.Text($"{name.ToUpperInvariant()} CONNECTED", 16, true, "#9BE28F"));
            content.Children.Add(Ui.Text($"Your open {name} windows and tabs can now appear when you capture a setup.", 12));
            return;
        }

        if (guidedStep == BrowserSetupStep.ConnectDesktop && state.State is BrowserConnectionState.NotConfigured or BrowserConnectionState.ExtensionRequired)
        {
            content.Children.Add(Ui.Text("The extension was not claimed as installed. If you loaded it, continue by registering the desktop connection.", 12));
            content.Children.Add(Ui.Text("1. Copy the exact extension ID shown on the browser extensions page.\n2. Run the current-user setup command below.\n3. Return here and continue to the connection check.", 12));
            content.Children.Add(Ui.Mono(SetupCommand(browser), 10));
            AddLinkAction(content, "OPEN SETUP FOLDER", () => { OpenSetupFolder(); return Task.CompletedTask; });
            return;
        }

        if (guidedStep == BrowserSetupStep.TestConnection && state.State is not BrowserConnectionState.Connected)
        {
            content.Children.Add(Ui.Text(state.State == BrowserConnectionState.ConnectionFailed
                ? "The connection needs attention. Check the browser extension and desktop registration, then try the check again."
                : "After completing the browser and desktop steps, return here and check the connection.", 12, false, state.State == BrowserConnectionState.ConnectionFailed ? "#EF7777" : "#8D9CA2"));
            if (state.State == BrowserConnectionState.ConnectionFailed && !string.IsNullOrWhiteSpace(state.Details.LastError)) content.Children.Add(Ui.Mono($"Last diagnostic: {state.Details.LastError}", 10, "#EF7777"));
            return;
        }

        switch (state.State)
        {
            case BrowserConnectionState.BrowserMissing:
                content.Children.Add(Ui.Text($"{name} was not found on this computer.", 13, true, "#F0B45B"));
                break;
            case BrowserConnectionState.ExtensionRequired:
            case BrowserConnectionState.NotConfigured:
                content.Children.Add(Ui.Text($"Install the extension in {name}, then return here. Browser security requires this manual confirmation; WorkParcel does not pretend the extension was installed for you.", 12));
                content.Children.Add(Ui.Text("1. Open the browser extensions page and enable Developer mode.\n2. Choose Load unpacked and select the WorkParcel browser-extension folder.\n3. Leave this window open and continue when the extension is ready.", 12));
                AddLinkAction(content, "OPEN EXTENSION FOLDER", () => { OpenExtensionFolder(); return Task.CompletedTask; });
                break;
            case BrowserConnectionState.DesktopConnectionRequired:
                content.Children.Add(Ui.Text("The extension is present, but it is not registered with the WorkParcel desktop connection yet.", 12));
                content.Children.Add(Ui.Text("The browser assigns the exact extension ID. WorkParcel must use that exact ID when registering the native connection, so this step cannot be safely automated.", 12));
                content.Children.Add(Ui.Mono(SetupCommand(browser), 10));
                AddLinkAction(content, "OPEN SETUP FOLDER", () => { OpenSetupFolder(); return Task.CompletedTask; });
                break;
            case BrowserConnectionState.ReadyToTest:
                content.Children.Add(Ui.Text("The browser connection is ready. Test it to confirm that WorkParcel can see eligible tabs.", 12));
                break;
            case BrowserConnectionState.ConnectionFailed:
                content.Children.Add(Ui.Text("The browser extension or desktop connection did not respond. Check the extension page and registration, then test again.", 12, false, "#EF7777"));
                if (!string.IsNullOrWhiteSpace(state.Details.LastError)) content.Children.Add(Ui.Mono($"Last diagnostic: {state.Details.LastError}", 10, "#EF7777"));
                break;
            case BrowserConnectionState.Disabled:
                content.Children.Add(Ui.Text("Browser tab capture is turned off in Tab Privacy. Enable it to continue.", 12));
                break;
        }
    }

    private async Task RunGuidedActionAsync(string browser, BrowserConnectionStateInfo current, BrowserSetupStep guidedStep, bool repairRequested)
    {
        switch (guidedStep)
        {
            case BrowserSetupStep.InstallExtension when current.State == BrowserConnectionState.Disabled:
                BrowserIntegrationService.Current.SetEnabled(true);
                break;
            case BrowserSetupStep.InstallExtension:
                OpenBrowserExtensionsPage(browser);
                OpenExtensionFolder();
                break;
            case BrowserSetupStep.ConnectDesktop:
                OpenSetupFolder();
                break;
            case BrowserSetupStep.TestConnection when current.State == BrowserConnectionState.ConnectionFailed && !repairRequested:
                OpenBrowserExtensionsPage(browser);
                OpenExtensionFolder();
                if (!current.Details.HostInstalled) OpenSetupFolder();
                break;
            case BrowserSetupStep.TestConnection:
                await TestBrowserConnectionAsync(browser, false);
                break;
        }
    }

    private async Task TestBrowserConnectionAsync(string browser, bool showMessage)
    {
        try
        {
            var before = await DetectBrowserAsync(browser);
            if (before.State != BrowserConnectionState.Connected)
            {
                if (showMessage) await Dialogs.ShowMessage(this, "CONNECTION NEEDS ATTENTION", StateDescription(before.State, before.Details, BrowserName(browser)));
                return;
            }

            var tabs = await BrowserIntegrationService.Current.ListTabsAsync(browser);
            var after = await DetectBrowserAsync(browser);
            if (after.State != BrowserConnectionState.Connected)
            {
                if (showMessage) await Dialogs.ShowMessage(this, "CONNECTION NEEDS ATTENTION", StateDescription(after.State, after.Details, BrowserName(browser)));
                return;
            }
            var supported = tabs.Count(tab => BrowserTabRules.IsAllowedForCapture(tab));
            if (showMessage) await Dialogs.ShowMessage(this, $"{BrowserName(browser).ToUpperInvariant()} CONNECTED", $"{supported} eligible HTTP/HTTPS tab{(supported == 1 ? string.Empty : "s")} available. Private and browser-internal pages are excluded.");
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            if (showMessage) await Dialogs.ShowMessage(this, "CONNECTION NEEDS ATTENTION", "The browser extension or desktop connection did not respond. Reconnect and try again.");
        }
        finally { await RefreshBrowserAsync(); }
    }

    private async Task<BrowserConnectionStateInfo> DetectBrowserAsync(string browser) => await Task.Run(() => BrowserIntegrationService.Current.GetConnectionState(browser));

    private async Task RemoveRegistrationAsync(string browser)
    {
        var name = BrowserName(browser);
        if (!await Dialogs.Confirm(this, $"REMOVE {name.ToUpperInvariant()} CONNECTION?", "This removes only WorkParcel's current-user desktop registration. It does not remove the browser, extension, tabs or parcels.", "REMOVE REGISTRATION")) return;
        var removed = BrowserIntegrationService.Current.RemoveRegistration(browser);
        await RefreshBrowserAsync();
        if (!removed) await Dialogs.ShowMessage(this, "CONNECTION NOT REMOVED", "WorkParcel could not remove the browser's desktop registration. Nothing else was changed. Try again or open setup help for the manual repair steps.");
    }

    private async Task ShowBrowserHowItWorksAsync(string browser)
    {
        await Dialogs.ShowMessage(this, $"HOW {BrowserName(browser).ToUpperInvariant()} CONNECTION WORKS", $"WorkParcel uses an optional browser extension to show eligible open tabs when you capture a setup. You choose which tabs to save. The extension sends tab metadata to WorkParcel over the local desktop connection; page contents, passwords, cookies and full browsing history are not read. You can disconnect {BrowserName(browser)} at any time without deleting saved parcels or browser tabs.");
    }

    private async Task ShowDiagnosticsAsync(string browser, BrowserConnectionStateInfo state)
    {
        var info = state.Details;
        var message = $"State: {state.State}\nBrowser installed: {(info.BrowserInstalled ? "yes" : "no")}\nExtension detected: {(info.ExtensionDetected ? "yes" : "no")}\nDesktop connection: {(info.HostInstalled ? "registered" : "not registered")}\nProtocol: {info.ProtocolVersion} {(info.ProtocolCompatible ? "compatible" : "not confirmed")}\nRegistered extension IDs: {HostRegistrationService.GetRegisteredExtensionIds(browser) ?? "not available"}\nLast error: {info.LastError ?? "none"}";
        await Dialogs.ShowMessage(this, $"{BrowserName(browser).ToUpperInvariant()} DIAGNOSTICS", message);
    }

    private async Task ShowSetupHelpAsync(string browser)
    {
        var name = BrowserName(browser).ToUpperInvariant();
        var extensionFolder = FindPath("browser-extension");
        var hostExecutable = FindPath(Path.Combine("BrowserHost", "WorkParcel.BrowserHost.exe"));
        var setupScript = FindPath(Path.Combine("BrowserHost", "Setup-BrowserHost.ps1")) ?? FindPath(Path.Combine("tools", "Setup-BrowserHost.ps1"));
        var folderLine = extensionFolder is null ? "Select the browser-extension folder shipped with this build." : extensionFolder;
        var command = SetupCommand(browser, setupScript, hostExecutable, name);
        await Dialogs.ShowMessage(this, $"CONNECT {name}", $"1. Load this folder as an unpacked extension:\n{folderLine}\n\n2. Copy the exact extension ID shown by {name}.\n\n3. Run this current-user setup command:\n{command}\n\n4. Return to WorkParcel and choose CHECK CONNECTION. The exact ID is required by browser security; WorkParcel will never register a wildcard origin.");
    }

    private static string SetupCommand(string browser)
    {
        var name = BrowserName(browser).ToUpperInvariant();
        var hostExecutable = FindPath(Path.Combine("BrowserHost", "WorkParcel.BrowserHost.exe"));
        var setupScript = FindPath(Path.Combine("BrowserHost", "Setup-BrowserHost.ps1")) ?? FindPath(Path.Combine("tools", "Setup-BrowserHost.ps1"));
        return SetupCommand(browser, setupScript, hostExecutable, name);
    }

    private static string SetupCommand(string browser, string? setupScript, string? hostExecutable, string name)
    {
        return setupScript is not null && hostExecutable is not null
            ? $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{setupScript}\" -Browser {name} -HostExecutablePath \"{hostExecutable}\" -{(browser == "chrome" ? "Chrome" : "Edge")}ExtensionId <exact-id>"
            : "Run the Setup-BrowserHost.ps1 file shipped with this build using the WorkParcel.BrowserHost.exe beside it.";
    }

    private static void OpenSetupFolder()
    {
        try
        {
            var path = FindPath("BrowserHost") ?? FindPath("tools");
            if (path is null) throw new DirectoryNotFoundException("The browser setup files are not included with this build.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
    }

    private static void OpenBrowserExtensionsPage(string browser)
    {
        try
        {
            var executable = BrowserInstallationService.FindExecutable(browser);
            if (executable is null) return;
            Process.Start(new ProcessStartInfo(executable) { Arguments = "--new-tab " + (browser == "edge" ? "edge://extensions/" : "chrome://extensions/"), UseShellExecute = true });
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
    }

    private static void OpenExtensionFolder()
    {
        try
        {
            var path = FindPath("browser-extension");
            if (path is null) throw new DirectoryNotFoundException("The browser-extension folder is not included with this build.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
    }

    private static string? FindPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private UIElement DeskMemory()
    {
        var current = Store.DeskMemorySettings;
        var restore = new CheckBox { Content = "RESTORE DESK MEMORY BY DEFAULT WHEN OPENING", IsChecked = current.RestoreByDefault };
        var review = new CheckBox { Content = "REVIEW MATCHES BEFORE APPLYING A LAYOUT", IsChecked = current.ReviewBeforeApplying };
        var reuse = new CheckBox { Content = "REUSE STRONGLY MATCHED WINDOWS THAT ARE ALREADY OPEN", IsChecked = current.ReuseMatchingOpenWindows };
        var maximized = new CheckBox { Content = "RESTORE MAXIMIZED WINDOWS", IsChecked = current.RestoreMaximized };
        var minimized = new CheckBox { Content = "RESTORE MINIMIZED WINDOWS", IsChecked = current.RestoreMinimized };
        var preview = new CheckBox { Content = "SHOW A LAYOUT PREVIEW DURING OPEN", IsChecked = current.ShowPreviewDuringOpen };
        var undo = new CheckBox { Content = "KEEP IN-MEMORY UNDO FOR WINDOW MOVES", IsChecked = current.EnableUndo };
        var timeout = new NumberBox { Header = "RESTORE TIMEOUT (SECONDS)", Value = current.RestorationTimeoutSeconds, Minimum = 2, Maximum = 120, SmallChange = 1, LargeChange = 5, Width = 230 };
        var save = Ui.Button("SAVE DESK MEMORY SETTINGS", true);
        save.Click += async (_, _) =>
        {
            try
            {
                await Store.SetDeskMemorySettingsAsync(new DeskMemorySettings(restore.IsChecked == true, review.IsChecked == true, reuse.IsChecked == true, maximized.IsChecked == true, minimized.IsChecked == true, preview.IsChecked == true, undo.IsChecked == true, (int)Math.Round(timeout.Value)));
                save.Content = Ui.Mono("SAVED", 11, "#0B0E10", true);
            }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "SETTINGS NOT SAVED", "The previous Desk Memory settings remain active."); }
        };
        var stack = Ui.Stack(6);
        stack.Children.Add(Ui.Text("Desk Memory stores monitor and window-placement metadata locally. It never stores HWNDs, process IDs, screenshots or application contents.", 12, false, "#8D9CA2"));
        stack.Children.Add(restore); stack.Children.Add(review); stack.Children.Add(reuse); stack.Children.Add(maximized); stack.Children.Add(minimized); stack.Children.Add(preview); stack.Children.Add(undo); stack.Children.Add(timeout); stack.Children.Add(save);
        return stack;
    }

    private UIElement TabPrivacy()
    {
        var service = BrowserIntegrationService.Current;
        var stack = Ui.Stack(7);
        stack.Children.Add(Ui.Text("You choose which tabs are saved. WorkParcel stores only the details needed to reopen them.", 12, false, "#8D9CA2"));

        var capture = new CheckBox { Content = "Allow browser tab capture\nShow eligible browser tabs when capturing a setup.", IsChecked = service.IsEnabled, IsThreeState = false };
        _captureToggle = capture;
        capture.Checked += (_, _) => { if (!_updatingPrivacy) service.SetEnabled(true); };
        capture.Unchecked += (_, _) => { if (!_updatingPrivacy) service.SetEnabled(false); };
        stack.Children.Add(capture);

        var closing = new CheckBox { Content = "Close selected tabs when packing away\nOnly tabs saved in that parcel may be closed.", IsChecked = service.TabClosingEnabled, IsThreeState = false };
        _tabClosingToggle = closing;
        closing.Checked += (_, _) => { if (!_updatingPrivacy) service.SetTabClosingEnabled(true); };
        closing.Unchecked += (_, _) => { if (!_updatingPrivacy) service.SetTabClosingEnabled(false); };
        stack.Children.Add(closing);

        stack.Children.Add(new CheckBox { Content = "Exclude private browsing\nIncognito and InPrivate tabs are never captured.", IsChecked = true, IsEnabled = false, IsThreeState = false });
        var stores = Ui.LinkButton("WHAT WORKPARCEL STORES");
        stores.HorizontalAlignment = HorizontalAlignment.Left;
        stores.Click += async (_, _) => await ShowWhatStoresAsync();
        stack.Children.Add(stores);
        return stack;
    }

    private async Task ShowWhatStoresAsync()
    {
        await Dialogs.ShowMessage(this, "WHAT WORKPARCEL STORES", "WorkParcel may store:\n• Tab title\n• URL\n• Browser type\n• Tab order\n• Pinned state\n• Browser-window grouping\n• Window position needed for restoration\n\nWorkParcel does not intentionally store:\n• Passwords\n• Cookies\n• Form contents\n• Page contents\n• Keystrokes\n• Full browser history");
    }

    private async Task ClearIconCacheAsync()
    {
        try
        {
            var path = Store.Paths.IconCacheDirectory;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            Store.Paths.EnsureDirectories();
            await Dialogs.ShowMessage(this, "ICON CACHE CLEARED", "Local file and application icons were removed. Saved browser records were not changed.");
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "CACHE NOT CLEARED", "The favicon cache could not be removed. Your saved tabs were not changed."); }
    }

    private UIElement About()
    {
        var stack = Ui.Stack(4);
        stack.Children.Add(Ui.Text("WorkParcel", 14, true));
        stack.Children.Add(Ui.Mono($"VERSION {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}", 10));
        stack.Children.Add(Ui.Mono("LOCAL PARCEL UTILITY", 10));
        stack.Children.Add(Ui.Text("Save a setup. Open it when you return.", 12, false, "#8D9CA2"));
        return stack;
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var counts = await Store.GetCountsAsync();
            _data.Children.Clear();
            _data.Children.Add(Ui.Text($"DATABASE STATUS   {Store.DatabaseStatus}", 12, true));
            _data.Children.Add(Ui.Text($"DATABASE SIZE   {FormatSize(Store.DatabaseSize)}", 12));
            _data.Children.Add(Ui.Text($"PARCELS   {counts.Parcels}", 12));
            _data.Children.Add(Ui.Text($"TODAY ITEMS   {counts.TodayItems}", 12));
            _data.Children.Add(Ui.Mono($"DATA FOLDER   {Store.Paths.DataDirectory}", 10));
            _data.Children.Add(Ui.Mono($"LAST INITIALIZED   {Store.LastSuccessfulInitializationUtc?.ToLocalTime():g}", 10));
            var row = Ui.Row();
            var open = Ui.Button("OPEN DATA FOLDER");
            open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(Store.Paths.DataDirectory) { UseShellExecute = true }); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); } };
            var backup = Ui.Button("BACK UP DATA");
            backup.Click += async (_, _) => { try { await Store.BackupAsync(); await Dialogs.ShowMessage(this, "BACKUP CREATED", "A new SQLite backup was saved without overwriting an earlier backup."); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "BACKUP FAILED", "The local database could not be backed up."); } };
            var clear = Ui.Button("CLEAR ICON CACHE");
            clear.Click += async (_, _) => await ClearIconCacheAsync();
            row.Children.Add(open); row.Children.Add(backup); row.Children.Add(clear); _data.Children.Add(row);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); _data.Children.Clear(); _data.Children.Add(Ui.Text("Local data information is unavailable.", 12, false, "#F0B45B")); }
    }

    private static string BrowserName(string browser) => browser.Equals("edge", StringComparison.OrdinalIgnoreCase) ? "Edge" : "Chrome";

    private static string StateTitle(BrowserConnectionState state, string browser) => state switch
    {
        BrowserConnectionState.Connected => "Connected",
        BrowserConnectionState.ConnectionFailed => "Connection needs attention",
        BrowserConnectionState.BrowserMissing => $"{browser} was not found",
        BrowserConnectionState.ExtensionRequired or BrowserConnectionState.DesktopConnectionRequired or BrowserConnectionState.ReadyToTest => "Setup in progress",
        BrowserConnectionState.Disabled => "Not connected",
        _ => "Not connected"
    };

    private static string StateDescription(BrowserConnectionState state, BrowserConnectionInfo? info, string browser) => state switch
    {
        BrowserConnectionState.Connected => $"Your open {browser} windows and tabs are ready to capture.",
        BrowserConnectionState.BrowserMissing => $"{browser} was not found on this computer.",
        BrowserConnectionState.ExtensionRequired => $"Install the WorkParcel extension in {browser}, then return here to continue.",
        BrowserConnectionState.DesktopConnectionRequired => "The browser extension is installed, but it cannot reach the WorkParcel desktop app yet.",
        BrowserConnectionState.ReadyToTest => "The browser connection is ready. Test it before capturing tabs.",
        BrowserConnectionState.ConnectionFailed when info?.Status == BrowserConnectionStatus.BrowserNotRunning => $"Open {browser}, then try the connection again.",
        BrowserConnectionState.ConnectionFailed when info?.Status == BrowserConnectionStatus.VersionMismatch => "The WorkParcel browser extension needs an update before it can connect.",
        BrowserConnectionState.ConnectionFailed => "The browser extension is installed, but it cannot reach WorkParcel.",
        BrowserConnectionState.Disabled => "Browser tab capture is turned off in Tab Privacy.",
        _ => $"Connect {browser} to select and restore tabs in your parcels."
    };

    private static string? PrimaryAction(BrowserConnectionState state, string browser) => BrowserConnectionStateLogic.PrimaryAction(state, browser);

    private static string GuidedAction(BrowserSetupStep step, BrowserConnectionState state, string browser, bool repairRequested) => step switch
    {
        BrowserSetupStep.Complete => "DONE",
        BrowserSetupStep.InstallExtension when state == BrowserConnectionState.Disabled => "ENABLE BROWSER TABS",
        BrowserSetupStep.InstallExtension => "INSTALL EXTENSION",
        BrowserSetupStep.ConnectDesktop => "CONNECT TO WORKPARCEL",
        BrowserSetupStep.TestConnection when state == BrowserConnectionState.ConnectionFailed && !repairRequested => "FIX CONNECTION",
        BrowserSetupStep.TestConnection => "CHECK CONNECTION",
        _ => $"CONNECT {browser.ToUpperInvariant()}"
    };

    private static string StateColor(BrowserConnectionState state) => state switch
    {
        BrowserConnectionState.Connected => "#9BE28F",
        BrowserConnectionState.ConnectionFailed => "#EF7777",
        BrowserConnectionState.BrowserMissing or BrowserConnectionState.ExtensionRequired or BrowserConnectionState.DesktopConnectionRequired or BrowserConnectionState.ReadyToTest or BrowserConnectionState.NotConfigured => "#F0B45B",
        _ => "#8D9CA2"
    };

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";
}
