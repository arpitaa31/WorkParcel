using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public sealed record WindowCandidate(nint Handle, uint ProcessId, string Title, string ProcessName, string? ExecutablePath, string FriendlyName, bool Visible, bool ToolWindow, string? WindowClassName = null);
public sealed record SavedWindowMatch(ParcelItem Item, WindowCandidate? Live, int Score, int ScoreMargin, bool IsAmbiguous, string Explanation);

public sealed class OpenWindowService
{
    private static readonly HashSet<string> SystemNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "sihost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp", "SystemSettings", "ApplicationFrameHost"
    };

    public Task<IReadOnlyList<ParcelItem>> DetectAsync(Guid parcelId, CancellationToken cancellationToken = default) => Task.Run(() => Detect(parcelId, cancellationToken), cancellationToken);

    public static bool ShouldInclude(WindowCandidate item, uint ownProcessId)
    {
        if (!item.Visible || item.ToolWindow || item.ProcessId == ownProcessId || string.IsNullOrWhiteSpace(item.Title)) return false;
        if (SystemNoise.Contains(item.ProcessName)) return false;
        return !item.Title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) && !item.Title.Equals("Windows Input Experience", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSystemNoise(string processName) => SystemNoise.Contains(processName);

    public static IReadOnlyList<SavedWindowMatch> MatchSavedItems(IEnumerable<ParcelItem> savedItems, IEnumerable<WindowCandidate> liveWindows)
    {
        var available = liveWindows.Where(window => window.Visible && !window.ToolWindow).ToList();
        var matches = new List<SavedWindowMatch>();
        foreach (var item in savedItems.Where(item => item.ItemType == ParcelItemType.ApplicationWindow))
        {
            var candidates = available.Select(window => (Window: window, Score: ScoreSavedItem(item, window)))
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Window.ProcessId)
                .ToList();
            if (candidates.Count == 0 || candidates[0].Score < 60)
            {
                matches.Add(new(item, null, candidates.FirstOrDefault().Score, 0, false, "No current top-level window met the saved identity threshold."));
                continue;
            }
            var best = candidates[0]; var second = candidates.Skip(1).Select(candidate => candidate.Score).FirstOrDefault(); var margin = best.Score - second;
            var ambiguous = candidates.Count > 1 && margin < 12;
            if (ambiguous)
            {
                matches.Add(new(item, null, best.Score, margin, true, "Multiple current windows have similarly strong saved identities; nothing was closed automatically."));
                continue;
            }
            available.Remove(best.Window);
            matches.Add(new(item, best.Window, best.Score, margin, false, ExplainSavedItem(item, best.Window)));
        }
        return matches;
    }

    public static IReadOnlyList<SavedWindowMatch> MatchSavedItems(IEnumerable<ParcelItem> savedItems, IEnumerable<ParcelItem> liveItems) =>
        MatchSavedItems(savedItems, liveItems.Where(item => item.ItemType == ParcelItemType.ApplicationWindow).Select(item =>
            new WindowCandidate(item.RuntimeWindowHandle, item.RuntimeProcessId, item.WindowTitle ?? item.DisplayName, item.ProcessName ?? string.Empty,
                item.ExecutablePath, item.DisplayName, true, false, item.WindowClassName)));

    private static IReadOnlyList<ParcelItem> Detect(Guid parcelId, CancellationToken cancellationToken)
    {
        var found = new List<ParcelItem>(); var seenHandles = new HashSet<nint>(); var ownPid = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            try
            {
                if (!NativeMethods.IsWindowVisible(window) || NativeMethods.GetWindowTextLength(window) <= 0) return true;
                var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64();
                var title = ReadTitle(window); if (title.Length == 0) return true;
                NativeMethods.GetWindowThreadProcessId(window, out var processId);
                var processName = string.Empty; string? executable = null; var friendly = string.Empty;
                try
                {
                    using var process = Process.GetProcessById((int)processId); processName = process.ProcessName; friendly = processName;
                    try { executable = process.MainModule?.FileName; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    if (executable is not null) { try { friendly = FileVersionInfo.GetVersionInfo(executable).FileDescription ?? processName; } catch (Exception exception) { AppLogger.LogTechnicalError(exception); } }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
                var candidate = new WindowCandidate(window, processId, title, processName, executable, friendly, true, (style & NativeMethods.WsExToolWindow) != 0, ReadClassName(window));
                if (!ShouldInclude(candidate, ownPid) || !seenHandles.Add(window)) return true;
                var normalizedExe = WindowsPathIdentity.Normalize(executable); var now = DateTime.Now;
                found.Add(new ParcelItem
                {
                    ParcelId = parcelId, ItemType = ParcelItemType.ApplicationWindow, DisplayName = title, Value = normalizedExe ?? processName,
                    NormalizedIdentity = $"{normalizedExe ?? processName}|{title}".ToUpperInvariant(), SecondaryDetail = $"{friendly} · {processName}", CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now,
                    SortOrder = found.Count, ExecutablePath = normalizedExe, WorkingDirectory = normalizedExe is null ? null : Path.GetDirectoryName(normalizedExe), WindowTitle = title, ProcessName = processName,
                    LaunchEnabled = normalizedExe is not null && File.Exists(normalizedExe), CloseSupported = true, RuntimeWindowHandle = window, RuntimeProcessId = processId, WindowClassName = candidate.WindowClassName, IsInaccessible = normalizedExe is null
                });
            }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
            return true;
        }, nint.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        return found.OrderBy(item => item.ProcessName).ThenBy(item => item.WindowTitle).ToList();
    }

    private static string ReadTitle(nint window)
    {
        var length = NativeMethods.GetWindowTextLength(window); if (length <= 0) return string.Empty;
        var text = new StringBuilder(Math.Min(length + 1, 4096)); NativeMethods.GetWindowText(window, text, text.Capacity); return text.ToString().Trim();
    }

    private static int ScoreSavedItem(ParcelItem item, WindowCandidate live)
    {
        if (!ShouldInclude(live, (uint)Environment.ProcessId)) return 0;
        var score = 0;
        var savedExecutable = WindowsPathIdentity.Normalize(item.ExecutablePath ?? item.Value);
        var liveExecutable = WindowsPathIdentity.Normalize(live.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(savedExecutable) && !string.IsNullOrWhiteSpace(liveExecutable) && string.Equals(savedExecutable, liveExecutable, StringComparison.OrdinalIgnoreCase)) score += 55;
        else if (!string.IsNullOrWhiteSpace(savedExecutable) && string.Equals(Path.GetFileName(savedExecutable), Path.GetFileName(liveExecutable), StringComparison.OrdinalIgnoreCase)) score += 45;
        if (!string.IsNullOrWhiteSpace(item.ProcessName) && string.Equals(item.ProcessName, live.ProcessName, StringComparison.OrdinalIgnoreCase)) score += 15;
        var savedTitle = DeskMemoryLogic.NormalizeTitle(item.WindowTitle ?? item.DisplayName); var liveTitle = DeskMemoryLogic.NormalizeTitle(live.Title);
        if (savedTitle.Length > 0 && savedTitle == liveTitle) score += 35;
        else if (savedTitle.Length > 0 && liveTitle.Length > 0 && savedTitle.Split(' ').Intersect(liveTitle.Split(' '), StringComparer.OrdinalIgnoreCase).Any()) score += 10;
        if (!string.IsNullOrWhiteSpace(item.WindowClassName) && string.Equals(item.WindowClassName, live.WindowClassName, StringComparison.OrdinalIgnoreCase)) score += 18;
        return score;
    }

    private static string ExplainSavedItem(ParcelItem item, WindowCandidate live)
    {
        var signals = new List<string>();
        if (string.Equals(WindowsPathIdentity.Normalize(item.ExecutablePath ?? item.Value), WindowsPathIdentity.Normalize(live.ExecutablePath), StringComparison.OrdinalIgnoreCase)) signals.Add("executable");
        if (string.Equals(item.ProcessName, live.ProcessName, StringComparison.OrdinalIgnoreCase)) signals.Add("process");
        if (DeskMemoryLogic.NormalizeTitle(item.WindowTitle ?? item.DisplayName) == DeskMemoryLogic.NormalizeTitle(live.Title)) signals.Add("title");
        if (!string.IsNullOrWhiteSpace(item.WindowClassName) && string.Equals(item.WindowClassName, live.WindowClassName, StringComparison.OrdinalIgnoreCase)) signals.Add("window class");
        return signals.Count == 0 ? "Matched by saved window identity." : $"Matched by {string.Join(", ", signals)}.";
    }

    private static string? ReadClassName(nint window)
    {
        var text = new StringBuilder(256);
        return NativeMethods.GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : null;
    }
}

