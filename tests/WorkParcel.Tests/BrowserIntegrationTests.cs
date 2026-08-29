using System.IO.Pipes;
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
    public void BrowserTabMatchingUsesSavedWindowGeometryAndRefusesUnresolvableDuplicates()
    {
        var savedOne = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "saved-a", Value = "https://same.example/", BrowserWindowLeft = 0, BrowserWindowTop = 0, BrowserWindowWidth = 1000, BrowserWindowHeight = 800, BrowserTabIndex = 0 };
        var savedTwo = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "saved-b", Value = "https://same.example/", BrowserWindowLeft = 100, BrowserWindowTop = 0, BrowserWindowWidth = 1000, BrowserWindowHeight = 800, BrowserTabIndex = 0 };
        var matched = new HashSet<Guid>(); var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var live = new BrowserTabData { Browser = "chrome", WindowGroupKey = "window-0", Url = "https://same.example/", WindowLeft = 100, WindowTop = 0, WindowWidth = 1000, WindowHeight = 800, TabIndex = 0, CanRestore = true };
        var selected = BrowserIntegrationService.FindSavedTab(new[] { savedOne, savedTwo }, live, matched, groups);
        Assert.Same(savedTwo, selected);
        matched.Add(savedTwo.Id);
        BrowserIntegrationService.ApplyLiveTab(savedTwo, live, updateSavedWindowGroup: true);
        Assert.Equal("window-0", savedTwo.BrowserWindowGroupId);

        var duplicateOne = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "a", Value = "https://ambiguous.example/" };
        var duplicateTwo = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.BrowserTab, BrowserFamily = "chrome", BrowserWindowGroupId = "b", Value = "https://ambiguous.example/" };
        Assert.Null(BrowserIntegrationService.FindSavedTab(new[] { duplicateOne, duplicateTwo }, new BrowserTabData { Browser = "chrome", WindowGroupKey = "new-window", Url = "https://ambiguous.example/", CanRestore = true }, new HashSet<Guid>(), new Dictionary<string, string>()));
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
}
