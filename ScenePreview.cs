using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Sada;

internal sealed class ScenePreview : Viewbox
{
    readonly App app;
    readonly Canvas desktop = new() { ClipToBounds = true };
    readonly Dictionary<WidgetKind, (Border Surface, WidgetView View)> widgets = new();

    public ScenePreview(App app)
    {
        this.app = app;
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        Child = desktop;
        Loaded += (_, _) => Apply();
        Apply();
    }

    public void Apply()
    {
        var scene = app.Scene;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == scene.MonitorName) ?? Forms.Screen.PrimaryScreen;
        var working = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = new Size(Math.Max(1, working.Width) / dpi.DpiScaleX, Math.Max(1, working.Height) / dpi.DpiScaleY);
        desktop.Width = area.Width;
        desktop.Height = area.Height;
        var enabled = scene.Widgets.Where(entry => entry.Value.Enabled).ToArray();
        foreach (var kind in widgets.Keys.Where(kind => !enabled.Any(entry => entry.Key == kind)).ToArray())
        {
            var old = widgets[kind];
            old.View.Release();
            desktop.Children.Remove(old.Surface);
            widgets.Remove(kind);
        }

        foreach (var (kind, options) in enabled)
        {
            if (!widgets.TryGetValue(kind, out var item))
            {
                var view = new WidgetView(app, kind, false);
                var panel = new Border { Child = view };
                item = (panel, view);
                widgets.Add(kind, item);
                desktop.Children.Add(panel);
            }
            bool fill = DesktopWidget.IsWallpaperSize(kind);
            var rect = fill ? new Rect(new Point(), area) :
                SceneLayout.Resolve(kind, options, area, new Point(working.X, working.Y), dpi.DpiScaleX, dpi.DpiScaleY);
            item.Surface.Width = rect.Width;
            item.Surface.Height = rect.Height;
            item.Surface.Opacity = Math.Clamp(options.Opacity, .08, 1);
            item.Surface.Background = fill ? Brushes.Transparent :
                new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(options.Surface * 239, 0, 239), 10, 21, 40));
            Canvas.SetLeft(item.Surface, rect.X);
            Canvas.SetTop(item.Surface, rect.Y);
            Canvas.SetZIndex(item.Surface, kind is WidgetKind.AlbumBackdrop or WidgetKind.Wallpaper ? -2 :
                kind == WidgetKind.Environment ? -1 : 0);
            item.View.Apply();
        }
    }

    public void Render(AudioFrame frame)
    {
        foreach (var item in widgets.Values) item.View.Render(frame, new Point(double.NaN, double.NaN));
    }

    public void RenderLyrics()
    {
        if (widgets.TryGetValue(WidgetKind.Lyrics, out var lyrics)) lyrics.View.RenderLyrics();
    }

    public void Release()
    {
        foreach (var item in widgets.Values) item.View.Release();
        widgets.Clear();
        desktop.Children.Clear();
    }
}