public enum ItemOpenStatus { Opened, AlreadyOpen, Missing, Failed, Unsupported, Skipped }
public sealed record ItemOpenResult(Guid ItemId, ItemOpenStatus Status, string Message);

public sealed class ItemLaunchService
{
    public IReadOnlyList<ParcelItem> BuildPlan(IEnumerable<ParcelItem> selected)
    {
        var plan = new List<ParcelItem>(); var launchedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (item.IsMissing || item.IsInaccessible || !item.LaunchEnabled || item.ItemType == ParcelItemType.Note) continue;
            if (item.ItemType == ParcelItemType.Application)
            {
                var app = WindowsPathIdentity.Normalize(item.ExecutablePath ?? item.Value); if (app is null || !launchedApps.Add(app)) continue;
            }
            plan.Add(item);
        }
        return plan;
    }

    public async Task<IReadOnlyList<ItemOpenResult>> OpenAsync(IEnumerable<ParcelItem> selected, IEnumerable<ParcelItem>? currentlyOpen = null, CancellationToken cancellationToken = default)
    {
        var selectedItems = selected.ToList();
        var currentWindows = (currentlyOpen ?? Array.Empty<ParcelItem>()).Where(item => item.ItemType == ParcelItemType.ApplicationWindow).ToList();
        var alreadyOpen = selectedItems.Where(item =>
        {
            var identity = WindowsPathIdentity.Normalize(item.ExecutablePath ?? item.Value);
            if (identity is null) return false;
            if (item.ItemType == ParcelItemType.Application)
                return currentWindows.Any(open => string.Equals(identity, WindowsPathIdentity.Normalize(open.ExecutablePath ?? open.Value), StringComparison.OrdinalIgnoreCase));
            if (item.ItemType != ParcelItemType.ApplicationWindow) return false;
            return currentWindows.Any(open =>
                string.Equals(identity, WindowsPathIdentity.Normalize(open.ExecutablePath ?? open.Value), StringComparison.OrdinalIgnoreCase) &&
                DeskMemoryLogic.NormalizeTitle(item.WindowTitle ?? item.DisplayName) == DeskMemoryLogic.NormalizeTitle(open.WindowTitle ?? open.DisplayName));
        }).ToList();
        var plan = BuildPlan(selectedItems.Except(alreadyOpen)); var inPlan = plan.Select(x => x.Id).ToHashSet(); var results = alreadyOpen.Select(item => new ItemOpenResult(item.Id, ItemOpenStatus.AlreadyOpen, "Application is already open")).ToList();
        foreach (var item in selectedItems.Where(item => !inPlan.Contains(item.Id)))
            if (!alreadyOpen.Contains(item)) results.Add(new ItemOpenResult(item.Id, item.IsMissing ? ItemOpenStatus.Missing : item.IsInaccessible ? ItemOpenStatus.Failed : item.LaunchEnabled && item.ItemType != ParcelItemType.Note ? ItemOpenStatus.Skipped : ItemOpenStatus.Unsupported, item.AvailabilityLabel));
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = BuildStartInfo(item); Process.Start(info); results.Add(new ItemOpenResult(item.Id, ItemOpenStatus.Opened, "Open request sent"));
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
            {
                results.Add(new ItemOpenResult(item.Id, ItemOpenStatus.Failed, "Windows could not open this item"));
            }
            await Task.Delay(150, cancellationToken);
        }
        return results;
    }

    public static IReadOnlyDictionary<ItemOpenStatus, int> Summarize(IEnumerable<ItemOpenResult> results) => results.GroupBy(result => result.Status).ToDictionary(group => group.Key, group => group.Count());

    internal static ProcessStartInfo BuildStartInfo(ParcelItem item)
    {
        Uri? link = null;
        if (item.ItemType == ParcelItemType.WebLink && !WebLinkRules.TryNormalize(item.Value, out link)) throw new InvalidOperationException("Unsafe or invalid link.");
        var target = item.ItemType switch { ParcelItemType.WebLink => link!.AbsoluteUri, ParcelItemType.Application or ParcelItemType.ApplicationWindow => item.ExecutablePath ?? item.Value, _ => item.Value };
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("No open target is available.");
        var info = new ProcessStartInfo { FileName = target, UseShellExecute = true };
        if (item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow)
        {
            info.Arguments = item.LaunchArguments ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory)) info.WorkingDirectory = item.WorkingDirectory;
        }
        return info;
    }
}

