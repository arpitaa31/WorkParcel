using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using WorkParcel.Core.Browser;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public enum BrowserConnectionStatus
{
    ExtensionNotDetected,
    NativeHostNotInstalled,
    Connected,
    ConnectionLost,
    ConnectionError,
    VersionMismatch,
    Connecting,
    BrowserNotFound,
    BrowserNotRunning,
    Disabled,
    Disconnected
}

public enum BrowserConnectionState
{
    BrowserMissing,
    NotConfigured,
    ExtensionRequired,
    DesktopConnectionRequired,
    ReadyToTest,
    Connected,
    ConnectionFailed,
    Disabled
}

public enum BrowserSetupStep
{
    InstallExtension = 1,
    ConnectDesktop = 2,
    TestConnection = 3,
    Complete = 4
}

public sealed record BrowserConnectionInfo(string Browser, BrowserConnectionStatus Status, string? ExtensionVersion, DateTimeOffset? LastConnectedUtc, int ActiveConnections, string? LastError, int WindowCount = 0, int TabCount = 0, string? ConnectionId = null, bool BrowserInstalled = false, bool HostInstalled = false, int ProtocolVersion = 0, bool ProtocolCompatible = false, bool ExtensionDetected = false);

public sealed record BrowserConnectionStateInfo(BrowserConnectionInfo Details, BrowserConnectionState State);

public static class BrowserConnectionStateLogic
{
    public static BrowserConnectionState Derive(BrowserConnectionInfo info)
    {
        if (info.Status == BrowserConnectionStatus.Disabled) return BrowserConnectionState.Disabled;
        if (info.Status == BrowserConnectionStatus.Connected) return BrowserConnectionState.Connected;
        if (!info.BrowserInstalled || info.Status == BrowserConnectionStatus.BrowserNotFound) return BrowserConnectionState.BrowserMissing;
        if (info.Status == BrowserConnectionStatus.Disconnected) return BrowserConnectionState.NotConfigured;
        if (info.Status is BrowserConnectionStatus.ConnectionLost or BrowserConnectionStatus.ConnectionError or BrowserConnectionStatus.VersionMismatch) return BrowserConnectionState.ConnectionFailed;
        if (info.Status == BrowserConnectionStatus.Connecting) return BrowserConnectionState.ReadyToTest;
        // A running browser with no detected extension is at the first setup
        // step, even when the desktop registration is also absent. Do not make
        // the user infer which technical flag to fix from a generic status.
        if (info.Status == BrowserConnectionStatus.NativeHostNotInstalled && !info.ExtensionDetected) return BrowserConnectionState.ExtensionRequired;
        if (info.Status == BrowserConnectionStatus.BrowserNotRunning) return info.HostInstalled ? BrowserConnectionState.ConnectionFailed : BrowserConnectionState.NotConfigured;
        if (info.ExtensionDetected && !info.HostInstalled) return BrowserConnectionState.DesktopConnectionRequired;
        if (!info.ExtensionDetected) return BrowserConnectionState.ExtensionRequired;
        if (!info.HostInstalled) return BrowserConnectionState.DesktopConnectionRequired;
        if (info.ExtensionDetected && info.HostInstalled) return BrowserConnectionState.ReadyToTest;
        return BrowserConnectionState.NotConfigured;
    }

    public static string? PrimaryAction(BrowserConnectionState state, string browser) => state switch
    {
        BrowserConnectionState.Connected => "REFRESH TABS",
        BrowserConnectionState.BrowserMissing => null,
        BrowserConnectionState.ExtensionRequired => "INSTALL EXTENSION",
        BrowserConnectionState.DesktopConnectionRequired => "CONNECT TO WORKPARCEL",
        BrowserConnectionState.ReadyToTest => "TEST CONNECTION",
        BrowserConnectionState.ConnectionFailed => "FIX CONNECTION",
        BrowserConnectionState.Disabled => "ENABLE BROWSER TABS",
        _ => $"CONNECT {browser.ToUpperInvariant()}"
    };
}

public static class BrowserSetupStepLogic
{
    public static BrowserSetupStep For(BrowserConnectionStateInfo info)
    {
        if (info.State == BrowserConnectionState.Connected) return BrowserSetupStep.Complete;
        if (info.State is BrowserConnectionState.Disabled or BrowserConnectionState.BrowserMissing) return BrowserSetupStep.InstallExtension;
        if (info.State == BrowserConnectionState.DesktopConnectionRequired) return BrowserSetupStep.ConnectDesktop;
        if (info.State is BrowserConnectionState.ReadyToTest or BrowserConnectionState.ConnectionFailed) return BrowserSetupStep.TestConnection;
        if (info.Details.ExtensionDetected && info.Details.HostInstalled) return BrowserSetupStep.TestConnection;
        if (info.Details.ExtensionDetected) return BrowserSetupStep.ConnectDesktop;
        return BrowserSetupStep.InstallExtension;
    }
}

public sealed class BrowserIntegrationService : IDisposable
{
    private static readonly Lazy<BrowserIntegrationService> Lazy = new(() => new BrowserIntegrationService());
    public const string ExpectedExtensionVersion = "0.1.0";
    private readonly ConcurrentDictionary<string, BrowserPipeSession> _sessions = new();
    private readonly ConcurrentDictionary<string, BrowserConnectionInfo> _lastKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _manuallyDisconnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private readonly TimeSpan _requestTimeout;
    private Task? _acceptLoop;
    private int _started;
    private int _enabled = 1;
    private int _tabClosingEnabled = 1;
    private readonly string? _preferencesPath;

