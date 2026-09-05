using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using WorkParcel.Core.Browser;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Xunit;

namespace WorkParcel.Tests;

[CollectionDefinition("BrowserIntegration", DisableParallelization = true)]
public sealed class BrowserIntegrationCollection { }

[Collection("BrowserIntegration")]
public sealed class BrowserIntegrationTests
{
    [Fact]
    public void BrowserConnectionStateMapsTechnicalFlagsToOneUserFacingNextStep()
    {
        var missing = Details(BrowserConnectionStatus.BrowserNotFound, installed: false);
        Assert.Equal(BrowserConnectionState.BrowserMissing, BrowserConnectionStateLogic.Derive(missing));

        var extensionRequired = Details(BrowserConnectionStatus.ExtensionNotDetected, hostInstalled: true);
        Assert.Equal(BrowserConnectionState.ExtensionRequired, BrowserConnectionStateLogic.Derive(extensionRequired));

        var extensionAndDesktopRequired = Details(BrowserConnectionStatus.NativeHostNotInstalled, hostInstalled: false);
        Assert.Equal(BrowserConnectionState.ExtensionRequired, BrowserConnectionStateLogic.Derive(extensionAndDesktopRequired));

        var desktopRequired = Details(BrowserConnectionStatus.NativeHostNotInstalled, hostInstalled: false, extensionDetected: true);
        Assert.Equal(BrowserConnectionState.DesktopConnectionRequired, BrowserConnectionStateLogic.Derive(desktopRequired));

        var notConfigured = Details(BrowserConnectionStatus.BrowserNotRunning, hostInstalled: false);
        Assert.Equal(BrowserConnectionState.NotConfigured, BrowserConnectionStateLogic.Derive(notConfigured));

        var ready = Details(BrowserConnectionStatus.Connecting, hostInstalled: true, extensionDetected: true);
        Assert.Equal(BrowserConnectionState.ReadyToTest, BrowserConnectionStateLogic.Derive(ready));

        var connected = Details(BrowserConnectionStatus.Connected, hostInstalled: true, extensionDetected: true);
        Assert.Equal(BrowserConnectionState.Connected, BrowserConnectionStateLogic.Derive(connected));
        Assert.Equal(BrowserConnectionState.Connected, BrowserConnectionStateLogic.Derive(Details(BrowserConnectionStatus.Connected, installed: false, hostInstalled: false, extensionDetected: true)));

        var failed = Details(BrowserConnectionStatus.ConnectionError, hostInstalled: true, extensionDetected: true);
        Assert.Equal(BrowserConnectionState.ConnectionFailed, BrowserConnectionStateLogic.Derive(failed));

        var registeredButClosed = Details(BrowserConnectionStatus.BrowserNotRunning, hostInstalled: true);
        Assert.Equal(BrowserConnectionState.ConnectionFailed, BrowserConnectionStateLogic.Derive(registeredButClosed));

        var disabled = Details(BrowserConnectionStatus.Disabled, installed: false);
        Assert.Equal(BrowserConnectionState.Disabled, BrowserConnectionStateLogic.Derive(disabled));

        var intentionallyDisconnected = Details(BrowserConnectionStatus.Disconnected, hostInstalled: true, extensionDetected: true);
        Assert.Equal(BrowserConnectionState.NotConfigured, BrowserConnectionStateLogic.Derive(intentionallyDisconnected));
        Assert.Equal(BrowserConnectionState.BrowserMissing, BrowserConnectionStateLogic.Derive(Details(BrowserConnectionStatus.Disconnected, installed: false)));
    }

