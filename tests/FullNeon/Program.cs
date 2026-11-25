using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Sada;

internal static class Program
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    static void Main()
    {
        var app = new App();
        var scene = new SceneOptions();
        typeof(App).GetProperty("Prefs", Instance)!.SetValue(app, new Preferences { Scene = scene });
        var type = typeof(App).Assembly.GetType("Sada.FullNeonOverlay", true)!;
        var overlay = (Canvas)Activator.CreateInstance(type, Instance, null, [app], null)!;
        var apply = type.GetMethod("Apply", Instance)!;

        apply.Invoke(overlay, null);
        Check(overlay.Visibility == Visibility.Collapsed && overlay.Children.Count == 0,
            "Fullscreen neon is absent until its own setting is enabled");

        var desktop = scene.Widget(WidgetKind.Neon);
        desktop.RelativeLeft = .22;
        desktop.RelativeTop = .61;
        scene.FullNeon = true;
        apply.Invoke(overlay, null);
        var handle = (Thumb)overlay.Children[1];
        Layout(1280, 800);
        Check(!desktop.Enabled && overlay.Visibility == Visibility.Visible && Canvas.GetTop(handle) >= 80,
            "Fullscreen neon works independently and avoids the fullscreen exit control");

        scene.FullNeonX = .75;
        scene.FullNeonY = .45;
        apply.Invoke(overlay, null);
        Check(HasFractions(1280, 800, .75, .45), "Fullscreen position follows the saved choice");
        Layout(860, 520);
        Check(HasFractions(860, 520, .75, .45), "Saved placement scales with available fullscreen area");

        handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        var getArea = type.GetMethod("MovementBounds", Instance)!;
        var move = type.GetMethod("SetPosition", Instance)!;
        var area = (Rect)getArea.Invoke(overlay, null)!;
        move.Invoke(overlay, [new Point(area.Right + 50, area.Top - 50), area]);
        handle.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Check(scene.FullNeonX == 1 && scene.FullNeonY == 0, "Drag release stores clamped normalized position");
        Check(desktop.RelativeLeft == .22 && desktop.RelativeTop == .61, "Fullscreen movement leaves desktop coordinates intact");
        var copy = Preferences.Clone(scene);
        Check(copy.FullNeon && copy.FullNeonX == 1 && copy.FullNeonY == 0,
            "Fullscreen position round-trips with scene settings");

        scene.FullNeon = false;
        apply.Invoke(overlay, null);
        Check(overlay.Visibility == Visibility.Collapsed, "Fullscreen control can hide its overlay independently");

        void Layout(double width, double height)
        {
            overlay.Measure(new Size(width, height));
            overlay.Arrange(new Rect(0, 0, width, height));
            overlay.UpdateLayout();
        }

        bool HasFractions(double width, double height, double x, double y)
        {
            double expectedX = (width - handle.Width) * x;
            double expectedY = 80 + (height - handle.Height - 80) * y;
            return Math.Abs(Canvas.GetLeft(handle) - expectedX) < .01 && Math.Abs(Canvas.GetTop(handle) - expectedY) < .01;
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
