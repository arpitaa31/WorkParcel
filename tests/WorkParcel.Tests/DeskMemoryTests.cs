using Microsoft.Data.Sqlite;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Xunit;

namespace WorkParcel.Tests;

public sealed class DeskMemoryTests
{
    [Fact]
    public void TitlesAreNormalizedWithoutDiscardingDocumentIdentity()
    {
        Assert.Equal("REPORT - NOTEPAD", DeskMemoryLogic.NormalizeTitle("  Report   - Notepad  "));
        Assert.Equal("REPORT - NOTEPAD", DeskMemoryLogic.NormalizeTitle("Report - Notepad (Administrator)"));
        Assert.NotEqual(DeskMemoryLogic.NormalizeTitle("Report - Notepad"), DeskMemoryLogic.NormalizeTitle("Other - Notepad"));
    }

    [Fact]
    public void NegativeVirtualScreenCoordinatesAndOffscreenBoundsAreMadeSafe()
    {
        var work = new DeskRect(-1920, 0, 1920, 1040);
        var safe = DeskMemoryLogic.ClampToWorkArea(new DeskRect(-3500, -900, 640, 480), work);
        Assert.True(safe.IsUsable);
        Assert.InRange(safe.Left, -1920 - safe.Width + 48, -1920 + 48);
        Assert.InRange(safe.Top, 0 - safe.Height + 32, 32);
        Assert.True(safe.Width >= 320);
        Assert.True(safe.Height >= 180);
    }

    [Fact]
    public void MatchingUsesStrongIdentityAndFlagsRepeatedSameTitleWindowsAsAmbiguous()
    {
        var monitor = Monitor("DISPLAY1", true, new DeskRect(0, 0, 1920, 1080), new DeskRect(0, 0, 1920, 1040));
        var live = new[]
        {
            Window((nint)11, "C:\\Apps\\editor.exe", "editor", "Project - Editor", monitor, 0),
            Window((nint)12, "C:\\Apps\\editor.exe", "editor", "Project - Editor", monitor, 1)
        };
        var saved = new DeskWindowLayout { ExecutableIdentity = DeskMemoryLogic.NormalizeExecutable("C:\\Apps\\editor.exe"), ProcessName = "editor", CapturedTitle = "Project - Editor", NormalizedTitle = DeskMemoryLogic.NormalizeTitle("Project - Editor"), WindowClassName = "EditorWindow", ZOrderRank = 0 };
        var exact = DeskMemoryLogic.MatchWindow(saved, new[] { live[0] with { WindowClassName = "EditorWindow" } });
        var ambiguous = DeskMemoryLogic.MatchWindow(saved, live);
        Assert.Equal(DeskMatchConfidence.Exact, exact.Confidence);
        Assert.True(exact.IsAutomatic);
        Assert.Equal(DeskMatchConfidence.Ambiguous, ambiguous.Confidence);
        Assert.False(ambiguous.IsAutomatic);
    }

    [Fact]
    public void ChangedDpiAndMissingMonitorUseNormalizedGeometryAndFallback()
    {
        var sourceMonitor = Monitor("OLD", true, new DeskRect(-1920, 0, 1920, 1080), new DeskRect(-1920, 0, 1920, 1040), 96);
        var savedMonitor = new DeskMonitorLayout { Id = Guid.NewGuid(), DeviceIdentifier = sourceMonitor.DeviceIdentifier, FriendlyName = sourceMonitor.FriendlyName, IsPrimary = sourceMonitor.IsPrimary, Bounds = sourceMonitor.Bounds, WorkArea = sourceMonitor.WorkArea, DpiX = sourceMonitor.DpiX, DpiY = sourceMonitor.DpiY };
        var savedWindow = new DeskWindowLayout { SavedMonitorId = savedMonitor.Id, NormalBounds = new DeskRect(-1700, 100, 900, 700), AbsoluteBounds = new DeskRect(-1700, 100, 900, 700), RelativeLeft = .11, RelativeTop = .1, RelativeWidth = .47, RelativeHeight = .67, SourceDpiX = 96, SourceDpiY = 96, WindowState = DeskWindowState.Maximized };
        var currentMonitor = new DeskMonitorInfo("NEW", "New display", true, new DeskRect(0, 0, 2560, 1440), new DeskRect(0, 0, 2560, 1400), 144, 144, 0, 0);
        var mapping = Assert.Single(DeskMemoryLogic.MapMonitors(new[] { savedMonitor }, new[] { currentMonitor }));
        var placement = DeskMemoryLogic.ResolvePlacement(savedWindow, mapping, 144, restoreMaximized: true, restoreMinimized: false);
        Assert.False(mapping.IsExact);
        Assert.True(placement.UsedFallbackMonitor);
        Assert.True(placement.UsedScaledGeometry);
        Assert.Equal(DeskWindowState.Maximized, placement.State);
        Assert.True(placement.NormalBounds.Left >= currentMonitor.WorkArea.Left - placement.NormalBounds.Width + 48);
    }