    [Fact]
    public void BrowserPreferencesPersistTabClosingAndCapturePolicy()
    {
        using var temp = new TestDirectory();
        var preferences = Path.Combine(temp.Path, "browser-preferences.json");
        using (var first = new BrowserIntegrationService($"WorkParcel.Browser.Test.{Guid.NewGuid():N}", preferencesPath: preferences))
        {
            first.SetTabClosingEnabled(false);
            first.SetEnabled(false);
            Assert.False(first.TabClosingEnabled);
            Assert.False(first.IsEnabled);
        }

        using var restarted = new BrowserIntegrationService($"WorkParcel.Browser.Test.{Guid.NewGuid():N}", preferencesPath: preferences);
        Assert.False(restarted.TabClosingEnabled);
        Assert.False(restarted.IsEnabled);
    }

    [Fact]
    public void BrowserConnectionStatesExposeTheCorrectNextAction()
    {
        Assert.Null(BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.BrowserMissing, "chrome"));
        Assert.Equal("CONNECT CHROME", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.NotConfigured, "chrome"));
        Assert.Equal("INSTALL EXTENSION", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.ExtensionRequired, "chrome"));
        Assert.Equal("CONNECT TO WORKPARCEL", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.DesktopConnectionRequired, "chrome"));
        Assert.Equal("TEST CONNECTION", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.ReadyToTest, "chrome"));
        Assert.Equal("REFRESH TABS", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.Connected, "chrome"));
        Assert.Equal("FIX CONNECTION", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.ConnectionFailed, "chrome"));
        Assert.Equal("ENABLE BROWSER TABS", BrowserConnectionStateLogic.PrimaryAction(BrowserConnectionState.Disabled, "chrome"));
    }

    [Fact]
    public void GuidedSetupStepFollowsFreshlyDetectedConnectionState()
    {
        Assert.Equal(BrowserSetupStep.InstallExtension, SetupStep(Details(BrowserConnectionStatus.ExtensionNotDetected, hostInstalled: true)));
        Assert.Equal(BrowserSetupStep.ConnectDesktop, SetupStep(Details(BrowserConnectionStatus.NativeHostNotInstalled, hostInstalled: false, extensionDetected: true)));
        Assert.Equal(BrowserSetupStep.TestConnection, SetupStep(Details(BrowserConnectionStatus.Connecting, hostInstalled: true, extensionDetected: true)));
        Assert.Equal(BrowserSetupStep.TestConnection, SetupStep(Details(BrowserConnectionStatus.ConnectionError, hostInstalled: true, extensionDetected: true)));
        Assert.Equal(BrowserSetupStep.Complete, SetupStep(Details(BrowserConnectionStatus.Connected, hostInstalled: true, extensionDetected: true)));
        Assert.Equal(BrowserSetupStep.InstallExtension, SetupStep(Details(BrowserConnectionStatus.Disabled, installed: false)));
        Assert.Equal(BrowserSetupStep.InstallExtension, SetupStep(Details(BrowserConnectionStatus.BrowserNotFound, installed: false)));
    }

    [Fact]
    public void RemovingBrowserRegistrationReportsSuccessOnlyWhenTheRegistrationIsGone()
    {
        var exists = true;
        Assert.True(HostRegistrationService.TryRemoveRegistration("test-registration", _ => exists = false, _ => exists));
        Assert.False(exists);
    }

    [Fact]
    public void RemovingBrowserRegistrationFailureIsTruthfulAndRetryable()
    {
        Assert.False(HostRegistrationService.TryRemoveRegistration("test-registration", _ => throw new IOException("locked"), _ => true));
    }

    [Fact]
    public void BrowserPreferenceChangesNotifySettingsImmediately()
    {
        using var temp = new TestDirectory();
        using var service = new BrowserIntegrationService($"WorkParcel.Browser.Test.{Guid.NewGuid():N}", preferencesPath: Path.Combine(temp.Path, "preferences.json"));
        var notifications = 0;
        service.StateChanged += (_, _) => notifications++;

        service.SetTabClosingEnabled(false);
        service.SetEnabled(false);

        Assert.Equal(2, notifications);
        Assert.False(service.TabClosingEnabled);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void ProtocolRejectsMissingOrOverlongMessageType()
    {
        var missing = Encoding.UTF8.GetBytes("{\"version\":1,\"requestId\":\"request\",\"type\":null,\"timestampUtc\":\"2026-01-01T00:00:00Z\",\"payload\":{}}");
        Assert.False(BrowserProtocol.TryDeserialize(missing, out _, out var missingError));
        Assert.Contains("type", missingError, StringComparison.OrdinalIgnoreCase);

        var overlong = new string('x', 65);
        var body = Encoding.UTF8.GetBytes($"{{\"version\":1,\"requestId\":\"request\",\"type\":\"{overlong}\",\"timestampUtc\":\"2026-01-01T00:00:00Z\",\"payload\":{{}}}}");
        Assert.False(BrowserProtocol.TryDeserialize(body, out _, out var overlongError));
        Assert.Contains("type", overlongError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserTabMatchingUsesSavedWindowGroupingAndRefusesUnresolvableDuplicates()
    {
        var savedOne = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "window-0", Value = "https://same.example/", BrowserTabIndex = 0 };
        var savedTwo = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "window-1", Value = "https://same.example/", BrowserTabIndex = 0 };
        var matched = new HashSet<Guid>(); var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var live = new BrowserTabData { Browser = "chrome", WindowGroupKey = "window-0", Url = "https://same.example/", TabIndex = 0, CanRestore = true };
        var selected = BrowserIntegrationService.FindSavedTab(new[] { savedOne, savedTwo }, live, matched, groups);
        Assert.Same(savedOne, selected);
        matched.Add(savedOne.Id);
        BrowserIntegrationService.ApplyLiveTab(savedOne, live, updateSavedWindowGroup: true);
        Assert.Equal("window-0", savedOne.BrowserWindowGroupId);

        var duplicateOne = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "a", Value = "https://duplicate.example/" };
        var duplicateTwo = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "b", Value = "https://duplicate.example/" };
        Assert.Null(BrowserIntegrationService.FindSavedTab(new[] { duplicateOne, duplicateTwo }, new BrowserTabData { Browser = "chrome", WindowGroupKey = "new-window", Url = "https://duplicate.example/", CanRestore = true }, new HashSet<Guid>(), new Dictionary<string, string>()));
    }

    [Fact]
    public void BrowserTabMatchingScopesWindowAssignmentsByBrowserConnection()
    {
        var chrome = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "chrome-saved", Value = "https://same.example/"};
        var edge = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "edge", BrowserWindowGroupId = "edge-saved", Value = "https://same.example/"};
        var matched = new HashSet<Guid>();
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var selectedChrome = BrowserIntegrationService.FindSavedTab(new[] { chrome, edge }, new BrowserTabData { Browser = "chrome", ConnectionId = "chrome-connection", WindowGroupKey = "window-0", Url = chrome.Value, CanRestore = true }, matched, groups);
        Assert.Same(chrome, selectedChrome);
        matched.Add(chrome.Id);

        var selectedEdge = BrowserIntegrationService.FindSavedTab(new[] { chrome, edge }, new BrowserTabData { Browser = "edge", ConnectionId = "edge-connection", WindowGroupKey = "window-0", Url = edge.Value, CanRestore = true }, matched, groups);
        Assert.Same(edge, selectedEdge);
    }

    [Fact]
    public void NewlyDetectedDuplicateTabsRemainSeparateWhenTheirStableIdsAreMarkedMatched()
    {
        var first = new BrowserTabData { Browser = "chrome", ConnectionId = "connection", WindowGroupKey = "window-0", Url = "https://same.example/", TabIndex = 0, CanRestore = true };
        var second = first with { WindowGroupKey = "window-1", TabIndex = 1 };
        var detected = BrowserIntegrationService.FromTab(Guid.NewGuid(), first, 0, null);
        var matched = new HashSet<Guid> { detected.Id };

        Assert.Null(BrowserIntegrationService.FindSavedTab(new[] { detected }, second, matched, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task ChromeAndEdgeSessionsRouteSnapshotsIndependently()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var edge = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(chrome.ConnectAsync(5000), edge.ConnectAsync(5000));

        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await SendAsync(edge, BrowserProtocol.Create("hello", "edge-hello", "edge", new { browser = "edge" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        var chromeStatus = await ReceiveAsync(chrome);
        var edgeStatus = await ReceiveAsync(edge);
        Assert.Equal("connection_status", chromeStatus.Type);
        Assert.Equal("connection_status", edgeStatus.Type);
        Assert.Equal("chrome", chromeStatus.Browser);
        Assert.Equal("edge", edgeStatus.Browser);
        Assert.False(string.IsNullOrWhiteSpace(chromeStatus.ConnectionId));
        Assert.False(string.IsNullOrWhiteSpace(edgeStatus.ConnectionId));
        Assert.NotEqual(chromeStatus.ConnectionId, edgeStatus.ConnectionId);

        var chromeList = service.ListTabsAsync("chrome");
        var edgeList = service.ListTabsAsync("edge");
        var chromeRequest = await ReceiveAsync(chrome);
        var edgeRequest = await ReceiveAsync(edge);
        Assert.Equal("request_snapshot", chromeRequest.Type);
        Assert.Equal("request_snapshot", edgeRequest.Type);
        Assert.Equal(chromeStatus.ConnectionId, chromeRequest.ConnectionId);
        Assert.Equal(edgeStatus.ConnectionId, edgeRequest.ConnectionId);

        await SendAsync(chrome, BrowserProtocol.Create("tab_snapshot", chromeRequest.RequestId, "chrome", Snapshot("chrome", "https://chrome.example/"), chromeStatus.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));
        await SendAsync(edge, BrowserProtocol.Create("tab_snapshot", edgeRequest.RequestId, "edge", Snapshot("edge", "https://edge.example/"), edgeStatus.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));

        var chromeTab = Assert.Single(await chromeList);
        var edgeTab = Assert.Single(await edgeList);
        Assert.Equal("chrome", chromeTab.Browser);
        Assert.Equal("edge", edgeTab.Browser);
        Assert.Equal("https://chrome.example/", chromeTab.Url);
        Assert.Equal("https://edge.example/", edgeTab.Url);
        Assert.Equal(chromeStatus.ConnectionId, chromeTab.ConnectionId);
        Assert.Equal(edgeStatus.ConnectionId, edgeTab.ConnectionId);
    }

    [Fact]
    public async Task DisconnectStopsOnlyTheSelectedBrowserConnection()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var edge = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(chrome.ConnectAsync(5000), edge.ConnectAsync(5000));

        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await SendAsync(edge, BrowserProtocol.Create("hello", "edge-hello", "edge", new { browser = "edge" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await ReceiveAsync(chrome);
        await ReceiveAsync(edge);

        service.Disconnect("chrome");
        var immediate = service.GetConnectionState("chrome");
        Assert.Equal(BrowserConnectionStatus.Disconnected, immediate.Details.Status);
        Assert.Equal(BrowserConnectionState.NotConfigured, immediate.State);
        for (var attempt = 0; attempt < 20 && service.Connections.Any(connection => connection.Browser == "chrome"); attempt++) await Task.Delay(25);

        Assert.DoesNotContain(service.Connections, connection => connection.Browser == "chrome");
        Assert.Contains(service.Connections, connection => connection.Browser == "edge");
    }

    [Fact]
    public async Task IntentionalDisconnectIsShownAsNotConnectedInsteadOfAConnectionFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await ReceiveAsync(chrome);

        service.Disconnect("chrome");
        for (var attempt = 0; attempt < 20 && service.Connections.Any(connection => connection.Browser == "chrome"); attempt++) await Task.Delay(25);

        var state = service.GetConnectionState("chrome");
        Assert.Equal(BrowserConnectionStatus.Disconnected, state.Details.Status);
        Assert.Equal(BrowserConnectionState.NotConfigured, state.State);
    }

    [Fact]
    public async Task RepairSucceedsAfterACompatibleHandshakeFollowsAFailedConnection()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using (var failed = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await failed.ConnectAsync(5000);
            await SendAsync(failed, BrowserProtocol.Create("hello", "failed-hello", "chrome", new { browser = "chrome" }, extensionVersion: "0.0.1"));
            var response = await ReceiveAsync(failed);
            Assert.Equal("VERSION_MISMATCH", response.Payload.GetProperty("status").GetString());
            Assert.Equal(BrowserConnectionState.ConnectionFailed, service.GetConnectionState("chrome").State);
        }

        await using var repaired = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await repaired.ConnectAsync(5000);
        await SendAsync(repaired, BrowserProtocol.Create("hello", "repaired-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        var repairedResponse = await ReceiveAsync(repaired);

        Assert.Equal("CONNECTED", repairedResponse.Payload.GetProperty("status").GetString());
        Assert.Equal(BrowserConnectionStatus.Connected, service.GetStatus("chrome").Status);
        Assert.Equal(BrowserConnectionState.Connected, service.GetConnectionState("chrome").State);
    }

    [Fact]
    public async Task FailedRepairRemainsRetryableAndNeverClaimsAConnection()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var retry = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await retry.ConnectAsync(5000);
        await SendAsync(retry, BrowserProtocol.Create("hello", "retry-hello", "chrome", new { browser = "chrome" }, extensionVersion: "0.0.1"));
        var response = await ReceiveAsync(retry);

        Assert.Equal("VERSION_MISMATCH", response.Payload.GetProperty("status").GetString());
        var state = service.GetConnectionState("chrome");
        Assert.Equal(BrowserConnectionStatus.VersionMismatch, state.Details.Status);
        Assert.Equal(BrowserConnectionState.ConnectionFailed, state.State);
        Assert.Equal("FIX CONNECTION", BrowserConnectionStateLogic.PrimaryAction(state.State, "chrome"));
    }

    [Fact]
    public async Task SameBrowserConnectionsRouteOperationsToTheCapturedConnectionOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var firstChrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var secondChrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(firstChrome.ConnectAsync(5000), secondChrome.ConnectAsync(5000));
        await SendAsync(firstChrome, BrowserProtocol.Create("hello", "first-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await SendAsync(secondChrome, BrowserProtocol.Create("hello", "second-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        var firstStatus = await ReceiveAsync(firstChrome);
        var secondStatus = await ReceiveAsync(secondChrome);
        Assert.NotEqual(firstStatus.ConnectionId, secondStatus.ConnectionId);

        var item = new ParcelItem
        {
            Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserConnectionId = firstStatus.ConnectionId,
            BrowserWindowGroupId = "window-0", Value = "https://captured-profile.example/"
        };
        var openTask = service.OpenTabsAsync(new[] { item }, "chrome");
        var request = await ReceiveAsync(firstChrome);
        Assert.Equal(firstStatus.ConnectionId, request.ConnectionId);
        await SendAsync(firstChrome, BrowserProtocol.Create("operation_result", request.RequestId, "chrome", new
        {
            status = "OK",
            results = new[] { new { itemKey = item.Id.ToString(), status = "Opened", message = "Tab created" } }
        }, firstStatus.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));
        var result = Assert.Single(await openTask);
        Assert.Equal("Opened", result.Status);
        Assert.Equal(BrowserConnectionStatus.Connected, service.GetStatus("chrome").Status);
    }

    [Fact]
    public async Task OpenRetriesOnTheOnlySameBrowserConnectionAfterExtensionReload()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        var status = await ReceiveAsync(chrome);

        var item = new ParcelItem
        {
            Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserConnectionId = "old-extension-connection",
            BrowserWindowGroupId = "window-0", Value = "https://reload-safe-open.example/"
        };
        var openTask = service.OpenTabsAsync(new[] { item }, "chrome");
        var request = await ReceiveAsync(chrome);
        Assert.Equal("open_tabs", request.Type);
        Assert.Equal(status.ConnectionId, request.ConnectionId);
        await SendAsync(chrome, BrowserProtocol.Create("operation_result", request.RequestId, "chrome", new
        {
            status = "OK",
            results = new[] { new { itemKey = item.Id.ToString(), status = "Opened", message = "Tab created" } }
        }, status.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));

        var result = Assert.Single(await openTask);
        Assert.Equal("Opened", result.Status);
    }

    [Fact]
    public async Task BrowserErrorResponseProducesPerItemFailureInsteadOfEmptySuccess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        var status = await ReceiveAsync(chrome);
        var item = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", Value = "https://example.test/failure" };

        var operation = service.OpenTabsAsync(new[] { item }, "chrome");
        var request = await ReceiveAsync(chrome);
        Assert.Equal("open_tabs", request.Type);
        await SendAsync(chrome, BrowserProtocol.Create("error", request.RequestId, "chrome", new { code = "connection_error", message = "The extension rejected this operation." }, status.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));

        var result = Assert.Single(await operation);
        Assert.Equal(item.Id.ToString(), result.ItemKey);
        Assert.Equal("ConnectionError", result.Status);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAndCloseOperationsCorrelatePerItemResultsThroughThePipe()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        var status = await ReceiveAsync(chrome);

        var openItems = new[]
        {
            new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "window-0", Value = "https://open-one.example/" },
            new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "window-0", Value = "https://open-two.example/" }
        };
        var openTask = service.OpenTabsAsync(openItems, "chrome");
        var openRequest = await ReceiveAsync(chrome);
        Assert.Equal("open_tabs", openRequest.Type);
        await SendAsync(chrome, BrowserProtocol.Create("operation_result", openRequest.RequestId, "chrome", new
        {
            status = "OK",
            results = new[]
            {
                new { itemKey = openItems[0].Id.ToString(), status = "Opened", message = "Tab created" },
                new { itemKey = openItems[1].Id.ToString(), status = "AlreadyOpen", message = "Already present" }
            }
        }, status.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));
        var openResults = await openTask;
        Assert.Equal(new[] { "Opened", "AlreadyOpen" }, openResults.Select(result => result.Status));
        Assert.Equal(openItems.Select(item => item.Id.ToString()), openResults.Select(result => result.ItemKey));

        var missingItem = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", Value = "https://missing-result.example/" };
        var incompleteTask = service.OpenTabsAsync(new[] { missingItem }, "chrome");
        var incompleteRequest = await ReceiveAsync(chrome);
        await SendAsync(chrome, BrowserProtocol.Create("operation_result", incompleteRequest.RequestId, "chrome", new { status = "OK", results = Array.Empty<object>() }, status.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));
        var incompleteResult = Assert.Single(await incompleteTask);
        Assert.Equal("InvalidMessage", incompleteResult.Status);

        var oversizedItem = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", Value = "https://oversized-result.example/" };
        var oversizedTask = service.OpenTabsAsync(new[] { oversizedItem }, "chrome");
        var oversizedRequest = await ReceiveAsync(chrome);
        var oversizedResults = Enumerable.Range(0, BrowserProtocol.MaxTabCount + 1).Select(index => new { itemKey = $"unexpected-{index}", status = "Opened", message = "Tab created" }).ToArray();
        await SendAsync(chrome, BrowserProtocol.Create("operation_result", oversizedRequest.RequestId, "chrome", new { status = "OK", results = oversizedResults }, status.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));
        var oversizedResult = Assert.Single(await oversizedTask);
        Assert.Equal("InvalidMessage", oversizedResult.Status);

        var closeItem = new ParcelItem
        {
            Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", Value = "https://close.example/",
            BrowserConnectionId = status.ConnectionId, BrowserSessionTabId = "42", BrowserSessionWindowId = "7"
        };
        var closeTask = service.CloseTabsAsync(new[] { closeItem }, "chrome");
        var closeRequest = await ReceiveAsync(chrome);
        Assert.Equal("close_tabs", closeRequest.Type);
        await SendAsync(chrome, BrowserProtocol.Create("operation_result", closeRequest.RequestId, "chrome", new
        {
            status = "OK",
            results = new[] { new { itemKey = closeItem.Id.ToString(), status = "Closed", message = "Close request accepted" } }
        }, status.ConnectionId, BrowserIntegrationService.ExpectedExtensionVersion));
        var closeResults = await closeTask;
        var closeResult = Assert.Single(closeResults);
        Assert.Equal(closeItem.Id.ToString(), closeResult.ItemKey);
        Assert.Equal("Closed", closeResult.Status);
    }

    [Fact]
    public async Task ExtensionVersionMismatchIsReportedBeforeOperationsAreAccepted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: "0.9.0"));
        var response = await ReceiveAsync(chrome);

        Assert.Equal("connection_status", response.Type);
        Assert.Equal("VERSION_MISMATCH", response.Payload.GetProperty("status").GetString());
        Assert.Equal(BrowserConnectionStatus.VersionMismatch, service.GetStatus("chrome").Status);
        Assert.Null(service.GetStatus("chrome").LastConnectedUtc);
    }

    [Fact]
    public async Task SnapshotRequestsHonorCancellationWithoutBlockingThePipe()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await ReceiveAsync(chrome);

        using var cancellation = new CancellationTokenSource();
        var listTask = service.ListTabsAsync("chrome", cancellation.Token);
        var request = await ReceiveAsync(chrome);
        Assert.Equal("request_snapshot", request.Type);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listTask);
    }

    [Fact]
    public async Task BrowserOperationTimeoutReturnsPerItemFailureAndReleasesTheSession()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName, TimeSpan.FromMilliseconds(100));
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await ReceiveAsync(chrome);

        var item = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", Value = "https://timeout.example/" };
        var operation = service.OpenTabsAsync(new[] { item }, "chrome");
        var request = await ReceiveAsync(chrome);
        Assert.Equal("open_tabs", request.Type);

        var result = Assert.Single(await operation);
        Assert.Equal(item.Id.ToString(), result.ItemKey);
        Assert.Equal("ConnectionError", result.Status);
        Assert.Equal(BrowserConnectionStatus.ConnectionError, service.GetStatus("chrome").Status);
    }

    [Fact]
    public async Task BrowserDisconnectProducesPerItemConnectionLostResults()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"WorkParcel.Browser.Test.{Guid.NewGuid():N}";
        using var service = new BrowserIntegrationService(pipeName);
        service.Start();
        await using var chrome = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await chrome.ConnectAsync(5000);
        await SendAsync(chrome, BrowserProtocol.Create("hello", "chrome-hello", "chrome", new { browser = "chrome" }, extensionVersion: BrowserIntegrationService.ExpectedExtensionVersion));
        await ReceiveAsync(chrome);

        var item = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", Value = "https://disconnect.example/" };
        var operation = service.OpenTabsAsync(new[] { item }, "chrome");
        var request = await ReceiveAsync(chrome);
        Assert.Equal("open_tabs", request.Type);
        await chrome.DisposeAsync();

        var result = Assert.Single(await operation);
        Assert.Equal(item.Id.ToString(), result.ItemKey);
        Assert.Equal("ConnectionLost", result.Status);
    }

    [Fact]
    public void BrowserExecutableResolutionReportsMissingExecutable()
    {
        using var temp = new TestDirectory();
        var candidate = Path.Combine(temp.Path, "chrome.exe");

        Assert.Null(BrowserInstallationService.FindExecutable("chrome", new[] { candidate }));
    }

    [Fact]
    public void MissingInstalledExtensionFolderIsReportedInsteadOfSilentlyIgnored()
    {
        using var temp = new TestDirectory();
        var started = false;
        var result = ExternalLaunchService.TryOpenFolder(Path.Combine(temp.Path, "browser-extension"), _ =>
        {
            started = true;
            return true;
        });

        Assert.False(result.Succeeded);
        Assert.Contains("browser extension was not included", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(started);
    }

    [Fact]
    public void FailedExternalLaunchIsReportedToTheCaller()
    {
        using var temp = new TestDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "browser-extension")).FullName;

        var result = ExternalLaunchService.TryOpenFolder(folder, _ => false);

        Assert.False(result.Succeeded);
        Assert.Contains("could not open", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuccessfulFolderLaunchUsesExplorerAndTheResolvedInstalledFolder()
    {
        using var temp = new TestDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "browser-extension")).FullName;
        ProcessStartInfo? started = null;

        var result = ExternalLaunchService.TryOpenFolder(folder, info =>
        {
            started = info;
            return true;
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(started);
        Assert.Equal("explorer.exe", started!.FileName);
        Assert.Equal(new[] { folder }, started.ArgumentList);
    }

    [Fact]
    public void SuccessfulBrowserLaunchUsesTheCorrectBrowserExecutableAndPage()
    {
        ProcessStartInfo? started = null;
        var result = ExternalLaunchService.TryOpenBrowserExtensions(
            "edge",
            _ => "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
            info =>
            {
                started = info;
                return true;
            });

        Assert.True(result.Succeeded);
        Assert.NotNull(started);
        Assert.Equal("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", started!.FileName);
        Assert.Equal(new[] { "--new-tab", "edge://extensions/" }, started.ArgumentList);
    }

    [Fact]
    public void InstalledPathResolutionDoesNotUseARepositorySibling()
    {
        using var temp = new TestDirectory();
        var install = Directory.CreateDirectory(Path.Combine(temp.Path, "install")).FullName;
        var appBase = Directory.CreateDirectory(Path.Combine(install, "App")).FullName;
        var repositoryExtension = Directory.CreateDirectory(Path.Combine(temp.Path, "repository", "browser-extension")).FullName;

        Assert.Null(InstalledResourcePathResolver.Find(appBase, "browser-extension"));
        var installedExtension = Directory.CreateDirectory(Path.Combine(install, "browser-extension")).FullName;
        Assert.Equal(installedExtension, InstalledResourcePathResolver.Find(appBase, "browser-extension"));
        Assert.NotEqual(repositoryExtension, installedExtension);
    }

    private static BrowserTabSnapshot Snapshot(string browser, string url) => new()
    {
        Browser = browser,
        WindowCount = 1,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Tabs = new[]
        {
            new BrowserTabData
            {
                SessionTabId = $"{browser}-tab",
                SessionWindowId = $"{browser}-window",
                WindowGroupKey = "window-0",
                Url = url,
                Title = browser,
                Domain = $"{browser}.example",
                Browser = browser,
                TabIndex = 0,
                CanRestore = true,
                CapturedAtUtc = DateTimeOffset.UtcNow
            }
        }
    };

    private static async Task SendAsync(Stream stream, BrowserMessage message) => await BrowserProtocol.WriteFrameAsync(stream, BrowserProtocol.Serialize(message));

    private static async Task<BrowserMessage> ReceiveAsync(Stream stream)
    {
        var body = await BrowserProtocol.ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(BrowserProtocol.TryDeserialize(body, out var message, out var error), error);
        return message!;
    }

    private static BrowserConnectionInfo Details(BrowserConnectionStatus status, bool installed = true, bool hostInstalled = false, bool extensionDetected = false) => new(
        "chrome", status, extensionDetected ? BrowserIntegrationService.ExpectedExtensionVersion : null, null, 0, null,
        BrowserInstalled: installed, HostInstalled: hostInstalled, ExtensionDetected: extensionDetected);

    private static BrowserSetupStep SetupStep(BrowserConnectionInfo info) => BrowserSetupStepLogic.For(new BrowserConnectionStateInfo(info, BrowserConnectionStateLogic.Derive(info)));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkParcelBrowserTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
