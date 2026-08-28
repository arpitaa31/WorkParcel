using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public interface IDeskWindowProvider
{
    DeskSystemSnapshot GetSnapshot();
    bool TryGetWindow(nint handle, out DeskLiveWindow window);
    bool TryApplyPlacement(nint handle, DeskResolvedPlacement placement, out string error);
    bool IsWindow(nint handle);
}

/// <summary>
/// The only production boundary that talks to user32/display APIs. It keeps
/// transient HWND/PID values in memory and never exposes them to persistence.
/// </summary>
public sealed class NativeDeskWindowProvider : IDeskWindowProvider
{
    private static readonly HashSet<string> SystemProcessNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "sihost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "LockApp", "SystemSettings", "ApplicationFrameHost",
        "RuntimeBroker", "SearchApp", "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp"
    };

    public DeskSystemSnapshot GetSnapshot()
    {
        var monitors = EnumerateMonitors();
        var byNativeHandle = monitors.ToDictionary(monitor => monitor.NativeHandle);
        var windows = new List<DeskLiveWindow>();
        var dpiByDevice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rank = 0;
        NativeMethods.EnumWindows((handle, _) =>
        {
            var currentRank = rank++;
            try
            {
                if (!IsEligibleWindow(handle, out var processId, out var processName, out var executablePath, out var title)) return true;
                if (!NativeMethods.GetWindowRect(handle, out var windowRect)) return true;
                var monitorHandle = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
                if (!byNativeHandle.TryGetValue(monitorHandle, out var monitor))
                    monitor = monitors.FirstOrDefault(candidate => candidate.Info.IsPrimary) ?? monitors.FirstOrDefault();
                if (monitor is null) return true;

                var windowPlacement = new NativeMethods.WINDOWPLACEMENT { Length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
                var hasPlacement = NativeMethods.GetWindowPlacement(handle, ref windowPlacement);
                var state = hasPlacement ? ToWindowState(windowPlacement.ShowCommand) : DeskWindowState.Normal;
                var normalBounds = state == DeskWindowState.Normal
                    ? ToDeskRect(windowRect)
                    : hasPlacement ? NormalBoundsFromPlacement(windowPlacement, monitor.WorkArea, windowRect) : ToDeskRect(windowRect);
                if (!normalBounds.IsUsable) normalBounds = ToDeskRect(windowRect);
                var dpi = NativeMethods.GetDpiForWindow(handle);
                if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
                if (dpi > 0 && !dpiByDevice.ContainsKey(monitor.DeviceIdentifier)) dpiByDevice[monitor.DeviceIdentifier] = (int)dpi;
                var className = ReadClassName(handle);
                windows.Add(new DeskLiveWindow(
                    handle,
                    processId,
                    title,
                    DeskMemoryLogic.NormalizeTitle(title),
                    processName,
                    executablePath,
                    null,
                    className,
                    ToDeskRect(windowRect),
                    normalBounds,
                    state,
                    monitor.DeviceIdentifier,
                    (int)dpi,
                    (int)dpi,
                    true,
                    false,
                    currentRank));
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                AppLogger.LogTechnicalError(exception);
            }
            return true;
        }, nint.Zero);

        var orderedMonitors = monitors.OrderByDescending(monitor => monitor.Info.IsPrimary).ThenBy(monitor => monitor.Info.Bounds.Left).ThenBy(monitor => monitor.Info.Bounds.Top).ToList();
        var finalMonitors = orderedMonitors.Select((monitor, index) =>
        {
            var dpi = dpiByDevice.TryGetValue(monitor.DeviceIdentifier, out var observed) ? observed : monitor.Info.DpiX;
            return monitor.Info with { DpiX = dpi, DpiY = dpi, CaptureOrder = index };
        }).ToList();
        return new DeskSystemSnapshot(finalMonitors, windows);
    }

    public bool TryGetWindow(nint handle, out DeskLiveWindow window)
    {
        var found = GetSnapshot().Windows.FirstOrDefault(candidate => candidate.Handle == handle);
        if (found is null) { window = null!; return false; }
        window = found;
        return true;
    }

    public bool IsWindow(nint handle) => handle != nint.Zero && NativeMethods.IsWindow(handle);

    public bool TryApplyPlacement(nint handle, DeskResolvedPlacement placement, out string error)
    {
        error = string.Empty;
        if (handle == nint.Zero || !NativeMethods.IsWindow(handle))
        {
            error = "The target window is no longer available.";
            return false;
        }
        var bounds = placement.NormalBounds;
        if (!bounds.IsUsable || !placement.TargetMonitor.WorkArea.IsUsable)
        {
            error = "The saved geometry is not usable on the current monitor.";
            return false;
        }
        try
        {
            if (placement.State != DeskWindowState.Minimized && !NativeMethods.ShowWindow(handle, NativeMethods.ShowRestore))
            {
                // ShowWindow returns the previous visibility state, not an error.
                // SetWindowPos below is the operation whose last error we inspect.
            }
            var flags = NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosNoOwnerZOrder | NativeMethods.SetWindowPosShowWindow;
            if (!NativeMethods.SetWindowPos(handle, nint.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags))
            {
                var code = Marshal.GetLastWin32Error();
                error = code == NativeMethods.AccessDenied ? "Windows denied moving this window (it may be elevated)." : $"Windows could not move this window (error {code}).";
                return false;
            }

            if (placement.State == DeskWindowState.Maximized)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.ShowMaximize);
            }
            else if (placement.State == DeskWindowState.Minimized)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.ShowMinimize);
            }
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            error = exception is Win32Exception win32 && win32.NativeErrorCode == NativeMethods.AccessDenied
                ? "Windows denied moving this window (it may be elevated)."
                : "Windows could not apply the saved window placement.";
            AppLogger.LogTechnicalError(exception);
            return false;
        }
    }

    private static bool IsEligibleWindow(nint handle, out uint processId, out string processName, out string? executablePath, out string title)
    {
        processId = 0;
        processName = string.Empty;
        executablePath = null;
        title = string.Empty;
        if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.GetWindowTextLength(handle) <= 0) return false;
        if (NativeMethods.GetAncestor(handle, NativeMethods.GetAncestorRoot) != handle) return false;
        if (NativeMethods.GetWindow(handle, NativeMethods.GetWindowOwner) != nint.Zero) return false;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        if ((style & NativeMethods.WsExToolWindow) != 0) return false;
        NativeMethods.GetWindowThreadProcessId(handle, out processId);
                if (processId == (uint)Environment.ProcessId) return false;
        title = ReadWindowTitle(handle);
        if (title.Length == 0 || title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) || title.Equals("Windows Input Experience", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            if (SystemProcessNoise.Contains(processName)) return false;
            try { executablePath = process.MainModule?.FileName; }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
        return processName.Length > 0;
    }

    private static List<NativeMonitor> EnumerateMonitors()
    {
        var monitors = new List<NativeMonitor>();
        NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, (nint monitorHandle, nint hdc, ref NativeMethods.RECT clipRect, nint parameter) =>
        {
            try
            {
                var info = new NativeMethods.MONITORINFOEX { Size = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info)) return true;
                var device = info.DeviceName ?? string.Empty;
                var display = ReadDisplayDevice(device);
                var identifier = string.IsNullOrWhiteSpace(display.DeviceId) ? device : $"{device}|{display.DeviceId}";
                monitors.Add(new NativeMonitor(
                    monitorHandle,
                    new DeskMonitorInfo(identifier, string.IsNullOrWhiteSpace(display.FriendlyName) ? device : display.FriendlyName,
                        (info.Flags & NativeMethods.MonitorPrimary) != 0,
                        ToDeskRect(info.Monitor),
                        ToDeskRect(info.Work),
                        (int)Math.Max(1u, NativeMethods.GetDpiForSystem()),
                        (int)Math.Max(1u, NativeMethods.GetDpiForSystem()),
                        0,
                        monitors.Count)));
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                AppLogger.LogTechnicalError(exception);
            }
            return true;
        }, nint.Zero);
        return monitors.OrderByDescending(monitor => monitor.Info.IsPrimary).ThenBy(monitor => monitor.Info.Bounds.Left).ThenBy(monitor => monitor.Info.Bounds.Top).ToList();
    }

    private static (string DeviceId, string FriendlyName) ReadDisplayDevice(string deviceName)
    {
        var device = new NativeMethods.DISPLAY_DEVICE { Size = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        if (!NativeMethods.EnumDisplayDevices(deviceName, 0, ref device, 0)) return (string.Empty, string.Empty);
        return (device.DeviceId?.Trim() ?? string.Empty, device.DeviceString?.Trim() ?? string.Empty);
    }

    private static DeskRect NormalBoundsFromPlacement(NativeMethods.WINDOWPLACEMENT placement, DeskRect workArea, NativeMethods.RECT currentRect)
    {
        var normal = placement.NormalPosition;
        var width = normal.Right - normal.Left;
        var height = normal.Bottom - normal.Top;
        if (width <= 0 || height <= 0) return ToDeskRect(currentRect);
        // WINDOWPLACEMENT stores normal coordinates in workspace space for a
        // top-level window. Convert them to virtual-screen coordinates here;
        // these coordinates are not passed directly to SetWindowPos.
        return new(workArea.Left + normal.Left, workArea.Top + normal.Top, width, height);
    }

    private static DeskWindowState ToWindowState(int showCommand) => showCommand switch
    {
        NativeMethods.ShowMaximize => DeskWindowState.Maximized,
        NativeMethods.ShowMinimize or NativeMethods.ShowMinimizedMaximized => DeskWindowState.Minimized,
        _ => DeskWindowState.Normal
    };

    private static DeskRect ToDeskRect(NativeMethods.RECT rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static string ReadWindowTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var text = new StringBuilder(Math.Min(length + 1, 4096));
        NativeMethods.GetWindowText(handle, text, text.Capacity);
        return text.ToString().Trim();
    }

    private static string? ReadClassName(nint handle)
    {
        var text = new StringBuilder(256);
        return NativeMethods.GetClassName(handle, text, text.Capacity) > 0 ? text.ToString() : null;
    }

    private sealed record NativeMonitor(nint NativeHandle, DeskMonitorInfo Info)
    {
        public string DeviceIdentifier => Info.DeviceIdentifier;
        public DeskRect WorkArea => Info.WorkArea;
    }
}

internal static partial class NativeMethods
{
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint MonitorPrimary = 0x00000001;
    internal const uint GetAncestorRoot = 2;
    internal const uint GetWindowOwner = 4;
    internal const int ShowRestore = 9;
    internal const int ShowMinimize = 6;
    internal const int ShowMaximize = 3;
    internal const int ShowMinimizedMaximized = 7;
    internal const uint SetWindowPosNoActivate = 0x0010;
    internal const uint SetWindowPosNoOwnerZOrder = 0x0200;
    internal const uint SetWindowPosShowWindow = 0x0040;
    internal const int AccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int Size;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceKey;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(string? deviceName, uint deviceIndex, ref DISPLAY_DEVICE displayDevice, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);
}