    [Fact]
    public void SameMonitorDpiChangeScalesPhysicalNormalBoundsFromSavedWorkArea()
    {
        var source = Monitor("DISPLAY1", true, new DeskRect(0, 0, 1920, 1080), new DeskRect(0, 0, 1920, 1040), 96);
        var current = Monitor("DISPLAY1", true, new DeskRect(0, 0, 2560, 1440), new DeskRect(0, 0, 2560, 1400), 144);
        var savedMonitor = new DeskMonitorLayout { Id = Guid.NewGuid(), DeviceIdentifier = source.DeviceIdentifier, IsPrimary = true, Bounds = source.Bounds, WorkArea = source.WorkArea, DpiX = 96, DpiY = 96 };
        var saved = new DeskWindowLayout { SavedMonitorId = savedMonitor.Id, NormalBounds = new DeskRect(100, 100, 800, 600), AbsoluteBounds = new DeskRect(100, 100, 800, 600), RelativeWidth = .4, RelativeHeight = .5, SourceDpiX = 96, MonitorDpiX = 96, WindowState = DeskWindowState.Normal };
        var mapping = Assert.Single(DeskMemoryLogic.MapMonitors(new[] { savedMonitor }, new[] { current }));
        var placement = DeskMemoryLogic.ResolvePlacement(saved, mapping, current.DpiX, true, false);
        Assert.True(placement.UsedScaledGeometry);
        Assert.Equal(new DeskRect(150, 150, 1200, 900), placement.NormalBounds);
    }

