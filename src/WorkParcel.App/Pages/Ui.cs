using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WorkParcel_App.Models;
using WorkParcel_App.Services;

namespace WorkParcel_App.Pages;

internal static class Ui
{
    private static readonly ItemIconCacheService Icons = new();
    public static SolidColorBrush Brush(string hex) => new(ColorHelper.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)));
    public static Brush Resource(string key) => key switch
    {
        "SurfaceBrush" => Application.Current.Resources[key] as Brush ?? Brush("#111619"),
        "SubtleSurfaceBrush" => Application.Current.Resources[key] as Brush ?? Brush("#182024"),
        "BorderBrush" => Application.Current.Resources[key] as Brush ?? Brush("#2A353A"),
        "SecondaryTextBrush" => Application.Current.Resources[key] as Brush ?? Brush("#8D9CA2"),
        "AccentBrush" => Application.Current.Resources[key] as Brush ?? Brush("#9BE28F"),
        "AccentSoftBrush" => Application.Current.Resources[key] as Brush ?? Brush("#1D352C"),
        "WarningBrush" => Application.Current.Resources[key] as Brush ?? Brush("#F0B45B"),
        _ => Application.Current.Resources[key] as Brush ?? Brush("#E5EDF0")
    };
    public static TextBlock Text(string value, double size = 13, bool bold = false, string? color = null) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color is null ? Resource("PrimaryTextBrush") : Brush(color), TextWrapping = TextWrapping.Wrap };
    public static TextBlock Mono(string value, double size = 11, string? color = null, bool bold = false) => new() { Text = value, FontFamily = new FontFamily("Consolas"), FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color is null ? Resource("SecondaryTextBrush") : Brush(color), TextWrapping = TextWrapping.Wrap };
    public static StackPanel Stack(double spacing = 8, Orientation orientation = Orientation.Vertical) => new() { Spacing = spacing, Orientation = orientation };
    public static StackPanel Row(params UIElement[] children) { var row = Stack(9, Orientation.Horizontal); foreach (var child in children) row.Children.Add(child); return row; }
    public static Border Card(UIElement child, double padding = 14, double radius = 2) => new() { Child = child, Background = Resource("SurfaceBrush"), BorderBrush = Resource("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius), Padding = new Thickness(padding) };
    public static Border Rule() => new() { Height = 1, Background = Resource("BorderBrush"), HorizontalAlignment = HorizontalAlignment.Stretch };
    public static Border Spacer() => new() { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    public static Button Button(string label, bool primary = false) => InteractionMotion.Attach(new Button { Content = Mono(label, 11, primary ? "#0B0E10" : null, true), Background = primary ? Resource("AccentBrush") : Resource("SubtleSurfaceBrush"), Foreground = primary ? Brush("#0B0E10") : Resource("PrimaryTextBrush"), BorderBrush = primary ? Resource("AccentBrush") : Resource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(11, 7, 11, 7), CornerRadius = new CornerRadius(2), UseSystemFocusVisuals = true });
    public static Button LinkButton(string label) => InteractionMotion.Attach(new Button { Content = Text(label, 12, false, "#9BE28F"), Background = new SolidColorBrush(Colors.Transparent), Foreground = Brush("#9BE28F"), BorderThickness = new Thickness(0), Padding = new Thickness(3, 2, 3, 2), UseSystemFocusVisuals = true });
    public static Button IconButton(string glyph, string tooltip) { var button = Button(glyph); ToolTipService.SetToolTip(button, tooltip); return button; }
    public static Border Tag(string value, string color) => new() { Child = Mono(value, 10, color, true), BorderBrush = Brush(color), BorderThickness = new Thickness(1), Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(1) };
    public static Grid ItemIcon(ParcelItem item)
    {
        var label = item.ItemType switch { ParcelItemType.ApplicationWindow => "WIN", ParcelItemType.Application => "APP", ParcelItemType.File => "FILE", ParcelItemType.Folder => "DIR", ParcelItemType.WebLink => "URL", _ => "NOTE" };
        var text = Mono(label, 8, item.IsMissing ? "#F0B45B" : null, true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        var fallback = new Border { Width = 34, Height = 34, Background = Resource("SubtleSurfaceBrush"), BorderBrush = Resource("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Child = text };
        var image = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform, Opacity = 0 };
        var grid = new Grid { Width = 34, Height = 34 };
        grid.Children.Add(fallback);
        grid.Children.Add(image);
        _ = Icons.TrySetAsync(item, image);
        return grid;
    }
    public static T Interactive<T>(T element, float hoverScale = 1.008f) where T : FrameworkElement => InteractionMotion.Attach(element, hoverScale, .99f);
    public static async Task<ContentDialogResult> ShowDialog(ContentDialog dialog)
    {
        dialog.Opened += (_, _) => AnimateTree(dialog);
        return await dialog.ShowAsync();
    }
    private static void AnimateTree(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) InteractionMotion.Attach(button);
            else if (child is Control control && child is not TextBlock) InteractionMotion.Attach(control, 1.006f, .99f);
            AnimateTree(child);
        }
    }
    public static string StateLabel(ParcelStatus status) => status.ToString().ToUpperInvariant();
    public static string StateColor(ParcelStatus status) => status switch { ParcelStatus.Open => "#9BE28F", ParcelStatus.Packed => "#8D9CA2", _ => "#F0B45B" };
    public static string Relative(DateTime? value) { if (value is null) return "NEVER"; var delta = DateTime.Now - value.Value; if (delta.TotalMinutes < 2) return "JUST NOW"; if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}M AGO"; if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}H AGO"; return value.Value.ToString("MMM d").ToUpperInvariant(); }
    public static StackPanel SectionHeader(string title, string? subtitle = null) { var panel = Stack(3); panel.Children.Add(Mono(title, 11, null, true)); if (subtitle is not null) panel.Children.Add(Text(subtitle, 12, false, "#8D9CA2")); return panel; }
    public static ScrollViewer PageScroll(UIElement child) => new() { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(28, 24, 28, 34) };
}

public abstract class PageBase : Page
{
    protected readonly Services.WorkspaceStore Store = Services.WorkspaceStore.Current;
    protected StackPanel Body(double spacing = 16) => Ui.Stack(spacing);
    protected void SetContent(UIElement content) => Content = Ui.PageScroll(content);
    protected StackPanel Header(string title, string description, UIElement? action = null)
    {
        var row = Ui.Row(); row.HorizontalAlignment = HorizontalAlignment.Stretch;
        var copy = Ui.Stack(4); copy.Children.Add(Ui.Text(title, 26, true)); copy.Children.Add(Ui.Text(description, 12, false, "#8D9CA2")); row.Children.Add(copy);
        if (action is not null) { row.Children.Add(Ui.Spacer()); row.Children.Add(action); }
        return row;
    }
    protected void NavigateDetails(Parcel parcel) => Frame?.Navigate(typeof(ParcelDetailsPage), parcel.Id);
}
