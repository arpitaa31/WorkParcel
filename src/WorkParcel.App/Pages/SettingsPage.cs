using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private bool _updatingPrivacy;
    private CheckBox? _captureToggle;
    private CheckBox? _tabClosingToggle;
    private readonly HashSet<string> _expandedBrowsers = new(StringComparer.OrdinalIgnoreCase);

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
        var feedback = Ui.Text($"CURRENT THEME: {Store.Theme.ToUpperInvariant()}", 10, false, "#8D9CA2");
        var row = Ui.Row();
        foreach (var theme in new[] { "System", "Light", "Dark" })
        {
            var button = Ui.Button(theme.ToUpperInvariant());
            button.Click += async (_, _) => await RunButtonActionAsync(button, feedback, "SAVING", async () =>
            {
                await Store.SetThemeAsync(theme);
                RequestedTheme = theme == "Dark" ? ElementTheme.Dark : theme == "Light" ? ElementTheme.Light : ElementTheme.Default;
                feedback.Text = $"CURRENT THEME: {theme.ToUpperInvariant()}";
            }, "THEME SAVED");
            row.Children.Add(button);
        }
        stack.Children.Add(row);
        stack.Children.Add(feedback);
        return stack;
    }

    private UIElement BrowserTabs()
    {
        var stack = Ui.Stack(10);
        stack.Children.Add(Ui.Text("Connect a browser so WorkParcel can show your open tabs when you capture a setup.", 12, false, "#8D9CA2"));
        _browser.Children.Clear();
        _browser.Children.Add(Ui.Text("Checking browser connection...", 12, false, "#8D9CA2"));
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
            card.Children.Add(Ui.Mono($"{details.EligibleTabCount} eligible tabs available   |   {details.WindowCount} browser windows found   |   Last checked {Ui.Relative(checkedAt)}", 10, "#9BE28F"));
        }

        var actionFeedback = Ui.Text("READY", 10, false, "#8D9CA2");
        var actions = Ui.Row();
        var primaryText = PrimaryAction(state.State, name);
        if (primaryText is not null)
        {
            var primary = Ui.Button(primaryText, true);
            primary.Click += async (_, _) => await RunButtonActionAsync(primary, actionFeedback, "WORKING", () => RunPrimaryActionAsync(browser, state.State, actionFeedback), "ACTION COMPLETE");
            actions.Children.Add(primary);
        }

        if (state.State == BrowserConnectionState.Connected)
        {
            var disconnect = Ui.LinkButton("Disconnect");
            disconnect.Click += async (_, _) => await RunButtonActionAsync(disconnect, actionFeedback, "DISCONNECTING", async () =>
            {
                BrowserIntegrationService.Current.Disconnect(browser);
                await RefreshBrowserAsync();
            }, "DISCONNECTED");
            actions.Children.Add(disconnect);
        }
        else if (state.State == BrowserConnectionState.ConnectionFailed)
        {
            var detailsLink = Ui.LinkButton("View details");
            detailsLink.Click += async (_, _) => await RunButtonActionAsync(detailsLink, actionFeedback, "OPENING", () => ShowDiagnosticsAsync(browser, state), "DIAGNOSTICS CLOSED");
            actions.Children.Add(detailsLink);
        }
        else if (state.State != BrowserConnectionState.BrowserMissing)
        {
            var how = Ui.LinkButton("How does this work?");
            how.Click += async (_, _) => await RunButtonActionAsync(how, actionFeedback, "OPENING", () => ShowBrowserHowItWorksAsync(browser), "HOW-TO CLOSED");
            actions.Children.Add(how);
        }
        card.Children.Add(actions);
        card.Children.Add(actionFeedback);

        card.Children.Add(BuildAdvancedSection(browser, state));
        return Ui.Card(card, 12, 3);
    }

    private static Border BrowserMark(string browser)
    {
        var color = browser == "edge" ? "#67C7FF" : "#9BE28F";
        var label = browser == "edge" ? "EDG" : "CHR";
        return new Border { Width = 42, Height = 42, Background = Ui.Resource("SubtleSurfaceBrush"), BorderBrush = Ui.Brush(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = Ui.Mono(label, 11, color, true), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }

    private UIElement BuildAdvancedSection(string browser, BrowserConnectionStateInfo state)
    {
        var toggle = Ui.LinkButton("›  ADVANCED DETAILS");
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        var initiallyExpanded = _expandedBrowsers.Contains(browser);
        var content = new Border
        {
            Child = AdvancedDetails(browser, state),
            Visibility = initiallyExpanded ? Visibility.Visible : Visibility.Collapsed,
            Opacity = 1
        };
        if (initiallyExpanded) SetButtonText(toggle, "⌄  ADVANCED DETAILS");
        toggle.Click += (_, _) => ToggleAdvanced(browser, toggle, content);
        var stack = Ui.Stack(4);
        stack.Children.Add(toggle);
        stack.Children.Add(content);
        return stack;
    }

    private void ToggleAdvanced(string browser, Button toggle, Border content)
    {
        if (!toggle.IsEnabled) return;
        toggle.IsEnabled = false;
        try
        {
            var expanding = content.Visibility != Visibility.Visible;
            if (expanding) _expandedBrowsers.Add(browser);
            else _expandedBrowsers.Remove(browser);
            SetButtonText(toggle, expanding ? "⌄  ADVANCED DETAILS" : "›  ADVANCED DETAILS");
            content.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            content.Opacity = 1;

            // The visibility change is the source of truth. The short scale
            // animation is best-effort so a composition/runtime limitation can
            // never make this control unresponsive or crash the page.
            if (expanding)
            {
                try
                {
                    var visual = ElementCompositionPreview.GetElementVisual(content);
                    visual.Scale = new System.Numerics.Vector3(.98f, .98f, 1f);
                    var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
                    animation.InsertKeyFrame(1, System.Numerics.Vector3.One);
                    animation.Duration = TimeSpan.FromMilliseconds(160);
                    visual.StartAnimation(nameof(visual.Scale), animation);
                }
                catch { }
            }
        }
        finally { toggle.IsEnabled = true; }
    }

    private UIElement AdvancedDetails(string browser, BrowserConnectionStateInfo state)
    {
        var info = state.Details;
        var stack = Ui.Stack(6);
        AddDetail(stack, "Browser installed", info.BrowserInstalled ? "Yes" : "No");
        AddDetail(stack, "Extension detected", info.ExtensionDetected ? "Yes" : "Not detected");
        AddDetail(stack, "Desktop connection", info.HostInstalled ? "Registered" : "Not registered");
        AddDetail(stack, "Protocol status", info.ProtocolVersion == 0 ? "Not detected" : $"v{info.ProtocolVersion} - {(info.ProtocolCompatible ? "compatible" : "incompatible")}");
        AddDetail(stack, "Extension version", info.ExtensionVersion ?? "Not detected");
        AddDetail(stack, "Registered extension IDs", HostRegistrationService.GetRegisteredExtensionIds(browser) ?? "Not available");
        AddDetail(stack, "Connection ID", info.ConnectionId ?? "Not connected");
        AddDetail(stack, "Last successful check", info.LastConnectedUtc?.ToLocalTime().ToString("g") ?? "Never");
        AddDetail(stack, "Browser windows detected", info.WindowCount.ToString());
        AddDetail(stack, "Eligible tabs detected", info.EligibleTabCount.ToString());
        if (!string.IsNullOrWhiteSpace(info.LastError)) AddDetail(stack, "Last diagnostic", info.LastError!);

        if (state.State != BrowserConnectionState.BrowserMissing)
        {
            var actions = Ui.Stack(4);
            var feedback = Ui.Text("READY", 10, false, "#8D9CA2");
            AddLinkAction(actions, "REFRESH STATUS", feedback, () => RefreshBrowserAsync(), "REFRESHING", "STATUS REFRESHED");
            AddLinkAction(actions, "TEST CONNECTION", feedback, () => TestBrowserConnectionAsync(browser, true, feedback), "CHECKING", "CONNECTION CHECK COMPLETE");
            AddLinkAction(actions, "REPAIR CONNECTION", feedback, () => ShowGuidedSetupAsync(browser), "OPENING", "SETUP DIALOG CLOSED");
            AddLinkAction(actions, "VIEW SETUP HELP", feedback, () => ShowSetupHelpAsync(browser), "OPENING", "SETUP HELP CLOSED");
            AddLinkAction(actions, "OPEN EXTENSION FOLDER", feedback, () => OpenExtensionFolderAsync(feedback), "OPENING", "EXTENSION FOLDER OPENED");
            AddLinkAction(actions, "OPEN DIAGNOSTICS", feedback, () => ShowDiagnosticsAsync(browser, state), "OPENING", "DIAGNOSTICS CLOSED");
            if (info.HostInstalled) AddLinkAction(actions, "REMOVE CONNECTION", feedback, () => RemoveRegistrationAsync(browser), "REMOVING", "CONNECTION REMOVAL COMPLETE");
            stack.Children.Add(actions);
            stack.Children.Add(feedback);
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

    private void AddLinkAction(StackPanel stack, string label, TextBlock feedback, Func<Task> action, string busyLabel, string successLabel)
    {
        var button = Ui.LinkButton(label);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Click += async (_, _) => await RunButtonActionAsync(button, feedback, busyLabel, action, successLabel);
        stack.Children.Add(button);
    }

    private async Task RunButtonActionAsync(Button button, TextBlock feedback, string busyLabel, Func<Task> action, string successLabel)
    {
        if (!button.IsEnabled) return;
        var originalLabel = ButtonText(button);
        button.IsEnabled = false;
        SetButtonText(button, $"{busyLabel}...");
        feedback.Text = $"{busyLabel}...";
        feedback.Foreground = Ui.Brush("#F0B45B");
        try
        {
            await action();
            if (feedback.Text == $"{busyLabel}...")
            {
                feedback.Text = successLabel;
                feedback.Foreground = Ui.Brush("#9BE28F");
            }
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            var message = exception is UserFacingActionException ? exception.Message : "ACTION FAILED - TRY AGAIN";
            feedback.Text = message;
            feedback.Foreground = Ui.Brush("#EF7777");
            await Dialogs.ShowMessage(this, "ACTION FAILED", message);
        }
        finally
        {
            SetButtonText(button, originalLabel);
            button.IsEnabled = true;
        }
    }

    private static string ButtonText(Button button) => button.Content is TextBlock text ? text.Text : button.Content?.ToString() ?? string.Empty;

    private static void SetButtonText(Button button, string value)
    {
        if (button.Content is TextBlock text) text.Text = value;
        else button.Content = Ui.Mono(value, 11, null, true);
    }

    private async Task RunPrimaryActionAsync(string browser, BrowserConnectionState state, TextBlock? feedback = null)
    {
        switch (state)
        {
            case BrowserConnectionState.Connected:
                await TestBrowserConnectionAsync(browser, true, feedback);
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
        var name = BrowserName(browser);
        var content = Ui.Stack(10);
        var feedback = Ui.Text("READY - FOLLOW THE STEPS ABOVE", 10, false, "#8D9CA2");
        var extensionFolder = Ui.Button("OPEN EXTENSION FOLDER");
        var setupHelp = Ui.LinkButton("VIEW DESKTOP SETUP HELP");
        var dialog = new ContentDialog
        {
            Title = $"CONNECT {name.ToUpperInvariant()}",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
            PrimaryButtonText = "CHECK CONNECTION",
            SecondaryButtonText = $"OPEN {name.ToUpperInvariant()} EXTENSIONS",
            CloseButtonText = "CANCEL",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var busy = false;

        void Render()
        {
            var guidedStep = BrowserSetupStepLogic.For(current);
            content.Children.Clear();
            content.Children.Add(Ui.Text($"Connect {name} so WorkParcel can show the tabs you choose when capturing a setup.", 13, true));
            content.Children.Add(GuidedProgress(guidedStep));
            AddGuidedExplanation(content, browser, current, guidedStep);
            content.Children.Add(extensionFolder);
            if (current.State is BrowserConnectionState.DesktopConnectionRequired or BrowserConnectionState.ConnectionFailed) content.Children.Add(setupHelp);
            content.Children.Add(feedback);
            dialog.IsPrimaryButtonEnabled = true;
            dialog.IsSecondaryButtonEnabled = true;
        }

        extensionFolder.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            extensionFolder.IsEnabled = false;
            feedback.Text = "OPENING EXTENSION FOLDER...";
            feedback.Foreground = Ui.Brush("#F0B45B");
            try
            {
                var result = OpenExtensionFolder();
                feedback.Text = result.Succeeded ? "EXTENSION FOLDER OPENED" : result.Message;
                feedback.Foreground = Ui.Brush(result.Succeeded ? "#9BE28F" : "#EF7777");
            }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                feedback.Text = "EXTENSION FOLDER COULD NOT BE OPENED";
                feedback.Foreground = Ui.Brush("#EF7777");
            }
            finally { busy = false; extensionFolder.IsEnabled = true; }
        };

        setupHelp.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            setupHelp.IsEnabled = false;
            try { await ShowSetupHelpAsync(browser); feedback.Text = "SETUP HELP CLOSED"; feedback.Foreground = Ui.Brush("#9BE28F"); }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); feedback.Text = "SETUP HELP COULD NOT BE OPENED"; feedback.Foreground = Ui.Brush("#EF7777"); }
            finally { busy = false; setupHelp.IsEnabled = true; }
        };

        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy) return;
            busy = true;
            dialog.IsSecondaryButtonEnabled = false;
            feedback.Text = $"OPENING {name.ToUpperInvariant()} EXTENSIONS...";
            feedback.Foreground = Ui.Brush("#F0B45B");
            try
            {
                var result = ExternalLaunchService.TryOpenBrowserExtensions(browser);
                feedback.Text = result.Succeeded ? $"{name.ToUpperInvariant()} EXTENSIONS OPENED" : result.Message;
                feedback.Foreground = Ui.Brush(result.Succeeded ? "#9BE28F" : "#EF7777");
            }
            finally { busy = false; dialog.IsSecondaryButtonEnabled = true; }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy) return;
            if (current.State == BrowserConnectionState.Connected)
            {
                dialog.Hide();
                return;
            }

            busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            feedback.Text = "CHECKING CONNECTION...";
            feedback.Foreground = Ui.Brush("#F0B45B");
            try
            {
                current = await DetectBrowserAsync(browser);
                if (current.State == BrowserConnectionState.Connected)
                {
                    try { await BrowserIntegrationService.Current.ListTabsAsync(browser); }
                    catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
                    current = await DetectBrowserAsync(browser);
                }
                feedback.Text = ConnectionFeedback(current);
                feedback.Foreground = Ui.Brush(current.State == BrowserConnectionState.Connected ? "#9BE28F" : "#EF7777");
                Render();
                if (current.State == BrowserConnectionState.Connected) await RefreshBrowserAsync();
            }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                feedback.Text = "CONNECTION FAILED - TRY AGAIN";
                feedback.Foreground = Ui.Brush("#EF7777");
            }
            finally { busy = false; dialog.IsPrimaryButtonEnabled = true; }
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
            content.Children.Add(Ui.Text("If you loaded the extension, continue by registering the desktop connection.", 12));
            content.Children.Add(Ui.Text("1. Copy the exact extension ID shown on the browser extensions page.\n2. Run the current-user setup command below.\n3. Return here and continue to the connection check.", 12));
            content.Children.Add(Ui.Mono(SetupCommand(browser), 10));
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
                break;
            case BrowserConnectionState.DesktopConnectionRequired:
                content.Children.Add(Ui.Text("The extension is present, but it is not registered with the WorkParcel desktop connection yet.", 12));
                content.Children.Add(Ui.Text("The browser assigns the exact extension ID. WorkParcel must use that exact ID when registering the native connection, so this step cannot be safely automated.", 12));
                content.Children.Add(Ui.Mono(SetupCommand(browser), 10));
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

    private async Task<bool> TestBrowserConnectionAsync(string browser, bool showMessage, TextBlock? feedback = null)
    {
        try
        {
            var before = await DetectBrowserAsync(browser);
            if (before.State != BrowserConnectionState.Connected)
            {
                SetConnectionFeedback(feedback, before);
                if (showMessage) await Dialogs.ShowMessage(this, "CONNECTION NEEDS ATTENTION", StateDescription(before.State, before.Details, BrowserName(browser)));
                return false;
            }

            var tabs = await BrowserIntegrationService.Current.ListTabsAsync(browser);
            var after = await DetectBrowserAsync(browser);
            if (after.State != BrowserConnectionState.Connected)
            {
                SetConnectionFeedback(feedback, after);
                if (showMessage) await Dialogs.ShowMessage(this, "CONNECTION NEEDS ATTENTION", StateDescription(after.State, after.Details, BrowserName(browser)));
                return false;
            }
            var supported = tabs.Count(tab => BrowserTabRules.IsAllowedForCapture(tab));
            SetConnectionFeedback(feedback, after);
            if (showMessage) await Dialogs.ShowMessage(this, $"{BrowserName(browser).ToUpperInvariant()} CONNECTED", $"{supported} eligible HTTP/HTTPS tab{(supported == 1 ? string.Empty : "s")} available. Private and browser-internal pages are excluded.");
            return true;
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            if (feedback is not null)
            {
                feedback.Text = "CONNECTION FAILED";
                feedback.Foreground = Ui.Brush("#EF7777");
            }
            if (showMessage) await Dialogs.ShowMessage(this, "CONNECTION NEEDS ATTENTION", "The browser extension or desktop connection did not respond. Reconnect and try again.");
            return false;
        }
        finally { await RefreshBrowserAsync(); }
    }

    private static void SetConnectionFeedback(TextBlock? feedback, BrowserConnectionStateInfo state)
    {
        if (feedback is null) return;
        feedback.Text = ConnectionFeedback(state);
        feedback.Foreground = Ui.Brush(state.State == BrowserConnectionState.Connected ? "#9BE28F" : "#EF7777");
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
        await Dialogs.ShowBrowserTabsHowItWorksAsync(this);
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
        var extensionFolder = InstalledResourcePathResolver.FindFromAppBase("browser-extension");
        var hostExecutable = InstalledResourcePathResolver.FindFromAppBase(Path.Combine("BrowserHost", "WorkParcel.BrowserHost.exe"));
        var setupScript = InstalledResourcePathResolver.FindFromAppBase(Path.Combine("BrowserHost", "Setup-BrowserHost.ps1"));
        var folderLine = extensionFolder ?? "The browser-extension folder was not included in this installation.";
        var command = SetupCommand(browser, setupScript, hostExecutable, name);
        await Dialogs.ShowMessage(this, $"CONNECT {name}", $"1. Load this folder as an unpacked extension:\n{folderLine}\n\n2. Copy the exact extension ID shown by {name}.\n\n3. Run this current-user setup command:\n{command}\n\n4. Return to WorkParcel and choose CHECK CONNECTION. The exact ID is required by browser security; WorkParcel will never register a wildcard origin.");
    }

    private static string SetupCommand(string browser)
    {
        var name = BrowserName(browser).ToUpperInvariant();
        var hostExecutable = InstalledResourcePathResolver.FindFromAppBase(Path.Combine("BrowserHost", "WorkParcel.BrowserHost.exe"));
        var setupScript = InstalledResourcePathResolver.FindFromAppBase(Path.Combine("BrowserHost", "Setup-BrowserHost.ps1"));
        return SetupCommand(browser, setupScript, hostExecutable, name);
    }

    private static string SetupCommand(string browser, string? setupScript, string? hostExecutable, string name)
    {
        return setupScript is not null && hostExecutable is not null
            ? $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{setupScript}\" -Browser {name} -HostExecutablePath \"{hostExecutable}\" -{(browser == "chrome" ? "Chrome" : "Edge")}ExtensionId <exact-id>"
            : "Run the Setup-BrowserHost.ps1 file shipped with this build using the WorkParcel.BrowserHost.exe beside it.";
    }

    private static ExternalLaunchResult OpenExtensionFolder()
    {
        var path = InstalledResourcePathResolver.FindFromAppBase("browser-extension");
        return path is null
            ? ExternalLaunchResult.Failure("browser-extension", "The WorkParcel browser extension was not included in this installation. Expected a browser-extension folder beside WorkParcel.exe.")
            : ExternalLaunchService.TryOpenFolder(path);
    }

    private static Task OpenExtensionFolderAsync(TextBlock feedback)
    {
        var result = OpenExtensionFolder();
        if (!result.Succeeded) throw new UserFacingActionException(result.Message);
        return Task.CompletedTask;
    }

    private static string ConnectionFeedback(BrowserConnectionStateInfo state) => state.State switch
    {
        BrowserConnectionState.Connected => "CONNECTED",
        BrowserConnectionState.ExtensionRequired => "EXTENSION NOT DETECTED",
        BrowserConnectionState.DesktopConnectionRequired => "DESKTOP CONNECTION UNAVAILABLE",
        BrowserConnectionState.BrowserMissing => "BROWSER UNAVAILABLE",
        BrowserConnectionState.ConnectionFailed => "CONNECTION FAILED",
        BrowserConnectionState.Disabled => "BROWSER TAB CAPTURE IS DISABLED",
        _ => "CONNECTION NOT READY"
    };

    private UIElement TabPrivacy()
    {
        var service = BrowserIntegrationService.Current;
        var stack = Ui.Stack(7);
        stack.Children.Add(Ui.Text("You choose which tabs are saved. WorkParcel stores only the details needed to reopen them.", 12, false, "#8D9CA2"));

        var capture = new CheckBox { Content = "Allow browser tab capture\nShow eligible browser tabs when capturing a setup.", IsChecked = service.IsEnabled, IsThreeState = false };
        _captureToggle = capture;
        var feedback = Ui.Text("READY", 10, false, "#8D9CA2");
        capture.Checked += (_, _) =>
        {
            if (_updatingPrivacy) return;
            service.SetEnabled(true);
            feedback.Text = "BROWSER TAB CAPTURE ENABLED";
            feedback.Foreground = Ui.Brush("#9BE28F");
        };
        capture.Unchecked += (_, _) =>
        {
            if (_updatingPrivacy) return;
            service.SetEnabled(false);
            feedback.Text = "BROWSER TAB CAPTURE DISABLED";
            feedback.Foreground = Ui.Brush("#F0B45B");
        };
        stack.Children.Add(capture);

        var closing = new CheckBox { Content = "Close selected tabs when packing away\nOnly tabs saved in that parcel may be closed.", IsChecked = service.TabClosingEnabled, IsThreeState = false };
        _tabClosingToggle = closing;
        closing.Checked += (_, _) =>
        {
            if (_updatingPrivacy) return;
            service.SetTabClosingEnabled(true);
            feedback.Text = "SELECTED TAB CLOSING ENABLED";
            feedback.Foreground = Ui.Brush("#9BE28F");
        };
        closing.Unchecked += (_, _) =>
        {
            if (_updatingPrivacy) return;
            service.SetTabClosingEnabled(false);
            feedback.Text = "SELECTED TAB CLOSING DISABLED";
            feedback.Foreground = Ui.Brush("#F0B45B");
        };
        stack.Children.Add(closing);

        stack.Children.Add(new CheckBox { Content = "Exclude private browsing\nIncognito and InPrivate tabs are never captured.", IsChecked = true, IsEnabled = false, IsThreeState = false });
        var stores = Ui.LinkButton("WHAT WORKPARCEL STORES");
        stores.HorizontalAlignment = HorizontalAlignment.Left;
        stores.Click += async (_, _) => await RunButtonActionAsync(stores, feedback, "OPENING", ShowWhatStoresAsync, "STORAGE DETAILS CLOSED");
        stack.Children.Add(stores);
        stack.Children.Add(feedback);
        return stack;
    }

    private async Task ShowWhatStoresAsync()
    {
        await Dialogs.ShowMessage(this, "WHAT WORKPARCEL STORES", "WorkParcel may store:\n- Tab title\n- URL\n- Browser type\n- Tab order\n- Pinned state\n- Browser-window grouping\n\nWorkParcel does not intentionally store:\n- Passwords\n- Cookies\n- Form contents\n- Page contents\n- Keystrokes\n- Full browser history");
    }

    private async Task ClearIconCacheAsync(TextBlock? feedback = null)
    {
        try
        {
            var path = Store.Paths.IconCacheDirectory;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            Store.Paths.EnsureDirectories();
            await Dialogs.ShowMessage(this, "ICON CACHE CLEARED", "Local file and application icons were removed. Saved browser records were not changed.");
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            if (feedback is not null)
            {
                feedback.Text = "CACHE NOT CLEARED";
                feedback.Foreground = Ui.Brush("#EF7777");
            }
            await Dialogs.ShowMessage(this, "CACHE NOT CLEARED", "The favicon cache could not be removed. Your saved tabs were not changed.");
        }
    }

    private UIElement About()
    {
        var stack = Ui.Stack(4);
        stack.Children.Add(Ui.Text("WorkParcel", 14, true));
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.2.0-beta.2";
        version = version.Replace("-beta.", " Beta ", StringComparison.OrdinalIgnoreCase).Replace("-", " ", StringComparison.OrdinalIgnoreCase);
        stack.Children.Add(Ui.Mono($"VERSION {version.ToUpperInvariant()}", 10));
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
            var feedback = Ui.Text("READY", 10, false, "#8D9CA2");
            var open = Ui.Button("OPEN DATA FOLDER");
            open.Click += async (_, _) => await RunButtonActionAsync(open, feedback, "OPENING", () => OpenDataFolderAsync(feedback), "DATA FOLDER OPENED");
            var backup = Ui.Button("BACK UP DATA");
            backup.Click += async (_, _) => await RunButtonActionAsync(backup, feedback, "BACKING UP", async () =>
            {
                await Store.BackupAsync();
                await Dialogs.ShowMessage(this, "BACKUP CREATED", "A new SQLite backup was saved without overwriting an earlier backup.");
            }, "BACKUP CREATED");
            var clear = Ui.Button("CLEAR ICON CACHE");
            clear.Click += async (_, _) => await RunButtonActionAsync(clear, feedback, "CLEARING", () => ClearIconCacheAsync(feedback), "CACHE CLEARED");
            row.Children.Add(open); row.Children.Add(backup); row.Children.Add(clear); _data.Children.Add(row); _data.Children.Add(feedback);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); _data.Children.Clear(); _data.Children.Add(Ui.Text("Local data information is unavailable.", 12, false, "#F0B45B")); }
    }

    private static Task OpenDataFolderAsync(TextBlock feedback)
    {
        var result = ExternalLaunchService.TryOpenFolder(WorkspaceStore.Current.Paths.DataDirectory);
        if (!result.Succeeded) throw new UserFacingActionException(result.Message);
        return Task.CompletedTask;
    }

    private sealed class UserFacingActionException : Exception
    {
        public UserFacingActionException(string message) : base(message) { }
    }

    private static string BrowserName(string browser) => browser.Equals("edge", StringComparison.OrdinalIgnoreCase) ? "Edge" : "Chrome";

    private static string StateTitle(BrowserConnectionState state, string browser) => state switch
    {
        BrowserConnectionState.Connected => "Connected",
        BrowserConnectionState.ExtensionRequired => "Extension not detected",
        BrowserConnectionState.DesktopConnectionRequired => "Desktop connection unavailable",
        BrowserConnectionState.BrowserMissing => "Browser unavailable",
        BrowserConnectionState.ConnectionFailed => "Connection failed",
        BrowserConnectionState.Disabled => "Not connected",
        BrowserConnectionState.ReadyToTest => "Connection ready to test",
        _ => "Not connected"
    };

    private static string StateDescription(BrowserConnectionState state, BrowserConnectionInfo? info, string browser) => state switch
    {
        BrowserConnectionState.Connected => $"Your open {browser} windows and tabs are ready to capture.",
        BrowserConnectionState.BrowserMissing => $"{browser} is not installed or could not be found on this computer.",
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
