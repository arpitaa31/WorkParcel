using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WorkParcel_App.Models;

namespace WorkParcel_App.Pages;

internal static class DeskPreviewBuilder
{
    private static readonly string[] Accents = { "#9BE28F", "#67C7FF", "#F0B45B", "#C9A7FF", "#FF8E8E", "#7DE2D1" };

    public static Border Build(DeskLayoutSnapshot? layout, double width = 720, double height = 270)
    {
        if (layout is null || layout.Monitors.Count == 0)
            return Ui.Card(Ui.Text("No monitor geometry has been captured yet.", 12, false, "#8D9CA2"), 14);

        var union = new DeskRect(
            layout.Monitors.Min(monitor => monitor.Bounds.Left),
            layout.Monitors.Min(monitor => monitor.Bounds.Top),
            layout.Monitors.Max(monitor => monitor.Bounds.Right) - layout.Monitors.Min(monitor => monitor.Bounds.Left),
            layout.Monitors.Max(monitor => monitor.Bounds.Bottom) - layout.Monitors.Min(monitor => monitor.Bounds.Top));
        if (!union.IsUsable) return Ui.Card(Ui.Text("The captured monitor geometry is not usable.", 12, false, "#F0B45B"), 14);

        const double padding = 12;
        var scale = Math.Min(Math.Max(100, width - padding * 2) / union.Width, Math.Max(100, height - padding * 2) / union.Height);
        scale = Math.Min(scale, 1.0);
        var canvas = new Canvas { Width = width, Height = height, Background = Ui.Resource("SubtleSurfaceBrush"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(canvas, $"Desk layout preview with {layout.Monitors.Count} monitor{(layout.Monitors.Count == 1 ? string.Empty : "s")} and {layout.Windows.Count} saved windows.");

        foreach (var monitor in layout.Monitors)
        {
            var rectangle = ScaleRect(monitor.Bounds, union, scale, padding);
            var label = Ui.Stack(2);
            label.Children.Add(Ui.Mono($"{(monitor.IsPrimary ? "PRIMARY · " : string.Empty)}{Shorten(monitor.FriendlyName, 28)}", 9, monitor.IsPrimary ? "#9BE28F" : "#C8D2D5", true));
            label.Children.Add(Ui.Mono($"{monitor.WorkArea.Width} × {monitor.WorkArea.Height} · {monitor.DpiX} DPI", 8, "#8D9CA2"));
            var border = new Border { Width = Math.Max(90, rectangle.Width), Height = Math.Max(70, rectangle.Height), BorderBrush = monitor.IsPrimary ? Ui.Resource("AccentBrush") : Ui.Resource("BorderBrush"), BorderThickness = new Thickness(monitor.IsPrimary ? 2 : 1), Background = Ui.Resource("SurfaceBrush"), Padding = new Thickness(6), Child = label };
            ToolTipService.SetToolTip(border, $"{monitor.FriendlyName}\n{monitor.Bounds.Left},{monitor.Bounds.Top} {monitor.Bounds.Width}×{monitor.Bounds.Height}\nWork area {monitor.WorkArea.Left},{monitor.WorkArea.Top} {monitor.WorkArea.Width}×{monitor.WorkArea.Height}");
            AutomationProperties.SetName(border, $"{(monitor.IsPrimary ? "Primary " : string.Empty)}monitor {monitor.FriendlyName}, {monitor.Bounds.Width} by {monitor.Bounds.Height}");
            Canvas.SetLeft(border, rectangle.Left); Canvas.SetTop(border, rectangle.Top); canvas.Children.Add(border);
        }

        foreach (var window in layout.Windows.Where(window => window.IsEnabled))
        {
            var rectangle = ScaleRect(window.AbsoluteBounds.IsUsable ? window.AbsoluteBounds : window.NormalBounds, union, scale, padding);
            var monitor = layout.Monitors.FirstOrDefault(candidate => candidate.Id == window.SavedMonitorId);
            var accent = Ui.Brush(Accents[Math.Abs(window.ExecutableIdentity.GetHashCode()) % Accents.Length]);
            var title = string.IsNullOrWhiteSpace(window.CapturedTitle) ? window.ProcessName : window.CapturedTitle;
            var text = Ui.Mono(Shorten(title, 24), 8, "#0B0E10", true);
            text.VerticalAlignment = VerticalAlignment.Center;
            var block = new Border { Width = Math.Max(42, Math.Min(rectangle.Width, 210)), Height = Math.Max(22, Math.Min(rectangle.Height, 70)), Background = accent, BorderBrush = Ui.Resource("PrimaryTextBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(4), Child = text, Opacity = .94 };
            var state = window.WindowState == DeskWindowState.Normal ? "normal" : window.WindowState.ToString().ToLowerInvariant();
            ToolTipService.SetToolTip(block, $"{title}\n{window.ProcessName} · {state}\nSaved on {(monitor?.FriendlyName ?? "missing monitor")}");
            AutomationProperties.SetName(block, $"Saved window {title}, {state}, {(monitor?.FriendlyName ?? "missing monitor")}");
            Canvas.SetLeft(block, Math.Clamp(rectangle.Left, 2, Math.Max(2, width - block.Width - 2))); Canvas.SetTop(block, Math.Clamp(rectangle.Top, 2, Math.Max(2, height - block.Height - 2))); canvas.Children.Add(block);
        }
        return new Border { Child = canvas, BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    public static TextBlock Summary(DeskLayoutSnapshot layout) =>
        Ui.Mono($"{layout.Monitors.Count} MONITOR{(layout.Monitors.Count == 1 ? string.Empty : "S")} · {layout.Windows.Count} WINDOW{(layout.Windows.Count == 1 ? string.Empty : "S")} · {layout.RestorableWindowCount} RESTORABLE · CAPTURED {layout.CaptureTimestamp:MMM d, yyyy h:mm tt}", 10, "#9BE28F", true);

    private static (double Left, double Top, double Width, double Height) ScaleRect(DeskRect rect, DeskRect union, double scale, double padding) =>
        (padding + (rect.Left - union.Left) * scale, padding + (rect.Top - union.Top) * scale, Math.Max(1, rect.Width * scale), Math.Max(1, rect.Height * scale));

    private static string Shorten(string value, int length) => string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Length <= length ? value : value[..Math.Max(1, length - 1)] + "…";
}
