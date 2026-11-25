using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sada;

internal sealed class FullNeonOverlay : Canvas
{
    readonly App app;
    readonly Thumb handle;
    WidgetView? sign;
    SceneOptions? dragScene;
    Point dragPointer;
    Point dragOrigin;

    internal FullNeonOverlay(App app)
    {
        this.app = app;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;

        var hitArea = new FrameworkElementFactory(typeof(Border));
        hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        handle = new Thumb
        {
            Cursor = Cursors.SizeAll,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = hitArea }
        };
        AutomationProperties.SetName(handle, "Move full-screen neon sign");
        handle.DragStarted += (_, _) =>
        {
            dragScene = app.Scene;
            dragPointer = Mouse.GetPosition(this);
            dragOrigin = new Point(GetLeft(handle), GetTop(handle));
        };
        handle.DragDelta += (_, _) =>
        {
            if (!ReferenceEquals(dragScene, app.Scene)) return;
            SetPosition(dragOrigin + (Mouse.GetPosition(this) - dragPointer), MovementBounds());
        };
        handle.DragCompleted += (_, args) =>
        {
            var position = new Point(GetLeft(handle), GetTop(handle));
            if (!args.Canceled && dragScene is { } scene && ReferenceEquals(scene, app.Scene) && position != dragOrigin)
            {
                var area = MovementBounds();
                if (area.Width > 0) scene.FullNeonX = (position.X - area.Left) / area.Width;
                if (area.Height > 0) scene.FullNeonY = (position.Y - area.Top) / area.Height;
                app.QueueSave();
            }
            dragScene = null;
            Place();
        };
        SizeChanged += (_, _) => Place();
    }

    internal void Apply()
    {
        var enabled = app.Scene.FullNeon;
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            handle.CancelDrag();
            return;
        }
        if (sign is null)
        {
            sign = new WidgetView(app, WidgetKind.Neon, false) { IsHitTestVisible = false };
            Children.Add(sign);
            Children.Add(handle);
        }
        else sign.Apply();
        Place();
    }

    internal void Render(AudioFrame frame)
    {
        if (Visibility == Visibility.Visible && sign is not null)
            sign.Render(frame, new Point(double.NaN, double.NaN));
    }

    internal void Release() => sign?.Release();

    void Place()
    {
        if (sign is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        sign.Width = handle.Width = Math.Min(Math.Clamp(ActualWidth * .43, 280, 620), ActualWidth);
        sign.Height = handle.Height = Math.Min(Math.Clamp(ActualHeight * .18, 100, 185), ActualHeight);
        var area = MovementBounds();
        if (ReferenceEquals(dragScene, app.Scene))
        {
            SetPosition(new Point(GetLeft(handle), GetTop(handle)), area);
            return;
        }

        var scene = app.Scene;
        SetPosition(new Point(area.Left + area.Width * Fraction(scene.FullNeonX, .5),
            area.Top + area.Height * Fraction(scene.FullNeonY, .12)), area);
    }

    Rect MovementBounds()
    {
        double horizontal = Math.Max(0, ActualWidth - handle.Width);
        double vertical = Math.Max(0, ActualHeight - handle.Height);
        double top = Math.Min(80, vertical);
        return new Rect(0, top, horizontal, vertical - top);
    }

    void SetPosition(Point position, Rect area)
    {
        if (sign is null) return;
        double x = Math.Clamp(position.X, area.Left, area.Right);
        double y = Math.Clamp(position.Y, area.Top, area.Bottom);
        SetLeft(sign, x);
        SetTop(sign, y);
        SetLeft(handle, x);
        SetTop(handle, y);
    }

    static double Fraction(double? value, double fallback) =>
        value is double fraction && double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : fallback;
}
