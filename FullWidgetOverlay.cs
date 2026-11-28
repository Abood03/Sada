using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sada;

internal sealed class FullWidgetOverlay : Canvas
{
    readonly App app;
    readonly WidgetKind kind;
    readonly Thumb handle;
    WidgetView? sign;
    SceneOptions? dragScene;
    Point dragPointer;
    Point dragOrigin;

    internal FullWidgetOverlay(App app, WidgetKind kind)
    {
        this.app = app;
        this.kind = kind;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;

        var hitArea = new FrameworkElementFactory(typeof(Border));
        hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        handle = new Thumb
        {
            Cursor = Cursors.SizeAll,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = hitArea }
        };
        AutomationProperties.SetName(handle, $"Move full-screen {Dashboard.WidgetName(kind).ToLowerInvariant()}");
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
                var x = area.Width > 0 ? (position.X - area.Left) / area.Width : (double?)null;
                var y = area.Height > 0 ? (position.Y - area.Top) / area.Height : (double?)null;
                if (kind == WidgetKind.Neon)
                {
                    scene.FullNeonX = x ?? scene.FullNeonX;
                    scene.FullNeonY = y ?? scene.FullNeonY;
                }
                else
                {
                    scene.FullGlitchX = x ?? scene.FullGlitchX;
                    scene.FullGlitchY = y ?? scene.FullGlitchY;
                }
                app.QueueSave();
            }
            dragScene = null;
            Place();
        };
        SizeChanged += (_, _) => Place();
    }

    internal void Apply()
    {
        var enabled = kind == WidgetKind.Neon ? app.Scene.FullNeon : app.Scene.FullGlitchBox;
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            handle.CancelDrag();
            return;
        }
        if (sign is null)
        {
            sign = new WidgetView(app, kind, false) { IsHitTestVisible = false };
            Children.Add(sign);
            Children.Add(handle);
        }
        else sign.Apply();
        sign.Opacity = Math.Clamp(app.Scene.Widget(kind).Opacity, .08, 1);
        Place();
    }

    internal void Render(AudioFrame frame)
    {
        if (Visibility == Visibility.Visible && sign is not null)
        {
            sign.Render(frame, new Point(double.NaN, double.NaN));
            handle.IsHitTestVisible = sign.IsMediaWidgetVisible;
        }
    }

    internal void Release() => sign?.Release();

    void Place()
    {
        if (sign is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (kind == WidgetKind.Neon)
        {
            sign.Width = handle.Width = Math.Min(Math.Clamp(ActualWidth * .43, 280, 620), ActualWidth);
            sign.Height = handle.Height = Math.Min(Math.Clamp(ActualHeight * .18, 100, 185), ActualHeight);
        }
        else
        {
            var options = app.Scene.Widget(kind);
            double aspect = Math.Clamp(options.Width / Math.Max(1, options.Height), .55, 2.8);
            double width = Math.Min(Math.Clamp(ActualWidth * .20, 154, 350), ActualWidth);
            double height = Math.Min(width / aspect, Math.Min(ActualHeight * .45, 360));
            sign.Width = handle.Width = Math.Min(width, height * aspect);
            sign.Height = handle.Height = height;
        }
        var area = MovementBounds();
        if (ReferenceEquals(dragScene, app.Scene))
        {
            SetPosition(new Point(GetLeft(handle), GetTop(handle)), area);
            return;
        }

        var scene = app.Scene;
        var (x, y) = kind == WidgetKind.Neon
            ? (Fraction(scene.FullNeonX, .5), Fraction(scene.FullNeonY, .12))
            : (Fraction(scene.FullGlitchX, .12), Fraction(scene.FullGlitchY, .42));
        SetPosition(new Point(area.Left + area.Width * x, area.Top + area.Height * y), area);
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
