using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public sealed record DeskRestoreOptions(
    bool RestoreMaximized = true,
    bool RestoreMinimized = false,
    bool EnableUndo = true,
    int TimeoutSeconds = 12,
    TimeSpan? UndoLifetime = null);

public delegate Task<nint?> DeskAmbiguityResolverAsync(
    DeskWindowMatch match,
    IReadOnlyList<DeskLiveWindow> candidates,
    CancellationToken cancellationToken);

/// <summary>
/// Coordinates capture and restoration without putting native calls in pages.
/// Restoration is bounded, cancellable, and applies each live handle at most
/// once per operation.
/// </summary>
public sealed class DeskMemoryService
{
    private static readonly TimeSpan DefaultUndoLifetime = TimeSpan.FromMinutes(5);
    private readonly IDeskWindowProvider _provider;
    private DeskUndoOperation? _lastUndo;

    public DeskMemoryService(IDeskWindowProvider? provider = null) => _provider = provider ?? new NativeDeskWindowProvider();

    public IDeskWindowProvider Provider => _provider;

    public Task<DeskLayoutCaptureResult> CaptureAsync(
        Guid parcelId,
        IReadOnlyList<ParcelItem> selectedItems,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DeskMemoryLogic.BuildCapture(parcelId, selectedItems, _provider.GetSnapshot());
        }, cancellationToken);

    public DeskLayoutCaptureResult Capture(
        Guid parcelId,
        IReadOnlyList<ParcelItem> selectedItems,
        DateTime? timestamp = null) =>
        DeskMemoryLogic.BuildCapture(parcelId, selectedItems, _provider.GetSnapshot(), timestamp);

    public async Task<DeskRestoreResult> RestoreAsync(
        DeskLayoutSnapshot layout,
        DeskRestoreOptions? options = null,
        ISet<Guid>? allowedParcelItemIds = null,
        DeskAmbiguityResolverAsync? ambiguityResolver = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DeskRestoreOptions();
        if (!layout.IsEnabled)
        {
            return new DeskRestoreResult
            {
                Topology = DeskTopologyComparison.SignificantlyDifferent,
                Items = layout.Windows.Select(window => new DeskRestoreItemResult(window.Id, window.ParcelItemId, DeskRestoreResultKind.Skipped, "Desk Memory is disabled.")).ToList()
            };
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 2, 120));
        var deadline = DateTime.UtcNow + timeout;
        DeskRestorePlan? plan = null;
        DeskSystemSnapshot? current = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = _provider.GetSnapshot();
            plan = DeskMemoryLogic.BuildPlan(layout, current, allowedParcelItemIds);
            if (plan.Items.All(item =>
                    (item.Match.Live is not null && item.Match.IsAutomatic) ||
                    item.PreflightResult is DeskRestoreResultKind.Unsupported or DeskRestoreResultKind.Ambiguous)) break;
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        if (plan is null || current is null)
            return new DeskRestoreResult { Topology = DeskTopologyComparison.SignificantlyDifferent, Cancelled = false };

        var plannedItems = plan.Items.ToList();
        if (ambiguityResolver is not null)
        {
            var reservedHandles = plannedItems.Where(item => item.Match.IsAutomatic && item.Match.Live is not null).Select(item => item.Match.Live!.Handle).ToHashSet();
            foreach (var item in plannedItems.Where(item => item.PreflightResult == DeskRestoreResultKind.Ambiguous).ToList())
            {
                var candidateWindows = current.Windows
                    .Where(window => !reservedHandles.Contains(window.Handle) && DeskMemoryLogic.ScoreWindow(item.Saved, window) >= 45)
                    .OrderBy(window => window.ZOrderRank)
                    .ToList();
                var selectedHandle = await ambiguityResolver(item.Match, candidateWindows, cancellationToken);
                if (selectedHandle == nint.Zero || candidateWindows.All(window => window.Handle != selectedHandle)) continue;
                var selected = candidateWindows.First(window => window.Handle == selectedHandle);
                var mapping = plan.MonitorMappings.FirstOrDefault(candidate => candidate.Saved.Id == item.Saved.SavedMonitorId);
                if (mapping is null || mapping.Current is null) continue;
                var match = item.Match with
                {
                    Live = selected,
                    Confidence = DeskMatchConfidence.High,
                    ScoreMargin = int.MaxValue,
                    Explanation = "User selected this candidate window."
                };
                plannedItems[plannedItems.IndexOf(item)] = item with
                {
                    Match = match,
                    Placement = DeskMemoryLogic.ResolvePlacement(item.Saved, mapping, mapping.Current.DpiX, options.RestoreMaximized, options.RestoreMinimized),
                    PreflightResult = null
                };
                reservedHandles.Add(selected.Handle);
            }
        }

        var undo = new List<DeskUndoWindowState>();
        var results = new List<DeskRestoreItemResult>();
        var appliedHandles = new HashSet<nint>();
        var appliedPlacements = new List<(nint Handle, DeskResolvedPlacement Placement)>();
        foreach (var item in plannedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Match.Live is null)
            {
                var kind = item.PreflightResult ?? (DateTime.UtcNow >= deadline ? DeskRestoreResultKind.TimedOut : DeskRestoreResultKind.Skipped);
                var message = kind == DeskRestoreResultKind.TimedOut ? "The window did not appear before the restore timeout." : item.Match.Explanation;
                results.Add(new(item.Saved.Id, item.Saved.ParcelItemId, kind, message));
                continue;
            }
            if (item.Placement is null)
            {
                results.Add(new(item.Saved.Id, item.Saved.ParcelItemId, item.PreflightResult ?? DeskRestoreResultKind.Failed, item.Match.Explanation));
                continue;
            }
            var live = item.Match.Live!;
            if (!appliedHandles.Add(live.Handle))
            {
                results.Add(new(item.Saved.Id, item.Saved.ParcelItemId, DeskRestoreResultKind.Ambiguous, "The same current window was selected for more than one saved window."));
                continue;
            }
            var requestedState = item.Placement.State switch
            {
                DeskWindowState.Maximized when options.RestoreMaximized => DeskWindowState.Maximized,
                DeskWindowState.Minimized when options.RestoreMinimized => DeskWindowState.Minimized,
                _ => DeskWindowState.Normal
            };
            var placement = item.Placement with { State = requestedState };
            if (options.EnableUndo && _provider.TryGetWindow(live.Handle, out var before) && before.NormalBounds.IsUsable)
                undo.Add(new DeskUndoWindowState(before.Handle, before.NormalBounds, before.WindowState));
            if (!_provider.TryApplyPlacement(live.Handle, placement, out var error))
            {
                var kind = error.Contains("denied", StringComparison.OrdinalIgnoreCase) || error.Contains("elevated", StringComparison.OrdinalIgnoreCase)
                    ? DeskRestoreResultKind.AccessDenied
                    : DeskRestoreResultKind.Failed;
                results.Add(new(item.Saved.Id, item.Saved.ParcelItemId, kind, error, live.Handle));
                continue;
            }
            var restoreKind = placement.UsedFallbackMonitor
                ? DeskRestoreResultKind.MovedToFallbackMonitor
                : placement.UsedScaledGeometry ? DeskRestoreResultKind.RestoredScaled
                : DeskRestoreResultKind.RestoredExactly;
            var stateText = placement.State == DeskWindowState.Maximized ? " and maximized" : placement.State == DeskWindowState.Minimized ? " and minimized" : string.Empty;
            results.Add(new(item.Saved.Id, item.Saved.ParcelItemId, restoreKind, string.IsNullOrWhiteSpace(error) ? $"Window restored{stateText}." : $"Window restored{stateText}; Windows adjusted the frame and it will be verified again.", live.Handle));
            appliedPlacements.Add((live.Handle, placement));
        }
        if (options.EnableUndo && undo.Count > 0)
            _lastUndo = new DeskUndoOperation { Windows = undo, CreatedAtUtc = DateTime.UtcNow };

        // Windows can recalculate a frame, snap zone, or DPI after the first
        // SetWindowPos call. A bounded verification pass reapplies the same
        // placement to the same validated HWND without rematching windows.
        if (appliedHandles.Count > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            foreach (var applied in appliedPlacements)
            {
                var resultIndex = results.FindIndex(result => result.RuntimeHandle == applied.Handle);
                if (!_provider.IsWindow(applied.Handle))
                {
                    if (resultIndex >= 0) results[resultIndex] = results[resultIndex] with { Kind = DeskRestoreResultKind.Failed, Message = "The window disappeared during placement verification." };
                    continue;
                }
                if (!_provider.TryApplyPlacement(applied.Handle, applied.Placement, out var verificationError) && resultIndex >= 0)
                    results[resultIndex] = results[resultIndex] with { Kind = DeskRestoreResultKind.Failed, Message = $"Initial placement succeeded, but verification failed: {verificationError}" };
                else if (!string.IsNullOrWhiteSpace(verificationError) && resultIndex >= 0)
                    results[resultIndex] = results[resultIndex] with { Message = $"{results[resultIndex].Message} Windows reported: {verificationError}" };
            }
        }
        return new DeskRestoreResult { Items = results, Topology = plan.Topology, Cancelled = false };
    }

    public Task<DeskRestoreResult> RestoreLayoutOnlyAsync(
        DeskLayoutSnapshot layout,
        DeskRestoreOptions? options = null,
        ISet<Guid>? allowedParcelItemIds = null,
        DeskAmbiguityResolverAsync? ambiguityResolver = null,
        CancellationToken cancellationToken = default) =>
        RestoreAsync(layout, options, allowedParcelItemIds, ambiguityResolver, cancellationToken);

    public async Task<DeskRestoreResult> UndoLastAsync(CancellationToken cancellationToken = default)
    {
        var undo = _lastUndo;
        _lastUndo = null;
        if (undo is null) return new DeskRestoreResult { Items = Array.Empty<DeskRestoreItemResult>(), Topology = DeskTopologyComparison.Exact };
        if (undo.IsExpired(DefaultUndoLifetime))
            return new DeskRestoreResult { Items = undo.Windows.Select(window => new DeskRestoreItemResult(Guid.Empty, null, DeskRestoreResultKind.TimedOut, "Undo expired before it was used.", window.Handle)).ToList(), Topology = DeskTopologyComparison.Exact };

        var current = _provider.GetSnapshot();
        var results = new List<DeskRestoreItemResult>();
        foreach (var old in undo.Windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Windows.FirstOrDefault(window => window.Handle == old.Handle) is not { } live)
            {
                results.Add(new(Guid.Empty, null, DeskRestoreResultKind.Skipped, "The original window is no longer available.", old.Handle));
                continue;
            }
            var monitor = current.Monitors.FirstOrDefault(candidate => string.Equals(candidate.DeviceIdentifier, live.MonitorDeviceIdentifier, StringComparison.OrdinalIgnoreCase))
                ?? current.Monitors.FirstOrDefault(candidate => candidate.IsPrimary)
                ?? current.Monitors.FirstOrDefault();
            if (monitor is null)
            {
                results.Add(new(Guid.Empty, null, DeskRestoreResultKind.Failed, "No monitor is available for Undo.", old.Handle));
                continue;
            }
            var placement = new DeskResolvedPlacement(DeskMemoryLogic.ClampToWorkArea(old.NormalBounds, monitor.WorkArea), old.State, monitor, false, false);
            if (_provider.TryApplyPlacement(old.Handle, placement, out var error)) results.Add(new(Guid.Empty, null, DeskRestoreResultKind.RestoredExactly, "The previous window placement was restored.", old.Handle));
            else results.Add(new(Guid.Empty, null, DeskRestoreResultKind.Failed, error, old.Handle));
        }
        await Task.CompletedTask;
        return new DeskRestoreResult { Items = results, Topology = DeskTopologyComparison.Exact };
    }
}