    [Fact]
    public void SavedApplicationWindowsMatchChangedTitlesWithoutMergingHandles()
    {
        var saved = new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Old document", Value = "C:\\Apps\\editor.exe", ExecutablePath = "C:\\Apps\\editor.exe", ProcessName = "editor", WindowTitle = "Old document", WindowClassName = "EditorWindow" };
        var first = new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, RuntimeWindowHandle = (nint)41, RuntimeProcessId = 1001, DisplayName = "New document", Value = "C:\\Apps\\editor.exe", ExecutablePath = "C:\\Apps\\editor.exe", ProcessName = "editor", WindowTitle = "New document", WindowClassName = "EditorWindow" };
        var second = new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, RuntimeWindowHandle = (nint)42, RuntimeProcessId = 1002, DisplayName = "Other", Value = "C:\\Apps\\other.exe", ExecutablePath = "C:\\Apps\\other.exe", ProcessName = "other", WindowTitle = "Other" };
        var match = Assert.Single(OpenWindowService.MatchSavedItems(new[] { saved }, new[] { first, second }));
        Assert.Equal(first.RuntimeWindowHandle, match.Live?.Handle);
        Assert.False(match.IsAmbiguous);
        Assert.Contains("executable", match.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaptureIncludesMonitorDpiRelativeGeometryAndPartialFailures()
    {
        var monitor = Monitor("DISPLAY1", true, new DeskRect(0, 0, 1920, 1080), new DeskRect(0, 0, 1920, 1040), 120);
        var item = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Editor", ExecutablePath = "C:\\Apps\\editor.exe", Value = "C:\\Apps\\editor.exe", RuntimeWindowHandle = (nint)11 };
        var missing = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Gone", RuntimeWindowHandle = (nint)99 };
        var result = DeskMemoryLogic.BuildCapture(Guid.NewGuid(), new[] { item, missing }, new DeskSystemSnapshot(new[] { monitor }, new[] { Window((nint)11, "C:\\Apps\\editor.exe", "editor", "Editor", monitor, 0, 120) }), DateTime.Parse("2026-08-27T10:00:00Z").ToLocalTime());
        var saved = Assert.Single(result.Snapshot.Windows);
        Assert.Equal(120, saved.SourceDpiX);
        Assert.Equal(120, saved.MonitorDpiX);
        Assert.Equal(result.Snapshot.Monitors.Single().Id, saved.SavedMonitorId);
        Assert.InRange(saved.RelativeWidth, 0, 1);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task LayoutPersistsAcrossRestartAndCascadesWithoutExternalHandles()
    {
        using var temp = new TestDirectory();
        var paths = new AppDataPaths(temp.Path);
        var store = await CreateStoreAsync(paths);
        var monitor = Monitor("DISPLAY1", true, new DeskRect(0, 0, 1920, 1080), new DeskRect(0, 0, 1920, 1040));
        var item = new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Editor", ExecutablePath = "C:\\Apps\\editor.exe", Value = "C:\\Apps\\editor.exe", RuntimeWindowHandle = (nint)11 };
        var capture = DeskMemoryLogic.BuildCapture(Guid.NewGuid(), new[] { item }, new DeskSystemSnapshot(new[] { monitor }, new[] { Window((nint)11, "C:\\Apps\\editor.exe", "editor", "Editor", monitor, 0) }));
        var parcel = await store.CreateWithItemsAsync("Desk", "", new[] { item }, deskLayout: capture.Snapshot, parcelId: capture.Snapshot.ParcelId);
        var restarted = await CreateStoreAsync(paths);
        var loaded = Assert.Single(restarted.Parcels);
        Assert.NotNull(loaded.DeskLayout);
        Assert.Single(loaded.DeskLayout!.Monitors);
        Assert.Single(loaded.DeskLayout.Windows);
        Assert.Equal(item.Id, loaded.DeskLayout.Windows[0].ParcelItemId);
        Assert.Equal(100, loaded.DeskLayout.Windows[0].AbsoluteBounds.Left);
        Assert.Equal(0L, await ScalarAsync(paths.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('DeskWindowLayouts') WHERE name IN ('HWND','PID','Screenshot','Contents');"));
        await restarted.DeleteAsync(loaded);
        Assert.Equal(0L, await ScalarAsync(paths.DatabasePath, "SELECT COUNT(*) FROM DeskLayoutSnapshots;"));
        Assert.Equal(0L, await ScalarAsync(paths.DatabasePath, "SELECT COUNT(*) FROM DeskWindowLayouts;"));
        Assert.NotNull(parcel);
    }

    [Fact]
    public async Task DeskMemorySafetySettingsPersistWithBoundedTimeout()
    {
        using var temp = new TestDirectory();
        var store = await CreateStoreAsync(new AppDataPaths(temp.Path));
        await store.SetDeskMemorySettingsAsync(new DeskMemorySettings(false, false, false, false, true, false, true, 999));
        var restarted = await CreateStoreAsync(new AppDataPaths(temp.Path));
        Assert.False(restarted.DeskMemorySettings.RestoreByDefault);
        Assert.False(restarted.DeskMemorySettings.ReviewBeforeApplying);
        Assert.False(restarted.DeskMemorySettings.ReuseMatchingOpenWindows);
        Assert.True(restarted.DeskMemorySettings.RestoreMinimized);
        Assert.Equal(120, restarted.DeskMemorySettings.RestorationTimeoutSeconds);
    }

    [Fact]
    public async Task MultipleWindowsFromOneExecutableRemainDistinctEvenWithTheSameTitle()
    {
        using var temp = new TestDirectory();
        var now = DateTime.Now;
        var items = new[]
        {
            new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Editor one", Value = "C:\\Apps\\editor.exe", ExecutablePath = "C:\\Apps\\editor.exe", ProcessName = "editor", WindowTitle = "Project", NormalizedIdentity = "C:\\APPS\\EDITOR.EXE|PROJECT", CreatedAt = now, UpdatedAt = now },
            new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Editor two", Value = "C:\\Apps\\editor.exe", ExecutablePath = "C:\\Apps\\editor.exe", ProcessName = "editor", WindowTitle = "Project", NormalizedIdentity = "C:\\APPS\\EDITOR.EXE|PROJECT", CreatedAt = now, UpdatedAt = now }
        };
        var store = await CreateStoreAsync(new AppDataPaths(temp.Path));
        var parcel = await store.CreateWithItemsAsync("Two editors", "", items);
        var restarted = await CreateStoreAsync(new AppDataPaths(temp.Path));
        Assert.Equal(2, Assert.Single(restarted.Parcels).Items.Count(item => item.ItemType == ParcelItemType.ApplicationWindow));
        Assert.Equal(2, parcel.Items.Count);
    }

    [Fact]
    public async Task RestoreMovesHighConfidenceWindowsOnceAndUndoUsesOnlyLiveHandles()
    {
        var monitor = Monitor("DISPLAY1", true, new DeskRect(0, 0, 1920, 1080), new DeskRect(0, 0, 1920, 1040));
        var item = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Editor", ExecutablePath = "C:\\Apps\\editor.exe", Value = "C:\\Apps\\editor.exe", RuntimeWindowHandle = (nint)11 };
        var provider = new FakeProvider(new DeskSystemSnapshot(new[] { monitor }, new[] { Window((nint)11, "C:\\Apps\\editor.exe", "editor", "Editor", monitor, 0, bounds: new DeskRect(300, 200, 800, 600)) }));
        var capture = DeskMemoryLogic.BuildCapture(Guid.NewGuid(), new[] { item }, new DeskSystemSnapshot(new[] { monitor }, new[] { Window((nint)11, "C:\\Apps\\editor.exe", "editor", "Editor", monitor, 0, bounds: new DeskRect(30, 40, 900, 700)) }));
        var service = new DeskMemoryService(provider);
        var result = await service.RestoreAsync(capture.Snapshot, new DeskRestoreOptions(EnableUndo: true, TimeoutSeconds: 2));
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(2, provider.Applied.Count);
        var undo = await service.UndoLastAsync();
        Assert.Equal(1, undo.RestoredCount);
        Assert.Equal(3, provider.Applied.Count);
    }

    [Fact]
    public void LayoutOnlyPlanDoesNotLaunchOrAutoMoveAmbiguousWindows()
    {
        var monitor = Monitor("DISPLAY1", true, new DeskRect(0, 0, 1920, 1080), new DeskRect(0, 0, 1920, 1040));
        var saved = new DeskLayoutSnapshot { ParcelId = Guid.NewGuid(), CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        saved.Monitors.Add(new DeskMonitorLayout { Id = Guid.NewGuid(), LayoutSnapshotId = saved.Id, ParcelId = saved.ParcelId, DeviceIdentifier = monitor.DeviceIdentifier, FriendlyName = monitor.FriendlyName, IsPrimary = true, Bounds = monitor.Bounds, WorkArea = monitor.WorkArea, DpiX = 96, DpiY = 96 });
        saved.Windows.Add(new DeskWindowLayout { LayoutSnapshotId = saved.Id, ParcelId = saved.ParcelId, SavedMonitorId = saved.Monitors[0].Id, ExecutableIdentity = DeskMemoryLogic.NormalizeExecutable("C:\\Apps\\editor.exe"), ProcessName = "editor", CapturedTitle = "Editor", NormalizedTitle = "EDITOR", NormalBounds = new DeskRect(30, 30, 800, 600), AbsoluteBounds = new DeskRect(30, 30, 800, 600), RelativeWidth = .4, RelativeHeight = .5, IsSupported = true });
        var current = new DeskSystemSnapshot(new[] { monitor }, new[] { Window((nint)11, "C:\\Apps\\editor.exe", "editor", "Editor", monitor, 0), Window((nint)12, "C:\\Apps\\editor.exe", "editor", "Editor", monitor, 1) });
        var plan = DeskMemoryLogic.BuildPlan(saved, current);
        Assert.Equal(DeskMatchConfidence.Ambiguous, Assert.Single(plan.Items).Match.Confidence);
        Assert.DoesNotContain(plan.Items, item => item.Match.IsAutomatic);
    }

    private static DeskMonitorInfo Monitor(string id, bool primary, DeskRect bounds, DeskRect work, int dpi = 96) => new(id, id, primary, bounds, work, dpi, dpi, 0, 0);

    private static DeskLiveWindow Window(nint handle, string executable, string process, string title, DeskMonitorInfo monitor, int rank, int dpi = 96, DeskRect? bounds = null) =>
        new(handle, (uint)handle, title, DeskMemoryLogic.NormalizeTitle(title), process, executable, null, "EditorWindow", bounds ?? new DeskRect(100 + rank * 50, 100, 800, 600), bounds ?? new DeskRect(100 + rank * 50, 100, 800, 600), DeskWindowState.Normal, monitor.DeviceIdentifier, dpi, dpi, true, false, rank);

    private static async Task<WorkspaceStore> CreateStoreAsync(AppDataPaths paths) { var store = new WorkspaceStore(paths); await store.InitializeAsync(); return store; }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }

    private sealed class FakeProvider(DeskSystemSnapshot snapshot) : IDeskWindowProvider
    {
        public DeskSystemSnapshot Snapshot { get; } = snapshot;
        public List<(nint Handle, DeskResolvedPlacement Placement)> Applied { get; } = new();
        public DeskSystemSnapshot GetSnapshot() => Snapshot;
        public bool TryGetWindow(nint handle, out DeskLiveWindow window) { window = Snapshot.Windows.FirstOrDefault(candidate => candidate.Handle == handle)!; return window is not null; }
        public bool TryApplyPlacement(nint handle, DeskResolvedPlacement placement, out string error) { error = string.Empty; Applied.Add((handle, placement)); return true; }
        public bool IsWindow(nint handle) => Snapshot.Windows.Any(window => window.Handle == handle);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkParcelDeskTests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