public enum CloseRequestStatus { Requested, Closed, StillOpen, Unsupported, Failed }
public sealed record WindowCloseResult(Guid ItemId, CloseRequestStatus Status);
public interface IWindowCloseTransport
{
    bool IsWindow(nint handle);
    bool IsSafeTarget(ParcelItem item);
    bool PostClose(nint handle);
}

public sealed class WindowCloseRequestService
{
    private readonly IWindowCloseTransport _transport;
    private readonly TimeSpan _closeWait;

    public WindowCloseRequestService(IWindowCloseTransport? transport = null, TimeSpan? closeWait = null)
    {
        _transport = transport ?? new NativeWindowCloseTransport();
        _closeWait = closeWait ?? TimeSpan.FromMilliseconds(1400);
    }

    public static bool IsCloseCandidate(ParcelItem item) => item.ItemType == ParcelItemType.ApplicationWindow && item.CloseSupported && item.RuntimeWindowHandle != nint.Zero;
    public static bool IsSafeCloseTarget(ParcelItem item)
    {
        if (!IsCloseCandidate(item) || !NativeMethods.IsWindow(item.RuntimeWindowHandle)) return false;
        try
        {
            var handle = item.RuntimeWindowHandle;
            if (NativeMethods.GetAncestor(handle, NativeMethods.GetAncestorRoot) != handle || NativeMethods.GetWindow(handle, NativeMethods.GetWindowOwner) != nint.Zero) return false;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            if ((style & NativeMethods.WsExToolWindow) != 0) return false;
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId || item.RuntimeProcessId != 0 && item.RuntimeProcessId != processId) return false;
            using var process = Process.GetProcessById((int)processId);
            if (OpenWindowService.IsSystemNoise(process.ProcessName)) return false;
            if (!string.IsNullOrWhiteSpace(item.ExecutablePath))
            {
                string? executable = null;
                try { executable = process.MainModule?.FileName; } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
                if (executable is not null && !string.Equals(WindowsPathIdentity.Normalize(item.ExecutablePath), WindowsPathIdentity.Normalize(executable), StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<ParcelItem> BuildPlan(IEnumerable<ParcelItem> selected) => selected.Where(item => IsCloseCandidate(item) && _transport.IsWindow(item.RuntimeWindowHandle) && _transport.IsSafeTarget(item)).ToList();

    public async Task<IReadOnlyList<WindowCloseResult>> RequestCloseAsync(IEnumerable<ParcelItem> selected, CancellationToken cancellationToken = default)
    {
        var results = new List<WindowCloseResult>(); var pending = new List<ParcelItem>();
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ItemType != ParcelItemType.ApplicationWindow || !item.CloseSupported || item.RuntimeWindowHandle == nint.Zero) { results.Add(new(item.Id, CloseRequestStatus.Unsupported)); continue; }
            try
            {
                if (!_transport.IsWindow(item.RuntimeWindowHandle)) { results.Add(new(item.Id, CloseRequestStatus.Closed)); continue; }
                if (!_transport.IsSafeTarget(item)) { results.Add(new(item.Id, CloseRequestStatus.Unsupported)); continue; }
                if (!_transport.PostClose(item.RuntimeWindowHandle)) { results.Add(new(item.Id, CloseRequestStatus.Failed)); continue; }
                pending.Add(item);
            }
            catch (Exception exception) { AppLogger.LogTechnicalError(exception); results.Add(new(item.Id, CloseRequestStatus.Failed)); }
        }
        if (pending.Count > 0) await Task.Delay(_closeWait, cancellationToken);
        foreach (var item in pending) results.Add(new(item.Id, _transport.IsWindow(item.RuntimeWindowHandle) ? CloseRequestStatus.StillOpen : CloseRequestStatus.Closed));
        return results;
    }

    private sealed class NativeWindowCloseTransport : IWindowCloseTransport
    {
        public bool IsWindow(nint handle) => NativeMethods.IsWindow(handle);
        public bool IsSafeTarget(ParcelItem item) => IsSafeCloseTarget(item);
        public bool PostClose(nint handle) => NativeMethods.PostMessage(handle, NativeMethods.WmClose, nint.Zero, nint.Zero);
    }
}

internal static partial class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExTopmost = 0x00000008L;
    internal const uint WmClose = 0x0010;
    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint window, int index);
    internal static nint GetWindowLongPtr(nint window, int index) => nint.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
