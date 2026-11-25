using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sada;

internal static class Program
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type VisualizerType = typeof(Visualizer);
    private static readonly MethodInfo Render = VisualizerType.GetMethod("OnRender", Fields)!;

    [STAThread]
    private static void Main()
    {
        var settings = new VisualSettings { Mode = VisualMode.Waterfall };
        var visual = new Visualizer { Settings = settings, Frame = Sample() };
        var window = new Window { Content = visual, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Left = -6000, Top = -6000, Width = 440, Height = 300 };
        window.Show();
        try { Run(visual, settings); }
        finally { window.Close(); }
    }

    private static void Run(Visualizer visual, VisualSettings settings)
    {
        SetSize(visual, 420, 280);
        Check(Field<List<DrawingGroup>>(visual, "fadingLayers").Count == 0, "initial draw has no outgoing layer");

        var outgoing = Field<DrawingGroup>(visual, "liveLayer");
        var firstWaterfall = Field<WriteableBitmap>(visual, "waterfall");
        var waterfallPixels = ReadPixels(firstWaterfall);
        settings.Palette = VisualPalette.Ember;
        visual.Frame = Sample(.8f);
        var transition = Draw(visual);
        var fading = Field<List<DrawingGroup>>(visual, "fadingLayers");
        if (!SystemParameters.ClientAreaAnimation)
        {
            Check(fading.Count == 0 && ReferenceEquals(outgoing, Field<DrawingGroup>(visual, "liveLayer")),
                "disabled Windows animations switch instantly");
            Console.WriteLine("Animated frames were not checked because Windows client-area animations are disabled.");
            return;
        }

        Check(fading.Count == 1 && ReferenceEquals(fading[0], outgoing) && transition.Children.Count == 2,
            "palette change draws the outgoing and incoming layers together");
        Check(!ReferenceEquals(firstWaterfall, Field<WriteableBitmap>(visual, "waterfall")) &&
            ReadPixels(firstWaterfall).SequenceEqual(waterfallPixels), "incoming waterfall cannot overwrite the outgoing image");

        var incoming = Field<DrawingGroup>(visual, "liveLayer");
        WaitForMidpoint(outgoing, incoming);
        Check(outgoing.Opacity > 0 && outgoing.Opacity < 1 && incoming.Opacity > 0 && incoming.Opacity < 1,
            "both visible layers ease through intermediate opacity");

        settings.Mode = VisualMode.Halo;
        Draw(visual);
        Check(fading.Count == 2, "retargeting keeps both visible contributions");
        settings.Direction = VisualDirection.Right;
        settings.Atmosphere = VisualAtmosphere.Dust;
        Draw(visual);
        Check(fading.Count <= 2, "rapid mode, direction, and atmosphere changes bound retained drawing layers");

        Tick(TimeSpan.FromMilliseconds(300));
        var settled = Draw(visual);
        Check(fading.Count == 0 && settled.Children.Count == 1 && Field<DrawingGroup>(visual, "liveLayer").Opacity == 1,
            "settled visuals draw one fully visible live layer");

        settings.Mode = VisualMode.Bars;
        Draw(visual);
        Check(fading.Count == 1, "a mode change starts a fresh fade");
        SetSize(visual, 400, 220);
        Check(fading.Count == 0, "resize drops outgoing geometry from the old size");
    }

    private static AudioFrame Sample(float amplitude = .4f)
    {
        var wave = Enumerable.Range(0, 512).Select(i => (float)Math.Sin(i / 10d) * amplitude).ToArray();
        var spectrum = Enumerable.Range(1, 80).Select(i => (1 - i / 100f) * amplitude).ToArray();
        return new AudioFrame(wave, wave, spectrum, amplitude, amplitude / 2);
    }

    private static void SetSize(Visualizer visual, double width, double height)
    {
        visual.Width = width;
        visual.Height = height;
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        Draw(visual);
    }

    private static DrawingGroup Draw(Visualizer visual)
    {
        var output = new DrawingGroup();
        using var drawing = output.Open();
        Render.Invoke(visual, [drawing]);
        return output;
    }

    private static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static T Field<T>(Visualizer visual, string name) => (T)VisualizerType.GetField(name, Fields)!.GetValue(visual)!;

    private static void WaitForMidpoint(DrawingGroup outgoing, DrawingGroup incoming)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Tick(TimeSpan.FromMilliseconds(14));
            if (outgoing.Opacity is > .1 and < .9 && incoming.Opacity is > .1 and < .9) return;
        }
    }

    private static void Tick(TimeSpan interval)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
