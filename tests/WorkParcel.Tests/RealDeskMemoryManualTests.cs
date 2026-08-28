using System.Diagnostics;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Xunit;

namespace WorkParcel.Tests;

public sealed class RealDeskMemoryManualTests
{
    [Fact]
    public async Task RealDisposableWindowsCanBeCapturedClosedReopenedAndRestored()
    {
        // This is deliberately opt-in: it opens, moves, closes and reopens
        // only the disposable windows created by the helper app.
        if (!string.Equals(Environment.GetEnvironmentVariable("WORKPARCEL_RUN_REAL_DESK_TEST"), "1", StringComparison.Ordinal)) return;
        using var temp = new DisposableDirectory();
        var firstTitle = "Desk Memory Alpha"; var secondTitle = "Desk Memory Beta";
        var executable = FindHelperExecutable();
        Assert.True(File.Exists(executable), "Build tools/DeskMemoryDisposableWindow before running the opt-in manual test.");
        var first = Start(executable, firstTitle); var second = Start(executable, secondTitle); var firstPid = first.Id; var secondPid = second.Id;
        var provider = new NativeDeskWindowProvider();
        try
        {
            var initial = await WaitForWindowsAsync(provider, new[] { first.Id, second.Id }, minimum: 2);
            Assert.True(initial.Count >= 2, "Both disposable windows did not become visible.");
            var selectedWindows = initial.Where(window => window.ProcessId == (uint)first.Id || window.ProcessId == (uint)second.Id).Take(2).ToList();
            Assert.Equal(2, selectedWindows.Count);
            var items = selectedWindows.Select((window, index) => new ParcelItem
            {
                Id = Guid.NewGuid(), ParcelId = Guid.NewGuid(), ItemType = ParcelItemType.ApplicationWindow, DisplayName = window.Title,
                Value = window.ExecutablePath ?? window.ProcessName, ExecutablePath = window.ExecutablePath, ProcessName = window.ProcessName,
                WindowTitle = window.Title, LaunchArguments = window.Title, RuntimeWindowHandle = window.Handle, LaunchEnabled = true, CloseSupported = true
            }).ToList();
            var capture = DeskMemoryLogic.BuildCapture(Guid.NewGuid(), items, new DeskSystemSnapshot(provider.GetSnapshot().Monitors, selectedWindows));
            Assert.Equal(2, capture.Snapshot.Windows.Count);
            var store = new WorkspaceStore(new AppDataPaths(temp.Path));
            await store.InitializeAsync();
            await store.CreateWithItemsAsync("Manual Desk Memory", "Disposable validation parcel", items, deskLayout: capture.Snapshot, parcelId: capture.Snapshot.ParcelId);
            var restartedStore = new WorkspaceStore(new AppDataPaths(temp.Path));
            await restartedStore.InitializeAsync();
            var persistedLayout = Assert.Single(restartedStore.Parcels).DeskLayout;
            Assert.NotNull(persistedLayout);

            foreach (var saved in capture.Snapshot.Windows)
            {
                var live = selectedWindows.Single(window => window.Handle == items.Single(item => item.Id == saved.ParcelItemId).RuntimeWindowHandle);
                var monitor = provider.GetSnapshot().Monitors.First(monitor => string.Equals(monitor.DeviceIdentifier, live.MonitorDeviceIdentifier, StringComparison.OrdinalIgnoreCase));
                var moved = saved.NormalBounds with { Left = saved.NormalBounds.Left + 80, Top = saved.NormalBounds.Top + 60 };
                Assert.True(provider.TryApplyPlacement(live.Handle, new DeskResolvedPlacement(DeskMemoryLogic.ClampToWorkArea(moved, monitor.WorkArea), DeskWindowState.Normal, monitor, false, false), out var moveError), moveError);
            }
            CloseWindows(provider.GetSnapshot().Windows.Where(window => selectedWindows.Any(selected => selected.Handle == window.Handle)).Select(window => window.Handle));
            await WaitForWindowsAsync(provider, new[] { first.Id, second.Id }, minimum: 0);
            first.Dispose(); second.Dispose();

            try
            {
                var openResults = await new ItemLaunchService().OpenAsync(items, Array.Empty<ParcelItem>());
                Assert.Equal(2, openResults.Count(result => result.Status == ItemOpenStatus.Opened));
                var reopened = await WaitForTitlesAsync(provider, new[] { firstTitle, secondTitle });
                var service = new DeskMemoryService(provider);
                var result = await service.RestoreAsync(persistedLayout!, new DeskRestoreOptions(TimeoutSeconds: 8));
                Assert.Equal(2, result.RestoredCount);
                Assert.DoesNotContain(result.Items, item => item.Kind is DeskRestoreResultKind.Ambiguous or DeskRestoreResultKind.TimedOut or DeskRestoreResultKind.Failed);
                Assert.Equal(2, reopened.Count(window => window.Title is "Desk Memory Alpha" or "Desk Memory Beta"));
            }
            finally
            {
                CloseWindows(provider.GetSnapshot().Windows.Where(window => window.Title is "Desk Memory Alpha" or "Desk Memory Beta").Select(window => window.Handle));
            }
        }
        finally
        {
            CloseWindows(provider.GetSnapshot().Windows.Where(window => window.ProcessId == (uint)firstPid || window.ProcessId == (uint)secondPid).Select(window => window.Handle));
            DisposeProcess(first); DisposeProcess(second);
        }
    }

    private static Process Start(string executable, string title) => Process.Start(new ProcessStartInfo { FileName = executable, Arguments = title, UseShellExecute = true }) ?? throw new InvalidOperationException("Disposable window did not start.");

    private static string FindHelperExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "DeskMemoryDisposableWindow", "bin", "Debug", "net10.0-windows", "DeskMemoryDisposableWindow.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return string.Empty;
    }

    private static async Task<IReadOnlyList<DeskLiveWindow>> WaitForWindowsAsync(NativeDeskWindowProvider provider, IReadOnlyList<int> processIds, int minimum)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var windows = provider.GetSnapshot().Windows.Where(window => processIds.Contains((int)window.ProcessId)).ToList();
            if (windows.Count >= minimum && (minimum > 0 || windows.Count == 0)) return windows;
            await Task.Delay(150);
        }
        return provider.GetSnapshot().Windows.Where(window => processIds.Contains((int)window.ProcessId)).ToList();
    }

    private static async Task<IReadOnlyList<DeskLiveWindow>> WaitForTitlesAsync(NativeDeskWindowProvider provider, IReadOnlyList<string> titles)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var windows = provider.GetSnapshot().Windows.Where(window => titles.Contains(window.Title, StringComparer.Ordinal)).ToList();
            if (windows.Count >= titles.Count) return windows;
            await Task.Delay(150);
        }
        return provider.GetSnapshot().Windows.Where(window => titles.Contains(window.Title, StringComparer.Ordinal)).ToList();
    }

    private static void CloseWindows(IEnumerable<nint> handles)
    {
        foreach (var handle in handles) if (NativeMethods.IsWindow(handle)) NativeMethods.PostMessage(handle, NativeMethods.WmClose, nint.Zero, nint.Zero);
    }

    private static void DisposeProcess(Process process)
    {
        try { if (!process.HasExited) { process.CloseMainWindow(); process.WaitForExit(1000); } } catch { }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private sealed class DisposableDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkParcelRealDesk", Guid.NewGuid().ToString("N"));
        public DisposableDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
