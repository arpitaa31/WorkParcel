namespace WorkParcel_App.Models;

public enum DeskWindowState
{
    Normal,
    Maximized,
    Minimized,
    Unknown
}

public enum DeskMatchConfidence
{
    Exact,
    High,
    Possible,
    Ambiguous,
    NoMatch,
    Unsupported
}

public enum DeskTopologyComparison
{
    Exact,
    CompatibleChanged,
    MissingMonitor,
    NewMonitor,
    SignificantlyDifferent
}

public enum DeskRestoreResultKind
{
    RestoredExactly,
    RestoredScaled,
    MovedToFallbackMonitor,
    ReusedExistingWindow,
    Ambiguous,
    TimedOut,
    Unsupported,
    AccessDenied,
    Skipped,
    Failed
}

public readonly record struct DeskRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Math.Max(0, Width);
    public int Bottom => Top + Math.Max(0, Height);
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
    public bool IsUsable => Width > 0 && Height > 0;
}

public sealed class DeskLayoutSnapshot : BindableBase
{
    private string _name = "Desk Memory";
    private bool _isCurrent = true;
    private bool _isEnabled = true;

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ParcelId { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public string TopologySignature { get; set; } = string.Empty;
    public List<DeskMonitorLayout> Monitors { get; } = new();
    public List<DeskWindowLayout> Windows { get; } = new();
    public int RestorableWindowCount => Windows.Count(window => window.IsEnabled && window.IsSupported);
    public int UnsupportedWindowCount => Windows.Count(window => !window.IsSupported);
    public DateTime CaptureTimestamp => CreatedAt;
}

public sealed class DeskMonitorLayout
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid LayoutSnapshotId { get; set; }
    public Guid ParcelId { get; set; }
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DeskRect Bounds { get; set; }
    public DeskRect WorkArea { get; set; }
    public string RelativeArrangement { get; set; } = string.Empty;
    public int DpiX { get; set; } = 96;
    public int DpiY { get; set; } = 96;
    public int Orientation { get; set; }
    public int CaptureOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class DeskWindowLayout
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid LayoutSnapshotId { get; set; }
    public Guid ParcelId { get; set; }
    public Guid? ParcelItemId { get; set; }
    public string ExecutableIdentity { get; set; } = string.Empty;
    public string? ApplicationIdentifier { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string CapturedTitle { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public string? WindowClassName { get; set; }
    public Guid? SavedMonitorId { get; set; }
    public DeskRect AbsoluteBounds { get; set; }
    public DeskRect NormalBounds { get; set; }
    public double RelativeLeft { get; set; }
    public double RelativeTop { get; set; }
    public double RelativeWidth { get; set; }
    public double RelativeHeight { get; set; }
    public DeskWindowState WindowState { get; set; } = DeskWindowState.Normal;
    public int ZOrderRank { get; set; }
    public int SourceDpiX { get; set; } = 96;
    public int SourceDpiY { get; set; } = 96;
    public int MonitorDpiX { get; set; } = 96;
    public int MonitorDpiY { get; set; } = 96;
    public bool IsTopmost { get; set; }
    public bool CoordinatesArePhysicalPixels { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public bool IsSupported { get; set; } = true;
    public string? MatchMetadata { get; set; }
    public DeskMatchConfidence LastMatchConfidence { get; set; } = DeskMatchConfidence.NoMatch;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Runtime-only window data. HWND and PID are deliberately never serialized.
public sealed record DeskLiveWindow(
    nint Handle,
    uint ProcessId,
    string Title,
    string NormalizedTitle,
    string ProcessName,
    string? ExecutablePath,
    string? ApplicationIdentifier,
    string? WindowClassName,
    DeskRect Bounds,
    DeskRect NormalBounds,
    DeskWindowState WindowState,
    string MonitorDeviceIdentifier,
    int DpiX,
    int DpiY,
    bool Visible,
    bool ToolWindow,
    int ZOrderRank)
{
    public bool IsTopmost { get; init; }
    public bool CoordinatesArePhysicalPixels { get; init; } = true;
    public DateTime? ProcessStartTimeUtc { get; init; }
}

public sealed record DeskMonitorInfo(
    string DeviceIdentifier,
    string FriendlyName,
    bool IsPrimary,
    DeskRect Bounds,
    DeskRect WorkArea,
    int DpiX,
    int DpiY,
    int Orientation,
    int CaptureOrder);

public sealed record DeskSystemSnapshot(IReadOnlyList<DeskMonitorInfo> Monitors, IReadOnlyList<DeskLiveWindow> Windows);

public sealed record DeskGeometryCaptureFailure(Guid? ParcelItemId, string DisplayName, string Reason);

public sealed class DeskLayoutCaptureResult
{
    public DeskLayoutSnapshot Snapshot { get; init; } = new();
    public IReadOnlyList<DeskGeometryCaptureFailure> Failures { get; init; } = Array.Empty<DeskGeometryCaptureFailure>();
}

public sealed record DeskWindowMatch(
    DeskWindowLayout Saved,
    DeskLiveWindow? Live,
    int Score,
    int ScoreMargin,
    DeskMatchConfidence Confidence,
    string Explanation)
{
    public bool IsAutomatic => Confidence is DeskMatchConfidence.Exact or DeskMatchConfidence.High;
}

public sealed record DeskMonitorMapping(DeskMonitorLayout Saved, DeskMonitorInfo? Current, bool IsExact, string Explanation);

public sealed record DeskResolvedPlacement(DeskRect NormalBounds, DeskWindowState State, DeskMonitorInfo TargetMonitor, bool UsedScaledGeometry, bool UsedFallbackMonitor, bool IsTopmost = false);

public sealed record DeskRestorePlanItem(DeskWindowLayout Saved, DeskWindowMatch Match, DeskResolvedPlacement? Placement, DeskRestoreResultKind? PreflightResult);

public sealed class DeskRestorePlan
{
    public DeskTopologyComparison Topology { get; init; }
    public IReadOnlyList<DeskMonitorMapping> MonitorMappings { get; init; } = Array.Empty<DeskMonitorMapping>();
    public IReadOnlyList<DeskRestorePlanItem> Items { get; init; } = Array.Empty<DeskRestorePlanItem>();
    public IReadOnlyList<DeskWindowMatch> AmbiguousMatches => Items.Where(item => item.Match.Confidence is DeskMatchConfidence.Ambiguous or DeskMatchConfidence.Possible).Select(item => item.Match).ToList();
}

public sealed record DeskRestoreItemResult(Guid LayoutId, Guid? ParcelItemId, DeskRestoreResultKind Kind, string Message, nint RuntimeHandle = default);

public sealed class DeskRestoreResult
{
    public IReadOnlyList<DeskRestoreItemResult> Items { get; init; } = Array.Empty<DeskRestoreItemResult>();
    public DeskTopologyComparison Topology { get; init; }
    public bool Cancelled { get; init; }
    public int RestoredCount => Items.Count(item => item.Kind is DeskRestoreResultKind.RestoredExactly or DeskRestoreResultKind.RestoredScaled or DeskRestoreResultKind.MovedToFallbackMonitor or DeskRestoreResultKind.ReusedExistingWindow);
    public int FailedCount => Items.Count(item => item.Kind is DeskRestoreResultKind.Failed or DeskRestoreResultKind.AccessDenied or DeskRestoreResultKind.TimedOut);
}

public sealed record DeskMemorySettings(
    bool RestoreByDefault = true,
    bool ReviewBeforeApplying = true,
    bool ReuseMatchingOpenWindows = true,
    bool RestoreMaximized = true,
    bool RestoreMinimized = false,
    bool ShowPreviewDuringOpen = true,
    bool EnableUndo = true,
    int RestorationTimeoutSeconds = 12)
{
    public const string SettingKey = "DeskMemory.Settings";
    public static DeskMemorySettings SafeDefaults => new();
}

public sealed record DeskUndoWindowState(nint Handle, DeskRect NormalBounds, DeskWindowState State);

public sealed class DeskUndoOperation
{
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);
    public IReadOnlyList<DeskUndoWindowState> Windows { get; init; } = Array.Empty<DeskUndoWindowState>();
    public bool IsExpired() => DateTime.UtcNow - CreatedAtUtc > Lifetime;
    public bool IsExpired(TimeSpan lifetime) => DateTime.UtcNow - CreatedAtUtc > lifetime;
}