    public BrowserIntegrationService(string? pipeName = null, TimeSpan? requestTimeout = null, string? preferencesPath = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? BrowserProtocol.PipeName : pipeName.Trim();
        if (_pipeName.Length > 256 || _pipeName.Contains('\\') || _pipeName.Contains('/')) throw new ArgumentException("Pipe name is invalid.", nameof(pipeName));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(6);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _preferencesPath = preferencesPath ?? (string.IsNullOrWhiteSpace(pipeName) ? Path.Combine(new AppDataPaths().DataDirectory, "browser-preferences.json") : null);
        LoadPreferences();
    }

    public static BrowserIntegrationService Current => Lazy.Value;
    public event EventHandler? StateChanged;
    public event EventHandler? OpenWorkParcelRequested;
    public IReadOnlyList<BrowserConnectionInfo> Connections => _sessions.Values.Select(session => session.Info).ToList();
    public bool IsEnabled => Volatile.Read(ref _enabled) == 1;
    public bool TabClosingEnabled => Volatile.Read(ref _tabClosingEnabled) == 1;

    public BrowserConnectionStateInfo GetConnectionState(string browser)
    {
        var details = GetStatus(browser);
        return new(details, BrowserConnectionStateLogic.Derive(details));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public BrowserConnectionInfo GetStatus(string browser)
    {
        if (!IsEnabled) return new(browser, BrowserConnectionStatus.Disabled, null, null, 0, "Browser integration is disabled in Privacy settings.", 0, 0, null, BrowserInstallationService.IsInstalled(browser), HostRegistrationService.IsRegistered(browser));
        var matches = _sessions.Values.Where(session => string.Equals(session.Browser, browser, StringComparison.OrdinalIgnoreCase)).ToList();
        var installed = BrowserInstallationService.IsInstalled(browser);
        var hostInstalled = HostRegistrationService.IsRegistered(browser);
        if (_manuallyDisconnected.ContainsKey(browser))
        {
            var disconnected = _lastKnown.TryGetValue(browser, out var knownDisconnected)
                ? knownDisconnected
                : new BrowserConnectionInfo(browser, BrowserConnectionStatus.Disconnected, null, null, 0, null);
            return disconnected with { Status = BrowserConnectionStatus.Disconnected, LastError = null, ActiveConnections = 0, BrowserInstalled = installed, HostInstalled = hostInstalled };
        }
        if (matches.Count == 0)
        {
            if (installed && hostInstalled && _sessions.Values.Any(session => session.Browser == "unknown" && session.LastError is null))
                return new(browser, BrowserConnectionStatus.Connecting, null, null, 0, null, 0, 0, null, installed, hostInstalled);
            var running = installed && BrowserInstallationService.IsRunning(browser);
            var status = !installed ? BrowserConnectionStatus.BrowserNotFound : !hostInstalled ? BrowserConnectionStatus.NativeHostNotInstalled : !running ? BrowserConnectionStatus.BrowserNotRunning : BrowserConnectionStatus.ExtensionNotDetected;
            if (installed && _lastKnown.TryGetValue(browser, out var previous) && previous.Status is BrowserConnectionStatus.ConnectionLost or BrowserConnectionStatus.ConnectionError or BrowserConnectionStatus.VersionMismatch)
                return previous with { ActiveConnections = 0, BrowserInstalled = installed, HostInstalled = hostInstalled };
            if (_lastKnown.TryGetValue(browser, out var known) && known.Status == BrowserConnectionStatus.Connected && known.ExtensionDetected && !hostInstalled)
                return known with { Status = BrowserConnectionStatus.NativeHostNotInstalled, ActiveConnections = 0, BrowserInstalled = installed, HostInstalled = hostInstalled };
            return new(browser, status, null, null, 0, null, 0, 0, null, installed, hostInstalled);
        }
        var session = matches.OrderByDescending(item => item.Info.LastConnectedUtc).First();
        return session.Info with { ActiveConnections = matches.Count, BrowserInstalled = installed, HostInstalled = hostInstalled };
    }

    private void Remember(BrowserConnectionInfo info)
    {
        if (info.Browser is not ("chrome" or "edge")) return;
        if (_manuallyDisconnected.ContainsKey(info.Browser))
        {
            _lastKnown[info.Browser] = info with { Status = BrowserConnectionStatus.Disconnected, LastError = null, ActiveConnections = 0 };
            return;
        }
        _lastKnown[info.Browser] = info;
    }

    public async Task<IReadOnlyList<BrowserTabData>> ListTabsAsync(string? browser = null, CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values.Where(session => session.Browser is "chrome" or "edge" && session.Info.Status == BrowserConnectionStatus.Connected && (browser is null || string.Equals(session.Browser, browser, StringComparison.OrdinalIgnoreCase))).ToList();
        var result = new List<BrowserTabData>();
        foreach (var session in sessions)
        {
            try
            {
                var snapshot = await session.RequestSnapshotAsync(cancellationToken);
                if (snapshot is not null) result.AddRange(snapshot.Tabs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                session.SetError("Connection unavailable", BrowserConnectionStatus.ConnectionError);
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<BrowserOperationResult>> OpenTabsAsync(IEnumerable<ParcelItem> items, string? browser, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        var allRequested = items.Where(item => item.ItemType == ParcelItemType.BrowserTab).ToList();
        var overLimit = allRequested.Skip(BrowserProtocol.MaxTabCount).Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "TooManyTabs", Message = $"Only the first {BrowserProtocol.MaxTabCount} tabs can be opened in one request." }).ToList();
        var allBrowserItems = allRequested.Take(BrowserProtocol.MaxTabCount).ToList();
        var requestedBrowser = browser ?? "the requested browser";
        var mismatched = browser is null ? Array.Empty<ParcelItem>() : allBrowserItems.Where(item => !string.IsNullOrWhiteSpace(item.BrowserFamily) && !string.Equals(item.BrowserFamily, browser, StringComparison.OrdinalIgnoreCase)).ToArray();
        var browserItems = allBrowserItems.Except(mismatched).ToList();
        var results = overLimit.Concat(mismatched.Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "BrowserMismatch", Message = $"This saved tab belongs to {item.BrowserFamily?.ToUpperInvariant()} and was not sent to {requestedBrowser.ToUpperInvariant()}." })).ToList();
        foreach (var batch in browserItems.GroupBy(item => (Browser: browser ?? item.BrowserFamily, ConnectionId: item.BrowserConnectionId ?? string.Empty)))
        {
            var session = PickSession(batch.Key.Browser, string.IsNullOrWhiteSpace(batch.Key.ConnectionId) ? null : batch.Key.ConnectionId);
            // Opening is safe to retry after an extension reload when there is
            // exactly one connected session for this browser. Never guess when
            // multiple profiles/connections are available; close operations
            // remain strict and never use this fallback.
            if (session is null && !string.IsNullOrWhiteSpace(batch.Key.ConnectionId))
            {
                var sameBrowser = _sessions.Values.Where(candidate => candidate.Info.Status == BrowserConnectionStatus.Connected && string.Equals(candidate.Browser, batch.Key.Browser, StringComparison.OrdinalIgnoreCase)).ToList();
                if (sameBrowser.Count == 1) session = sameBrowser[0];
            }
            var batchItems = batch.ToList();
            if (session is null)
            {
                results.AddRange(batchItems.Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "ConnectionLost", Message = "The browser extension is not connected." }));
                continue;
            }
            var payload = new { tabs = batchItems.Select(item => ToOpenPayload(item, allowDuplicates)).ToList() };
            try { results.AddRange(await session.OpenTabsAsync(payload, batchItems.Select(item => item.Id.ToString()).ToList(), cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception); session.SetError("Connection unavailable", session.LastError is null ? BrowserConnectionStatus.ConnectionError : session.FailureStatus);
                results.AddRange(batchItems.Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = session.FailureResultStatus, Message = session.FailureResultMessage }));
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<BrowserOperationResult>> CloseTabsAsync(IEnumerable<ParcelItem> items, string? browser, CancellationToken cancellationToken = default)
    {
        var allRequested = items.Where(item => item.ItemType == ParcelItemType.BrowserTab).ToList();
        var overLimit = allRequested.Skip(BrowserProtocol.MaxTabCount).Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "TooManyTabs", Message = $"Only the first {BrowserProtocol.MaxTabCount} tabs can be closed in one request." }).ToList();
        var browserItems = allRequested.Take(BrowserProtocol.MaxTabCount).ToList();
        if (!TabClosingEnabled) return overLimit.Concat(browserItems.Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "Disabled", Message = "Tab closing is disabled in Privacy settings." })).ToList();
        var stale = browserItems.Where(item => string.IsNullOrWhiteSpace(item.BrowserConnectionId) || string.IsNullOrWhiteSpace(item.BrowserSessionTabId) || string.IsNullOrWhiteSpace(item.BrowserSessionWindowId)).Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "Stale", Message = "Refresh the current browser tabs before closing this saved selection." }).ToList();
        var current = browserItems.Except(browserItems.Where(item => string.IsNullOrWhiteSpace(item.BrowserConnectionId) || string.IsNullOrWhiteSpace(item.BrowserSessionTabId) || string.IsNullOrWhiteSpace(item.BrowserSessionWindowId))).ToList();
        if (current.Count == 0) return overLimit.Concat(stale).ToList();
        var results = new List<BrowserOperationResult>();
        foreach (var batch in current.GroupBy(item => (Browser: browser ?? item.BrowserFamily, ConnectionId: item.BrowserConnectionId ?? string.Empty)))
        {
            var session = PickSession(batch.Key.Browser, batch.Key.ConnectionId);
            var batchItems = batch.ToList();
            if (session is null)
            {
                results.AddRange(batchItems.Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = "ConnectionLost", Message = "The browser extension is not connected for this saved tab." }));
                continue;
            }
            var payload = new { tabs = batchItems.Select(item => new { id = item.Id.ToString(), browser = item.BrowserFamily, connectionId = item.BrowserConnectionId, expectedUrl = item.Value, sessionTabId = item.BrowserSessionTabId, sessionWindowId = item.BrowserSessionWindowId }).ToList() };
            try { results.AddRange(await session.CloseTabsAsync(payload, batchItems.Select(item => item.Id.ToString()).ToList(), cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception); session.SetError("Connection unavailable", session.LastError is null ? BrowserConnectionStatus.ConnectionError : session.FailureStatus);
                results.AddRange(batchItems.Select(item => new BrowserOperationResult { ItemKey = item.Id.ToString(), Status = session.FailureResultStatus, Message = session.FailureResultMessage }));
            }
        }
        return overLimit.Concat(stale).Concat(results).ToList();
    }

    public void Disconnect(string browser)
    {
        _manuallyDisconnected[browser] = 0;
        foreach (var session in _sessions.Values.Where(session => string.Equals(session.Browser, browser, StringComparison.OrdinalIgnoreCase))) session.Disconnect();
        _lastKnown[browser] = new BrowserConnectionInfo(browser, BrowserConnectionStatus.Disconnected, null, null, 0, null, BrowserInstalled: BrowserInstallationService.IsInstalled(browser), HostInstalled: HostRegistrationService.IsRegistered(browser));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveRegistration(string browser)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var removed = HostRegistrationService.Remove(browser);
        // A failed registry delete must not silently disconnect an otherwise
        // working browser session. The caller can retry the removal while the
        // browser connection and its saved state remain untouched.
        if (removed) Disconnect(browser);
        return removed;
    }

    public void SetEnabled(bool enabled)
    {
        Interlocked.Exchange(ref _enabled, enabled ? 1 : 0);
        if (!enabled) foreach (var session in _sessions.Values) session.Disconnect();
        SavePreferences();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetTabClosingEnabled(bool enabled)
    {
        Interlocked.Exchange(ref _tabClosingEnabled, enabled ? 1 : 0);
        SavePreferences();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static ParcelItem FromTab(Guid parcelId, BrowserTabData tab, int sortOrder, string? connectionId)
    {
        var now = DateTime.Now; var groupKey = string.IsNullOrWhiteSpace(tab.WindowGroupKey) ? "window-0" : tab.WindowGroupKey; var identity = BrowserTabRules.Identity(tab.Browser, groupKey, tab.Url);
        return new ParcelItem
        {
            ParcelId = parcelId, ItemType = ParcelItemType.BrowserTab, DisplayName = string.IsNullOrWhiteSpace(tab.Title) ? tab.Domain : tab.Title, Value = tab.Url, NormalizedIdentity = identity,
            SecondaryDetail = tab.Domain, BrowserDomain = tab.Domain, CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now, SortOrder = sortOrder, LaunchEnabled = tab.CanRestore, BrowserFamily = tab.Browser,
            BrowserWindowGroupId = groupKey, BrowserTabIndex = tab.TabIndex, BrowserPinned = tab.Pinned, BrowserActive = tab.Active, BrowserTabGroupId = BrowserTabRules.StableGroupIdentity(tab.Browser, groupKey, tab.GroupTitle, tab.GroupColor),
            BrowserTabGroupTitle = tab.GroupTitle, BrowserTabGroupColor = tab.GroupColor, BrowserFaviconUrl = BrowserTabRules.SafeFaviconUrl(tab.FaviconUrl), BrowserCapturedAt = tab.CapturedAtUtc.LocalDateTime,
            BrowserSessionTabId = tab.SessionTabId, BrowserSessionWindowId = tab.SessionWindowId, BrowserConnectionId = connectionId ?? tab.ConnectionId, CloseSupported = tab.CanRestore, IsInaccessible = !tab.CanRestore,
            BrowserWindowLeft = tab.WindowLeft, BrowserWindowTop = tab.WindowTop, BrowserWindowWidth = tab.WindowWidth, BrowserWindowHeight = tab.WindowHeight,
            BrowserWindowState = tab.WindowState, BrowserWindowFocused = tab.WindowFocused
        };
    }

    /// <summary>
    /// Associates a live tab with a saved tab without treating a temporary
    /// browser window id as a permanent identity. The caller supplies the
    /// already matched item ids and receives a stable-window assignment map so
    /// identical URLs in two browser windows remain separate when another tab
    /// in each window provides the disambiguating signal.
    /// </summary>
    public static ParcelItem? FindSavedTab(
        IEnumerable<ParcelItem> savedItems,
        BrowserTabData liveTab,
        ISet<Guid> matchedItemIds,
        IDictionary<string, string>? liveToSavedGroups = null)
    {
        var candidates = savedItems
            .Where(item => item.ItemType == ParcelItemType.BrowserTab && !matchedItemIds.Contains(item.Id))
            .Where(item => string.Equals(item.BrowserFamily, liveTab.Browser, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var urlCandidates = candidates.Where(item => SameRestorableUrl(item.Value, liveTab.Url)).ToList();
        var liveGroupKey = BuildLiveGroupKey(liveTab);
        if (liveToSavedGroups is not null &&
            (liveToSavedGroups.TryGetValue(liveGroupKey, out var assignedGroup) ||
             liveToSavedGroups.TryGetValue(liveTab.WindowGroupKey, out assignedGroup)))
            urlCandidates = urlCandidates.Where(item => string.Equals(item.BrowserWindowGroupId, assignedGroup, StringComparison.OrdinalIgnoreCase)).ToList();
        if (urlCandidates.Count == 0) return null;
        var ranked = urlCandidates.Select(item => (Item: item, Score: TabMatchScore(item, liveTab)))
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Item.SortOrder).ToList();
        var best = ranked[0];
        var margin = best.Score - ranked.Skip(1).Select(candidate => candidate.Score).FirstOrDefault();
        if (ranked.Count > 1 && margin < 8) return null;
        if (liveToSavedGroups is not null)
            liveToSavedGroups[liveGroupKey] = best.Item.BrowserWindowGroupId ?? liveTab.WindowGroupKey;
        return best.Item;
    }

    private static string BuildLiveGroupKey(BrowserTabData tab) =>
        string.Join("\u001F", tab.Browser, tab.ConnectionId ?? string.Empty, tab.WindowGroupKey);

    public static void ApplyLiveTab(ParcelItem item, BrowserTabData tab, bool updateSavedWindowGroup = false)
    {
        item.UpdatedAt = DateTime.Now;
        item.LastVerifiedAt = DateTime.Now;
        item.BrowserCapturedAt = tab.CapturedAtUtc.LocalDateTime;
        item.BrowserSessionTabId = tab.SessionTabId;
        item.BrowserSessionWindowId = tab.SessionWindowId;
        item.BrowserConnectionId = tab.ConnectionId;
        item.BrowserTabIndex = tab.TabIndex;
        item.BrowserPinned = tab.Pinned;
        item.BrowserActive = tab.Active;
        item.BrowserTabGroupId = BrowserTabRules.StableGroupIdentity(tab.Browser, updateSavedWindowGroup ? tab.WindowGroupKey : item.BrowserWindowGroupId ?? tab.WindowGroupKey, tab.GroupTitle, tab.GroupColor);
        item.BrowserTabGroupTitle = tab.GroupTitle;
        item.BrowserTabGroupColor = tab.GroupColor;
        item.BrowserFaviconUrl = BrowserTabRules.SafeFaviconUrl(tab.FaviconUrl);
        item.BrowserWindowLeft = tab.WindowLeft;
        item.BrowserWindowTop = tab.WindowTop;
        item.BrowserWindowWidth = tab.WindowWidth;
        item.BrowserWindowHeight = tab.WindowHeight;
        item.BrowserWindowState = tab.WindowState;
        item.BrowserWindowFocused = tab.WindowFocused;
        item.BrowserWindowDpiX = item.BrowserWindowDpiX ?? 96;
        item.BrowserWindowDpiY = item.BrowserWindowDpiY ?? 96;
        item.LaunchEnabled = tab.CanRestore;
        item.IsInaccessible = !tab.CanRestore;
        item.CloseSupported = tab.CanRestore;
        if (updateSavedWindowGroup)
        {
            item.BrowserWindowGroupId = string.IsNullOrWhiteSpace(tab.WindowGroupKey) ? "window-0" : tab.WindowGroupKey;
            item.NormalizedIdentity = BrowserTabRules.Identity(tab.Browser, item.BrowserWindowGroupId, item.Value);
        }
    }

    private static bool SameRestorableUrl(string left, string right) =>
        BrowserTabRules.TryGetRestorableUri(left, out var leftUri) &&
        BrowserTabRules.TryGetRestorableUri(right, out var rightUri) &&
        string.Equals(leftUri.AbsoluteUri, rightUri.AbsoluteUri, StringComparison.Ordinal);

    private static int TabMatchScore(ParcelItem saved, BrowserTabData live)
    {
        var score = 100;
        if (string.Equals(saved.BrowserWindowGroupId, live.WindowGroupKey, StringComparison.OrdinalIgnoreCase)) score += 18;
        if (saved.BrowserTabIndex == live.TabIndex) score += 4;
        if (saved.BrowserPinned == live.Pinned) score += 2;
        if (saved.BrowserActive == live.Active) score += 1;
        score += GeometrySignal(saved.BrowserWindowLeft, live.WindowLeft);
        score += GeometrySignal(saved.BrowserWindowTop, live.WindowTop);
        score += GeometrySignal(saved.BrowserWindowWidth, live.WindowWidth);
        score += GeometrySignal(saved.BrowserWindowHeight, live.WindowHeight);
        return score;
    }

    private static int GeometrySignal(int? saved, int? live) => saved.HasValue && live.HasValue ? Math.Abs(saved.Value - live.Value) <= 8 ? 10 : 0 : 0;

    private static object ToOpenPayload(ParcelItem item, bool allowDuplicates) => new { id = item.Id.ToString(), url = item.Value, browserWindowGroupId = item.BrowserWindowGroupId, tabIndex = item.BrowserTabIndex, pinned = item.BrowserPinned, active = item.BrowserActive, browserTabGroupId = item.BrowserTabGroupId, browserTabGroupTitle = item.BrowserTabGroupTitle, browserTabGroupColor = item.BrowserTabGroupColor, windowLeft = item.BrowserWindowLeft, windowTop = item.BrowserWindowTop, windowWidth = item.BrowserWindowWidth, windowHeight = item.BrowserWindowHeight, windowState = item.BrowserWindowState, windowFocused = item.BrowserWindowFocused, allowDuplicate = allowDuplicates };
    private BrowserPipeSession? PickSession(string? browser, string? connectionId = null) => _sessions.Values.Where(session => session.Info.Status == BrowserConnectionStatus.Connected && (browser is null || string.Equals(session.Browser, browser, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrWhiteSpace(connectionId) || string.Equals(session.ConnectionKey, connectionId, StringComparison.Ordinal))).OrderByDescending(session => session.Info.LastConnectedUtc).FirstOrDefault();

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                if (!IsEnabled) { await Task.Delay(250, _shutdown.Token); continue; }
                var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                if (!IsEnabled) { await pipe.DisposeAsync(); continue; }
                _ = HandleAsync(pipe);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Task.Delay(500, _shutdown.Token); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using var owned = pipe; var session = new BrowserPipeSession(pipe, this, _requestTimeout); var key = Guid.NewGuid().ToString("N"); session.ConnectionKey = key; _sessions[key] = session; StateChanged?.Invoke(this, EventArgs.Empty);
        try { await session.RunAsync(_shutdown.Token); }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ObjectDisposedException)
        {
            // A request timeout already classified the session as a connection
            // error; do not downgrade that diagnostic merely because disposing
            // the pipe causes the reader to observe an IOException afterwards.
            session.SetError(exception.Message, session.FailureStatus == BrowserConnectionStatus.ConnectionError ? BrowserConnectionStatus.ConnectionError : BrowserConnectionStatus.ConnectionLost);
        }
        catch (InvalidDataException exception) { session.SetError(exception.Message, BrowserConnectionStatus.ConnectionError); }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); session.SetError("Connection failed", BrowserConnectionStatus.ConnectionError); }
        finally
        {
            _sessions.TryRemove(key, out _);
            if (session.Browser is "chrome" or "edge") Remember(session.Info with { ActiveConnections = 0 });
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var session in _sessions.Values) session.Disconnect();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception exception) when (exception is AggregateException or OperationCanceledException or ObjectDisposedException) { }
    }

    private sealed class BrowserPipeSession
    {
        private readonly Stream _pipe; private readonly BrowserIntegrationService _owner; private readonly SemaphoreSlim _writeGate = new(1, 1); private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserMessage>> _pending = new();
        private readonly TimeSpan _requestTimeout;
        public BrowserPipeSession(Stream pipe, BrowserIntegrationService owner, TimeSpan requestTimeout) { _pipe = pipe; _owner = owner; _requestTimeout = requestTimeout; }
        public string ConnectionKey { get; set; } = string.Empty; public string Browser { get; private set; } = "unknown"; public string? ExtensionVersion { get; private set; } public DateTimeOffset? LastConnected { get; private set; } public string? LastError { get; private set; } public BrowserTabSnapshot? LatestSnapshot { get; private set; } public int ProtocolVersion { get; private set; } public bool ProtocolCompatible { get; private set; } public bool VersionMismatchDetected { get; private set; } public BrowserConnectionStatus FailureStatus { get; private set; } = BrowserConnectionStatus.ConnectionLost;
        public string FailureResultStatus => FailureStatus == BrowserConnectionStatus.ConnectionError ? "ConnectionError" : "ConnectionLost";
        public string FailureResultMessage => FailureStatus == BrowserConnectionStatus.ConnectionError ? "The browser connection timed out or returned an error. Reconnect and try again." : "The browser extension did not respond. Try again after reconnecting.";
        public BrowserConnectionInfo Info => new(Browser, Status, ExtensionVersion, LastConnected, 1, LastError, LatestSnapshot?.WindowCount ?? 0, LatestSnapshot?.Tabs.Count ?? 0, ConnectionKey, false, false, ProtocolVersion, ProtocolCompatible, Browser is "chrome" or "edge");
        private BrowserConnectionStatus Status => VersionMismatchDetected ? BrowserConnectionStatus.VersionMismatch : LastError is not null ? FailureStatus : Browser == "unknown" ? BrowserConnectionStatus.Connecting : BrowserConnectionStatus.Connected;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var body = await BrowserProtocol.ReadFrameAsync(_pipe, cancellationToken); if (!BrowserProtocol.TryDeserializeForRelay(body, out var message, out var error)) { var errorBrowser = message?.Browser is "chrome" or "edge" ? message.Browser : Browser; var errorRequest = string.IsNullOrWhiteSpace(message?.RequestId) ? Guid.NewGuid().ToString("N") : message.RequestId; await SendAsync(BrowserProtocol.Create("error", errorRequest, errorBrowser, new { code = "invalid_message", message = error }, ConnectionKey)); continue; } await ReceiveAsync(message!, cancellationToken);
            }
        }

        public async Task<BrowserTabSnapshot?> RequestSnapshotAsync(CancellationToken cancellationToken)
        {
            var message = await RequestAsync("request_snapshot", new { }, cancellationToken); if (message is null) return null;
            if (message.Type == "tab_snapshot") return ReadSnapshot(message.Payload);
            return LatestSnapshot;
        }
        public async Task<IReadOnlyList<BrowserOperationResult>> OpenTabsAsync(object payload, IReadOnlyList<string> itemKeys, CancellationToken cancellationToken) => ReadResults(await RequestAsync("open_tabs", payload, cancellationToken), itemKeys, FailureResultStatus, FailureResultMessage);
        public async Task<IReadOnlyList<BrowserOperationResult>> CloseTabsAsync(object payload, IReadOnlyList<string> itemKeys, CancellationToken cancellationToken) => ReadResults(await RequestAsync("close_tabs", payload, cancellationToken), itemKeys, FailureResultStatus, FailureResultMessage);
        private static IReadOnlyList<BrowserOperationResult> ReadResults(BrowserMessage? message, IReadOnlyList<string> itemKeys, string failureStatus, string failureMessage)
        {
            static IReadOnlyList<BrowserOperationResult> InvalidResults(IReadOnlyList<string> keys, string detail) => keys.Select(itemKey => new BrowserOperationResult { ItemKey = itemKey, Status = "InvalidMessage", Message = detail }).ToList();
            if (message is null)
                return itemKeys.Select(itemKey => new BrowserOperationResult { ItemKey = itemKey, Status = failureStatus, Message = failureMessage }).ToList();
            if (message.Type == "error")
            {
                var error = JsonSerializer.Deserialize<BrowserErrorEnvelope>(message.Payload.GetRawText(), BrowserProtocol.JsonOptions);
                var code = error?.Code ?? "connection_error";
                var status = code switch { "version_mismatch" => "VersionMismatch", "invalid_message" => "InvalidMessage", "connection_error" => "ConnectionError", _ => "ConnectionLost" };
                var detail = string.IsNullOrWhiteSpace(error?.Message) ? "The browser extension rejected the operation. Try again after reconnecting." : error.Message;
                return itemKeys.Select(itemKey => new BrowserOperationResult { ItemKey = itemKey, Status = status, Message = detail }).ToList();
            }
            try
            {
                var envelope = JsonSerializer.Deserialize<BrowserOperationEnvelope>(message.Payload.GetRawText(), BrowserProtocol.JsonOptions);
                if (envelope?.Results is null) return InvalidResults(itemKeys, "The browser returned no per-item operation results.");
                if (envelope.Results.Count > BrowserProtocol.MaxTabCount) return InvalidResults(itemKeys, $"The browser returned more than {BrowserProtocol.MaxTabCount} operation results.");
                var byKey = envelope.Results.Where(result => result is not null && !string.IsNullOrWhiteSpace(result.ItemKey)).GroupBy(result => result.ItemKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                return itemKeys.Select(itemKey => byKey.TryGetValue(itemKey, out var result) ? result : new BrowserOperationResult { ItemKey = itemKey, Status = "InvalidMessage", Message = "The browser omitted a result for this item." }).ToList();
            }
            catch (JsonException) { return InvalidResults(itemKeys, "The browser returned an invalid operation response."); }
        }

        private async Task<BrowserMessage?> RequestAsync(string type, object payload, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N"); var completion = new TaskCompletionSource<BrowserMessage>(TaskCreationOptions.RunContinuationsAsynchronously); _pending[id] = completion;
            try
            {
                await SendAsync(BrowserProtocol.Create(type, id, Browser, payload, ConnectionKey));
                return await completion.Task.WaitAsync(_requestTimeout, cancellationToken);
            }
            catch (TimeoutException) { SetError("Connection timeout", BrowserConnectionStatus.ConnectionError); return null; }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task ReceiveAsync(BrowserMessage message, CancellationToken cancellationToken)
        {
            if (message.Type != "hello" && message.Version != BrowserProtocol.CurrentVersion)
            {
                await SendAsync(BrowserProtocol.Create("error", message.RequestId, Browser, new { code = "version_mismatch", message = $"Protocol version {message.Version} is not supported; expected {BrowserProtocol.CurrentVersion}." }, ConnectionKey, ExpectedExtensionVersion));
                return;
            }
            if (message.Type != "hello")
            {
                if (Browser == "unknown") { await SendAsync(BrowserProtocol.Create("error", message.RequestId, Browser, new { code = "handshake_required", message = "The browser must complete its hello handshake first." }, ConnectionKey)); return; }
                if (!string.Equals(message.Browser, Browser, StringComparison.OrdinalIgnoreCase)) { await SendAsync(BrowserProtocol.Create("error", message.RequestId, Browser, new { code = "browser_mismatch", message = "The browser identity does not match this connection." }, ConnectionKey)); return; }
                if (!string.Equals(message.ConnectionId, ConnectionKey, StringComparison.Ordinal)) { await SendAsync(BrowserProtocol.Create("error", message.RequestId, Browser, new { code = "connection_mismatch", message = "The browser connection identity is stale." }, ConnectionKey)); return; }
            }
            if (VersionMismatchDetected && message.Type != "hello") { await SendAsync(BrowserProtocol.Create("error", message.RequestId, Browser, new { code = "version_mismatch", message = LastError ?? "The extension version is incompatible." }, ConnectionKey, ExpectedExtensionVersion)); return; }
            if (message.Type == "hello")
            {
                Browser = message.Browser is "chrome" or "edge" ? message.Browser : "unknown";
                if (Browser is "chrome" or "edge") _owner.ClearManualDisconnect(Browser);
                ExtensionVersion = message.ExtensionVersion;
                ProtocolVersion = message.Version;
                ProtocolCompatible = message.Version == BrowserProtocol.CurrentVersion;
                LastError = null;
                FailureStatus = BrowserConnectionStatus.ConnectionLost;
                VersionMismatchDetected = message.Version != BrowserProtocol.CurrentVersion || !string.Equals(ExtensionVersion, ExpectedExtensionVersion, StringComparison.Ordinal);
                if (VersionMismatchDetected)
                {
                    LastError = message.Version != BrowserProtocol.CurrentVersion
                        ? $"Protocol version {message.Version} is not compatible with {BrowserProtocol.CurrentVersion}."
                        : $"Extension version {ExtensionVersion} is not compatible with {ExpectedExtensionVersion}.";
                    await SendAsync(BrowserProtocol.Create("connection_status", message.RequestId, Browser, new { status = "VERSION_MISMATCH", connectionId = ConnectionKey }, ConnectionKey, ExpectedExtensionVersion));
                    _owner.Remember(Info);
                    _owner.NotifyStateChanged();
                    return;
                }
                // Only a fully compatible handshake is a successful connection.
                // Keep the timestamp semantic honest for the Settings page.
                LastConnected = DateTimeOffset.UtcNow;
                await SendAsync(BrowserProtocol.Create("connection_status", message.RequestId, Browser, new { status = "CONNECTED", connectionId = ConnectionKey }, ConnectionKey, ExpectedExtensionVersion));
                _owner.Remember(Info);
                _owner.NotifyStateChanged();
                return;
            }
            if (message.Type == "tab_snapshot") { LatestSnapshot = ReadSnapshot(message.Payload); if (_pending.TryRemove(message.RequestId, out var snapshotTask)) snapshotTask.TrySetResult(message); _owner.NotifyStateChanged(); return; }
            if ((message.Type is "operation_result" or "list_tabs" or "error") && _pending.TryRemove(message.RequestId, out var task)) { task.TrySetResult(message); return; }
            if (message.Type is "list_tabs" or "list_windows" or "refresh_tabs") { var snapshot = await RequestSnapshotAsync(cancellationToken); await SendAsync(BrowserProtocol.Create("list_tabs", message.RequestId, Browser, snapshot ?? new BrowserTabSnapshot { Browser = Browser }, ConnectionKey)); return; }
            if (message.Type == "open_workparcel") { _owner.OpenWorkParcelRequested?.Invoke(_owner, EventArgs.Empty); await SendAsync(BrowserProtocol.Create("operation_result", message.RequestId, Browser, new { status = "OK", message = "WorkParcel window activation requested." }, ConnectionKey)); return; }
            if (message.Type == "ping") await SendAsync(BrowserProtocol.Create("pong", message.RequestId, Browser, new { status = "OK" }, ConnectionKey));
        }

        private async Task SendAsync(BrowserMessage message)
        {
            var bytes = BrowserProtocol.Serialize(message); await _writeGate.WaitAsync(); try { await BrowserProtocol.WriteFrameAsync(_pipe, bytes); } finally { _writeGate.Release(); }
        }
        public void SetError(string error, BrowserConnectionStatus status = BrowserConnectionStatus.ConnectionLost) { LastError = error; FailureStatus = status; _owner.NotifyStateChanged(); try { _pipe.Dispose(); } catch { } var failure = new IOException(error); foreach (var task in _pending.Values) task.TrySetException(failure); }
        public void Disconnect() { try { _pipe.Dispose(); } catch { } SetError("Disconnected by user", BrowserConnectionStatus.ConnectionLost); }

        private BrowserTabSnapshot? ReadSnapshot(JsonElement payload)
        {
            var snapshot = JsonSerializer.Deserialize<BrowserTabSnapshot>(payload.GetRawText(), BrowserProtocol.JsonOptions); if (snapshot is not null && snapshot.Tabs.Count > BrowserProtocol.MaxTabCount) throw new InvalidDataException("Browser snapshot exceeds the tab limit."); return snapshot is null ? null : snapshot with { Tabs = snapshot.Tabs.Select(tab => tab with { ConnectionId = ConnectionKey }).ToList() };
        }
    }

    private sealed record BrowserOperationEnvelope(string Status, IReadOnlyList<BrowserOperationResult> Results);
    private sealed record BrowserErrorEnvelope(string? Code, string? Message);

    private sealed record BrowserPreferences(bool Enabled, bool TabClosingEnabled);

    private void LoadPreferences()
    {
        if (_preferencesPath is null || !File.Exists(_preferencesPath)) return;
        try
        {
            var preferences = JsonSerializer.Deserialize<BrowserPreferences>(File.ReadAllText(_preferencesPath));
            if (preferences is not null)
            {
                Interlocked.Exchange(ref _enabled, preferences.Enabled ? 1 : 0);
                Interlocked.Exchange(ref _tabClosingEnabled, preferences.TabClosingEnabled ? 1 : 0);
            }
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
    }

    private void SavePreferences()
    {
        if (_preferencesPath is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_preferencesPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_preferencesPath, JsonSerializer.Serialize(new BrowserPreferences(IsEnabled, TabClosingEnabled)));
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
    }

    private void ClearManualDisconnect(string browser) => _manuallyDisconnected.TryRemove(browser, out _);
}

public static class BrowserInstallationService
{
    public static string? FindExecutable(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = browser.Equals("edge", StringComparison.OrdinalIgnoreCase)
            ? new[] { Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe") }
            : new[] { Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe") };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsInstalled(string browser) => FindExecutable(browser) is not null;

    public static bool IsRunning(string browser)
    {
        try { var name = browser.Equals("edge", StringComparison.OrdinalIgnoreCase) ? "msedge" : "chrome"; return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }
}

public static class HostRegistrationService
{
    internal static string RegistrationPath(string browser) => browser.Equals("edge", StringComparison.OrdinalIgnoreCase)
        ? @"Software\Microsoft\Edge\NativeMessagingHosts\com.workparcel.browser"
        : @"Software\Google\Chrome\NativeMessagingHosts\com.workparcel.browser";

    public static bool IsRegistered(string browser)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var basePath = RegistrationPath(browser);
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(basePath); return key?.GetValue(string.Empty) is string path && File.Exists(path); } catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    public static bool Remove(string browser)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var basePath = RegistrationPath(browser);
        return TryRemoveRegistration(
            basePath,
            path => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false),
            path =>
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
                return key is not null;
            });
    }

    internal static bool TryRemoveRegistration(string path, Action<string> delete, Func<string, bool> exists)
    {
        try
        {
            delete(path);
            return !exists(path);
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            return false;
        }
    }

    public static string? GetRegisteredExtensionId(string browser) => GetRegisteredExtensionIds(browser)?.Split(", ", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    public static string? GetRegisteredExtensionIds(string browser)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var basePath = RegistrationPath(browser);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(basePath);
            var manifestPath = key?.GetValue(string.Empty) as string;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("allowed_origins", out var origins) || origins.ValueKind != JsonValueKind.Array) return null;
            var ids = new List<string>();
            foreach (var origin in origins.EnumerateArray())
            {
                var value = origin.GetString();
                const string prefix = "chrome-extension://";
                if (value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    var id = value[prefix.Length..].TrimEnd('/');
                    if (id.Length == 32 && id.All(character => character is >= 'a' and <= 'p') && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
                }
            }
            return ids.Count == 0 ? null : string.Join(", ", ids);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
        return null;
    }
}
