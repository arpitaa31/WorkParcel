using System.Globalization;
using System.Text;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

/// <summary>
/// Pure Desk Memory rules. This class deliberately knows nothing about HWNDs,
/// WinUI, the database, or the process that owns a window so it can be tested
/// with deterministic fake monitors and windows.
/// </summary>
public static class DeskMemoryLogic
{
    public const int DefaultMinimumWindowWidth = 320;
    public const int DefaultMinimumWindowHeight = 180;

    public static string NormalizeTitle(string? title)
    {
        var value = NormalizeWhitespace(title);
        foreach (var suffix in new[] { " (administrator)", " [administrator]", " - administrator" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length].TrimEnd();
                break;
            }
        }
        return value.ToUpperInvariant();
    }

    public static string NormalizeExecutable(string? executable, string? processName = null)
    {
        var path = WindowsPathIdentity.Normalize(executable);
        if (!string.IsNullOrWhiteSpace(path)) return path.ToUpperInvariant();
        return NormalizeWhitespace(processName).ToUpperInvariant();
    }

    public static string BuildTopologySignature(IEnumerable<DeskMonitorInfo> monitors)
    {
        var ordered = monitors.OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Bounds.Left)
            .ThenBy(monitor => monitor.Bounds.Top)
            .ThenBy(monitor => monitor.DeviceIdentifier, StringComparer.OrdinalIgnoreCase);
        return string.Join("|", ordered.Select(monitor => string.Join(
            ":",
            monitor.DeviceIdentifier,
            monitor.Bounds.Left,
            monitor.Bounds.Top,
            monitor.Bounds.Width,
            monitor.Bounds.Height,
            monitor.WorkArea.Left,
            monitor.WorkArea.Top,
            monitor.WorkArea.Width,
            monitor.WorkArea.Height,
            monitor.DpiX,
            monitor.DpiY,
            monitor.Orientation,
            monitor.IsPrimary ? "P" : "S")));
    }

    public static DeskLayoutCaptureResult BuildCapture(
        Guid parcelId,
        IReadOnlyList<ParcelItem> selectedItems,
        DeskSystemSnapshot system,
        DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;
        var snapshot = new DeskLayoutSnapshot
        {
            Id = Guid.NewGuid(),
            ParcelId = parcelId,
            Name = "Desk Memory",
            CreatedAt = now,
            UpdatedAt = now,
            IsCurrent = true,
            IsEnabled = true,
            TopologySignature = BuildTopologySignature(system.Monitors)
        };

        var monitorByDevice = new Dictionary<string, DeskMonitorLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in system.Monitors.OrderByDescending(monitor => monitor.IsPrimary).ThenBy(monitor => monitor.CaptureOrder))
        {
            var saved = new DeskMonitorLayout
            {
                Id = Guid.NewGuid(),
                LayoutSnapshotId = snapshot.Id,
                ParcelId = parcelId,
                DeviceIdentifier = monitor.DeviceIdentifier,
                FriendlyName = monitor.FriendlyName,
                IsPrimary = monitor.IsPrimary,
                Bounds = monitor.Bounds,
                WorkArea = monitor.WorkArea,
                RelativeArrangement = BuildRelativeArrangement(monitor.Bounds, system.Monitors),
                DpiX = Math.Max(1, monitor.DpiX),
                DpiY = Math.Max(1, monitor.DpiY),
                Orientation = monitor.Orientation,
                CaptureOrder = monitor.CaptureOrder,
                CreatedAt = now
            };
            snapshot.Monitors.Add(saved);
            if (!string.IsNullOrWhiteSpace(saved.DeviceIdentifier)) monitorByDevice[saved.DeviceIdentifier] = saved;
        }

        var failures = new List<DeskGeometryCaptureFailure>();
        var liveByHandle = system.Windows.ToDictionary(window => window.Handle);
        foreach (var item in selectedItems.Where(item => item.ItemType == ParcelItemType.ApplicationWindow))
        {
            if (item.RuntimeWindowHandle == nint.Zero || !liveByHandle.TryGetValue(item.RuntimeWindowHandle, out var live))
            {
                failures.Add(new(item.Id, item.DisplayName, "The window is no longer available."));
                continue;
            }
            if (!live.Visible || live.ToolWindow || !live.Bounds.IsUsable)
            {
                failures.Add(new(item.Id, item.DisplayName, "Windows did not return usable visible geometry."));
                continue;
            }
            if (!monitorByDevice.TryGetValue(live.MonitorDeviceIdentifier, out var monitor) || !monitor.WorkArea.IsUsable)
            {
                failures.Add(new(item.Id, item.DisplayName, "The window is not on a captured monitor."));
                continue;
            }

            var normal = live.NormalBounds.IsUsable ? live.NormalBounds : live.Bounds;
            var relative = RelativeGeometry(normal, monitor.WorkArea);
            var executable = NormalizeExecutable(live.ExecutablePath ?? item.ExecutablePath ?? item.Value, live.ProcessName);
            var savedWindow = new DeskWindowLayout
            {
                Id = Guid.NewGuid(),
                LayoutSnapshotId = snapshot.Id,
                ParcelId = parcelId,
                ParcelItemId = item.Id,
                ExecutableIdentity = executable,
                ApplicationIdentifier = live.ApplicationIdentifier ?? item.ApplicationUserModelId,
                ProcessName = NormalizeWhitespace(live.ProcessName),
                CapturedTitle = live.Title,
                NormalizedTitle = NormalizeTitle(live.Title),
                WindowClassName = NormalizeWhitespaceOrNull(live.WindowClassName),
                SavedMonitorId = monitor.Id,
                AbsoluteBounds = live.Bounds,
                NormalBounds = normal,
                RelativeLeft = relative.Left,
                RelativeTop = relative.Top,
                RelativeWidth = relative.Width,
                RelativeHeight = relative.Height,
                WindowState = live.WindowState,
                ZOrderRank = live.ZOrderRank,
                SourceDpiX = Math.Max(1, live.DpiX),
                SourceDpiY = Math.Max(1, live.DpiY),
                IsEnabled = true,
                IsSupported = !string.IsNullOrWhiteSpace(executable) && normal.IsUsable,
                MatchMetadata = BuildMatchMetadata(item, live),
                LastMatchConfidence = DeskMatchConfidence.Exact,
                CreatedAt = now,
                UpdatedAt = now
            };
            if (!savedWindow.IsSupported)
            {
                failures.Add(new(item.Id, item.DisplayName, "The window has no stable executable identity."));
                savedWindow.IsSupported = false;
            }
            snapshot.Windows.Add(savedWindow);
        }

        return new DeskLayoutCaptureResult { Snapshot = snapshot, Failures = failures };
    }

    public static DeskTopologyComparison CompareTopology(
        IReadOnlyList<DeskMonitorLayout> saved,
        IReadOnlyList<DeskMonitorInfo> current)
    {
        if (saved.Count == 0 || current.Count == 0) return DeskTopologyComparison.SignificantlyDifferent;
        var savedIds = saved.Select(monitor => monitor.DeviceIdentifier).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentIds = current.Select(monitor => monitor.DeviceIdentifier).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = savedIds.Except(currentIds, StringComparer.OrdinalIgnoreCase).Any();
        var added = currentIds.Except(savedIds, StringComparer.OrdinalIgnoreCase).Any();
        var common = saved.Where(monitor => current.FirstOrDefault(candidate => string.Equals(candidate.DeviceIdentifier, monitor.DeviceIdentifier, StringComparison.OrdinalIgnoreCase)) is not null).ToList();
        if (!missing && !added && common.Count == saved.Count && common.All(monitor =>
        {
            var currentMonitor = current.First(candidate => string.Equals(candidate.DeviceIdentifier, monitor.DeviceIdentifier, StringComparison.OrdinalIgnoreCase));
            return monitor.Bounds == currentMonitor.Bounds && monitor.WorkArea == currentMonitor.WorkArea &&
                   Math.Abs(monitor.DpiX - currentMonitor.DpiX) <= 1 && Math.Abs(monitor.DpiY - currentMonitor.DpiY) <= 1 &&
                   monitor.Orientation == currentMonitor.Orientation;
        })) return DeskTopologyComparison.Exact;
        if (missing && !added) return DeskTopologyComparison.MissingMonitor;
        if (added && !missing) return DeskTopologyComparison.NewMonitor;
        if (common.Count > 0) return DeskTopologyComparison.CompatibleChanged;
        return DeskTopologyComparison.SignificantlyDifferent;
    }

    public static IReadOnlyList<DeskMonitorMapping> MapMonitors(
        IReadOnlyList<DeskMonitorLayout> saved,
        IReadOnlyList<DeskMonitorInfo> current)
    {
        var mappings = new List<DeskMonitorMapping>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldMonitor in saved.OrderByDescending(monitor => monitor.IsPrimary).ThenBy(monitor => monitor.CaptureOrder))
        {
            var exact = current.FirstOrDefault(monitor => !used.Contains(monitor.DeviceIdentifier) && string.Equals(monitor.DeviceIdentifier, oldMonitor.DeviceIdentifier, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                used.Add(exact.DeviceIdentifier);
                mappings.Add(new(oldMonitor, exact, true, "Same Windows display identity."));
                continue;
            }

            var candidates = current.Where(monitor => !used.Contains(monitor.DeviceIdentifier)).ToList();
            var role = candidates.Where(monitor => monitor.IsPrimary == oldMonitor.IsPrimary).ToList();
            var pool = role.Count > 0 ? role : candidates;
            var nearest = pool.OrderBy(monitor => MonitorDistance(oldMonitor, monitor)).ThenByDescending(monitor => monitor.IsPrimary).FirstOrDefault();
            if (nearest is not null)
            {
                used.Add(nearest.DeviceIdentifier);
                mappings.Add(new(oldMonitor, nearest, false, oldMonitor.IsPrimary == nearest.IsPrimary
                    ? "Display identity changed; used the nearest equivalent monitor."
                    : "Original monitor is unavailable; used the available fallback monitor."));
                continue;
            }

            var fallback = current.FirstOrDefault(monitor => monitor.IsPrimary) ?? current.FirstOrDefault();
            mappings.Add(new(oldMonitor, fallback, false, fallback is null
                ? "No current monitor is available."
                : "No one-to-one monitor match remained; used the primary monitor."));
        }
        return mappings;
    }

    public static DeskWindowMatch MatchWindow(DeskWindowLayout saved, IEnumerable<DeskLiveWindow> liveWindows)
    {
        var candidates = liveWindows.Select(live => (Live: live, Score: ScoreWindow(saved, live), Explanation: ExplainScore(saved, live)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Live.ZOrderRank)
            .ToList();
        if (string.IsNullOrWhiteSpace(saved.ExecutableIdentity) && string.IsNullOrWhiteSpace(saved.ProcessName))
            return new(saved, null, 0, 0, DeskMatchConfidence.Unsupported, "The saved window has no stable executable or process identity.");
        if (candidates.Count == 0 || candidates[0].Score < 45)
            return new(saved, null, candidates.Count == 0 ? 0 : candidates[0].Score, 0, DeskMatchConfidence.NoMatch, "No current window met the minimum identity score.");

        var best = candidates[0];
        var second = candidates.Skip(1).Select(candidate => candidate.Score).FirstOrDefault();
        var margin = best.Score - second;
        var confidence = margin < 10 && candidates.Count > 1
            ? DeskMatchConfidence.Ambiguous
            : best.Score >= 78 ? DeskMatchConfidence.Exact
            : best.Score >= 62 ? DeskMatchConfidence.High
            : DeskMatchConfidence.Possible;
        return new(saved, best.Live, best.Score, margin, confidence, best.Explanation + (confidence == DeskMatchConfidence.Ambiguous ? " Multiple current windows are similarly plausible." : string.Empty));
    }

    public static DeskRestorePlan BuildPlan(
        DeskLayoutSnapshot layout,
        DeskSystemSnapshot current,
        ISet<Guid>? allowedParcelItemIds = null)
    {
        var savedMonitors = layout.Monitors;
        var mappings = MapMonitors(savedMonitors, current.Monitors);
        var topology = CompareTopology(savedMonitors, current.Monitors);
        var usedHandles = new HashSet<nint>();
        var items = new List<DeskRestorePlanItem>();
        foreach (var saved in layout.Windows.Where(window => window.IsEnabled && window.IsSupported && (allowedParcelItemIds is null || savedParcelItemIdsContains(window, allowedParcelItemIds))))
        {
            var available = current.Windows.Where(window => !usedHandles.Contains(window.Handle));
            var match = MatchWindow(saved, available);
            DeskResolvedPlacement? placement = null;
            DeskRestoreResultKind? preflight = null;
            if (match.Live is null)
            {
                preflight = match.Confidence == DeskMatchConfidence.Unsupported ? DeskRestoreResultKind.Unsupported : DeskRestoreResultKind.Skipped;
            }
            else if (match.Confidence is DeskMatchConfidence.Ambiguous or DeskMatchConfidence.Possible)
            {
                preflight = DeskRestoreResultKind.Ambiguous;
            }
            else
            {
                usedHandles.Add(match.Live!.Handle);
                var mapping = mappings.FirstOrDefault(candidate => candidate.Saved.Id == saved.SavedMonitorId);
                if (mapping is null || mapping.Current is null)
                {
                    preflight = DeskRestoreResultKind.Failed;
                }
                else
                {
                    placement = ResolvePlacement(saved, mapping, match.Live!.DpiX, true, true);
                }
            }
            items.Add(new(saved, match, placement, preflight));
        }
        return new DeskRestorePlan { Topology = topology, MonitorMappings = mappings, Items = items };
    }

    public static DeskResolvedPlacement ResolvePlacement(
        DeskWindowLayout saved,
        DeskMonitorMapping mapping,
        int targetDpiX,
        bool restoreMaximized,
        bool restoreMinimized,
        int minimumWidth = DefaultMinimumWindowWidth,
        int minimumHeight = DefaultMinimumWindowHeight)
    {
        var monitor = mapping.Current ?? throw new InvalidOperationException("No monitor is available for the saved window.");
        var sourceDpi = Math.Max(1, saved.SourceDpiX);
        var dpiChanged = Math.Abs(Math.Max(1, targetDpiX) - sourceDpi) > 1;
        var useScaled = !mapping.IsExact || dpiChanged;
        DeskRect desired;
        if (!useScaled && saved.NormalBounds.IsUsable) desired = saved.NormalBounds;
        else
        {
            var relative = saved.RelativeWidth > 0 && saved.RelativeHeight > 0
                ? new DeskRect(
                    monitor.WorkArea.Left + Convert.ToInt32(Math.Round(saved.RelativeLeft * monitor.WorkArea.Width)),
                    monitor.WorkArea.Top + Convert.ToInt32(Math.Round(saved.RelativeTop * monitor.WorkArea.Height)),
                    Convert.ToInt32(Math.Round(saved.RelativeWidth * monitor.WorkArea.Width)),
                    Convert.ToInt32(Math.Round(saved.RelativeHeight * monitor.WorkArea.Height)))
                : ScaleFromSource(saved.NormalBounds, saved.SourceDpiX, targetDpiX, monitor.WorkArea);
            desired = relative;
        }
        var safe = ClampToWorkArea(desired, monitor.WorkArea, minimumWidth, minimumHeight);
        var state = saved.WindowState switch
        {
            DeskWindowState.Maximized when restoreMaximized => DeskWindowState.Maximized,
            DeskWindowState.Minimized when restoreMinimized => DeskWindowState.Minimized,
            _ => DeskWindowState.Normal
        };
        return new(safe, state, monitor, useScaled, !mapping.IsExact);
    }

    public static DeskRect ClampToWorkArea(DeskRect desired, DeskRect workArea, int minimumWidth = DefaultMinimumWindowWidth, int minimumHeight = DefaultMinimumWindowHeight)
    {
        if (!workArea.IsUsable) return desired;
        var width = Math.Clamp(desired.Width, Math.Min(minimumWidth, workArea.Width), workArea.Width);
        var height = Math.Clamp(desired.Height, Math.Min(minimumHeight, workArea.Height), workArea.Height);
        var left = Math.Clamp(desired.Left, workArea.Left - width + Math.Min(48, width), workArea.Right - Math.Min(48, width));
        var top = Math.Clamp(desired.Top, workArea.Top - height + Math.Min(32, height), workArea.Bottom - Math.Min(32, height));
        return new(left, top, width, height);
    }

    public static (double Left, double Top, double Width, double Height) RelativeGeometry(DeskRect bounds, DeskRect workArea)
    {
        if (!workArea.IsUsable) return (0, 0, 1, 1);
        return (
            Math.Clamp((double)(bounds.Left - workArea.Left) / workArea.Width, -1, 1),
            Math.Clamp((double)(bounds.Top - workArea.Top) / workArea.Height, -1, 1),
            Math.Clamp((double)bounds.Width / workArea.Width, 0, 1),
            Math.Clamp((double)bounds.Height / workArea.Height, 0, 1));
    }

    public static int ScoreWindow(DeskWindowLayout saved, DeskLiveWindow live)
    {
        if (!live.Visible || live.ToolWindow) return 0;
        var score = 0;
        if (SameExecutable(saved.ExecutableIdentity, live.ExecutablePath, live.ProcessName)) score += 45;
        if (!string.IsNullOrWhiteSpace(saved.ProcessName) && string.Equals(saved.ProcessName, live.ProcessName, StringComparison.OrdinalIgnoreCase)) score += 10;
        if (!string.IsNullOrWhiteSpace(saved.ApplicationIdentifier) && string.Equals(saved.ApplicationIdentifier, live.ApplicationIdentifier, StringComparison.OrdinalIgnoreCase)) score += 15;
        if (!string.IsNullOrWhiteSpace(saved.WindowClassName) && string.Equals(saved.WindowClassName, live.WindowClassName, StringComparison.OrdinalIgnoreCase)) score += 12;
        var savedTitle = string.IsNullOrWhiteSpace(saved.NormalizedTitle) ? NormalizeTitle(saved.CapturedTitle) : saved.NormalizedTitle;
        var liveTitle = string.IsNullOrWhiteSpace(live.NormalizedTitle) ? NormalizeTitle(live.Title) : live.NormalizedTitle;
        if (savedTitle.Length > 0 && savedTitle == liveTitle) score += 35;
        else if (savedTitle.Length > 0 && liveTitle.Length > 0) score += TokenSimilarity(savedTitle, liveTitle) >= 0.5 ? 16 : 0;
        if (saved.ZOrderRank >= 0 && live.ZOrderRank >= 0 && Math.Abs(saved.ZOrderRank - live.ZOrderRank) <= 1) score += 2;
        return score;
    }

    public static string ExplainScore(DeskWindowLayout saved, DeskLiveWindow live)
    {
        var signals = new List<string>();
        if (SameExecutable(saved.ExecutableIdentity, live.ExecutablePath, live.ProcessName)) signals.Add("executable");
        if (string.Equals(saved.ProcessName, live.ProcessName, StringComparison.OrdinalIgnoreCase)) signals.Add("process");
        if (!string.IsNullOrWhiteSpace(saved.ApplicationIdentifier) && string.Equals(saved.ApplicationIdentifier, live.ApplicationIdentifier, StringComparison.OrdinalIgnoreCase)) signals.Add("application id");
        if (!string.IsNullOrWhiteSpace(saved.WindowClassName) && string.Equals(saved.WindowClassName, live.WindowClassName, StringComparison.OrdinalIgnoreCase)) signals.Add("window class");
        if (NormalizeTitle(saved.CapturedTitle) == NormalizeTitle(live.Title)) signals.Add("title");
        return signals.Count == 0 ? "Only weak or missing identity signals were available." : $"Matched by {string.Join(", ", signals)}.";
    }

    private static bool savedParcelItemIdsContains(DeskWindowLayout window, ISet<Guid> ids) => window.ParcelItemId.HasValue && ids.Contains(window.ParcelItemId.Value);

    private static string BuildRelativeArrangement(DeskRect bounds, IReadOnlyList<DeskMonitorInfo> monitors)
    {
        if (monitors.Count == 0) return string.Empty;
        var union = new DeskRect(
            monitors.Min(monitor => monitor.Bounds.Left),
            monitors.Min(monitor => monitor.Bounds.Top),
            monitors.Max(monitor => monitor.Bounds.Right) - monitors.Min(monitor => monitor.Bounds.Left),
            monitors.Max(monitor => monitor.Bounds.Bottom) - monitors.Min(monitor => monitor.Bounds.Top));
        if (!union.IsUsable) return string.Empty;
        return string.Join(",", new[] {
            ((double)(bounds.Left - union.Left) / union.Width).ToString("0.####", CultureInfo.InvariantCulture),
            ((double)(bounds.Top - union.Top) / union.Height).ToString("0.####", CultureInfo.InvariantCulture),
            ((double)bounds.Width / union.Width).ToString("0.####", CultureInfo.InvariantCulture),
            ((double)bounds.Height / union.Height).ToString("0.####", CultureInfo.InvariantCulture)
        });
    }

    private static string BuildMatchMetadata(ParcelItem item, DeskLiveWindow live) =>
        $"item={item.Id:D};process={live.ProcessName};class={live.WindowClassName};captured={DateTime.UtcNow:O}";

    private static double MonitorDistance(DeskMonitorLayout oldMonitor, DeskMonitorInfo current)
    {
        var oldWidth = Math.Max(1, oldMonitor.Bounds.Width);
        var oldHeight = Math.Max(1, oldMonitor.Bounds.Height);
        var size = Math.Abs(oldWidth - current.Bounds.Width) / (double)oldWidth + Math.Abs(oldHeight - current.Bounds.Height) / (double)oldHeight;
        var center = Math.Abs(oldMonitor.Bounds.CenterX - current.Bounds.CenterX) + Math.Abs(oldMonitor.Bounds.CenterY - current.Bounds.CenterY);
        var dpi = Math.Abs(oldMonitor.DpiX - current.DpiX) / 96d + Math.Abs(oldMonitor.DpiY - current.DpiY) / 96d;
        return size * 10000 + center + dpi * 1000;
    }

    private static bool SameExecutable(string savedIdentity, string? liveExecutable, string liveProcess)
    {
        if (string.IsNullOrWhiteSpace(savedIdentity)) return false;
        var liveIdentity = NormalizeExecutable(liveExecutable, liveProcess);
        return string.Equals(savedIdentity, liveIdentity, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(savedIdentity), Path.GetFileName(liveIdentity), StringComparison.OrdinalIgnoreCase);
    }

    private static DeskRect ScaleFromSource(DeskRect bounds, int sourceDpi, int targetDpi, DeskRect workArea)
    {
        var scale = Math.Max(1, targetDpi) / (double)Math.Max(1, sourceDpi);
        return new(
            workArea.Left + Convert.ToInt32(Math.Round((bounds.Left - workArea.Left) * scale)),
            workArea.Top + Convert.ToInt32(Math.Round((bounds.Top - workArea.Top) * scale)),
            Convert.ToInt32(Math.Round(bounds.Width * scale)),
            Convert.ToInt32(Math.Round(bounds.Height * scale)));
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var wasWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!wasWhitespace) builder.Append(' ');
                wasWhitespace = true;
            }
            else
            {
                builder.Append(character);
                wasWhitespace = false;
            }
        }
        return builder.ToString();
    }

    private static string? NormalizeWhitespaceOrNull(string? value)
    {
        var normalized = NormalizeWhitespace(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static double TokenSimilarity(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (a.Count == 0 || b.Count == 0) return 0;
        return a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count() / (double)a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
    }
}
