using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace WorkParcel_App.Services;

internal static class InteractionMotion
{
    private static readonly bool AnimationsEnabled = ReadAnimationSetting();
    private static readonly ConditionalWeakTable<FrameworkElement, MotionMarker> Attached = new();

    public static T Attach<T>(T element, float hoverScale = 1.025f, float pressedScale = 0.98f) where T : FrameworkElement
    {
        if (!AnimationsEnabled || Attached.TryGetValue(element, out _)) return element;
        var visual = ElementCompositionPreview.GetElementVisual(element); Attached.Add(element, new MotionMarker());
        void Center(object? _, SizeChangedEventArgs args) => visual.CenterPoint = new Vector3((float)args.NewSize.Width / 2, (float)args.NewSize.Height / 2, 0);
        void Hover(object? _, PointerRoutedEventArgs __) { if (CanMove(element)) Scale(visual, hoverScale, 130); }
        void Leave(object? _, PointerRoutedEventArgs __) => Scale(visual, 1, 130);
        void Press(object? _, PointerRoutedEventArgs __) { if (CanMove(element)) Scale(visual, pressedScale, 75); }
        void Release(object? _, PointerRoutedEventArgs __) { if (CanMove(element)) Scale(visual, hoverScale, 90); }
        void Unloaded(object? _, RoutedEventArgs __)
        {
            element.SizeChanged -= Center; element.PointerEntered -= Hover; element.PointerExited -= Leave; element.PointerPressed -= Press; element.PointerReleased -= Release; element.PointerCanceled -= Leave; element.Unloaded -= Unloaded; Attached.Remove(element);
        }
        element.SizeChanged += Center; element.PointerEntered += Hover; element.PointerExited += Leave; element.PointerPressed += Press; element.PointerReleased += Release; element.PointerCanceled += Leave; element.Unloaded += Unloaded;
        return element;
    }

    private static bool CanMove(FrameworkElement element) => element is not Control control || control.IsEnabled;

    private static void Scale(Visual visual, float target, int milliseconds)
    {
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(target, target, 1), visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, 0), new Vector2(0, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds); visual.StartAnimation(nameof(visual.Scale), animation);
    }

    private static bool ReadAnimationSetting()
    {
        try { return new UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    private sealed class MotionMarker;
}
