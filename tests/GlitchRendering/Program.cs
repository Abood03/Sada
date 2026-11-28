using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sada;

internal static class Program
{
    private static readonly Type CardType = typeof(App).Assembly.GetType("Sada.GlitchBox")!;
    private static readonly MethodInfo DrawFrame = CardType.GetMethod("RenderAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly AudioFrame Quiet = new([], [], [], 0, 0);
    private static readonly AudioFrame Beat = new([], [], [.8f, .75f, .85f, .92f], .82f, 1);

    [STAThread]
    private static void Main()
    {
        var cover = CreateArtwork(Colors.OrangeRed, Colors.LemonChiffon);
        var secondCover = CreateArtwork(Colors.MediumBlue, Colors.SkyBlue);
        var song = Playing("A Painted Skyline");
        var nextSong = Playing("لحن المساء");
        var options = new GlitchOptions { Intensity = .95, MusicResponse = 1, Scanlines = false };
        var card = Create(156, 112);
        var resting = Capture(card, cover, song, Quiet, options, 0);
        Check(resting[3] < 32 && resting.Any(value => value > 200), "rounded mount keeps its outside corners transparent and preserves cover detail");

        Capture(card, cover, song, Beat, options, 370);
        var pulsing = Capture(card, cover, song, Beat, options, 430);
        Check(DifferentPixels(resting, pulsing) > 350, "a beat displaces and separates cover colors");
        var settled = Capture(card, cover, song, Quiet, options, 750);
        Check(settled.SequenceEqual(resting), "the quiet frame returns to the original cover");

        var first = Capture(card, secondCover, nextSong, Quiet, options, 800);
        var between = Capture(card, secondCover, nextSong, Quiet, options, 935);
        var second = Capture(card, secondCover, nextSong, Quiet, options, 1240);
        Check(first.SequenceEqual(resting) && DifferentPixels(first, between) > 350 &&
            DifferentPixels(between, second) > 350, "cover change starts on the outgoing frame and blends to the new artwork");

        options.Scanlines = true;
        var scanlines = Capture(card, secondCover, nextSong, Quiet, options, 1300);
        Check(DifferentPixels(second, scanlines) > 350, "scanlines can be changed independently of the audio response");

        options.Scanlines = false;
        options.MusicResponse = 0;
        Capture(card, secondCover, nextSong, Beat, options, 1450);
        var responseOff = Capture(card, secondCover, nextSong, Beat, options, 1520);
        Check(responseOff.SequenceEqual(second), "turning music response off keeps the cover still at a beat");
        options.MusicResponse = 1;

        var portrait = Create(104, 164);
        var reduced = Capture(portrait, cover, song, Quiet, options, 0, reducedMotion: true);
        Capture(portrait, cover, song, Beat, options, 380, reducedMotion: true);
        var reducedAfterBeat = Capture(portrait, cover, song, Beat, options, 430, reducedMotion: true);
        Check(reduced.SequenceEqual(reducedAfterBeat), "reduced motion displays a static portrait crop");
        var reducedSwitch = Capture(portrait, secondCover, nextSong, Quiet, options, 570, reducedMotion: true);
        var reducedLater = Capture(portrait, secondCover, nextSong, Quiet, options, 800, reducedMotion: true);
        Check(reducedSwitch.SequenceEqual(reducedLater) && DifferentPixels(reduced, reducedSwitch) > 350,
            "reduced motion updates the cover immediately");

        var accent = Colors.Lime;
        var fallback = Capture(portrait, null, nextSong, Quiet, options, 950, reducedMotion: true, accent: accent);
        options.Content = GlitchContent.Title;
        var title = Capture(portrait, secondCover, nextSong, Quiet, options, 960, reducedMotion: true, accent: accent);
        var noTrack = Capture(portrait, null, new MediaSnapshot("", "", "", null, TimeSpan.Zero, TimeSpan.Zero, false, ""),
            Quiet, options, 975, reducedMotion: true, accent: accent);
        Check(title.SequenceEqual(fallback) && DifferentPixels(title, noTrack) > 100,
            "missing artwork shows the song title, and a missing song shows the idle mark");
    }

    private static MediaSnapshot Playing(string title) =>
        new(title, "Studio", "Album", null, TimeSpan.Zero, TimeSpan.FromMinutes(3), true, "Test");

    private static FrameworkElement Create(int width, int height)
    {
        var widget = (FrameworkElement)Activator.CreateInstance(CardType, nonPublic: true)!;
        widget.Measure(new Size(width, height));
        widget.Arrange(new Rect(0, 0, width, height));
        return widget;
    }

    private static byte[] Capture(FrameworkElement card, BitmapSource? cover, MediaSnapshot song, AudioFrame frame,
        GlitchOptions options, long milliseconds, bool reducedMotion = false, Color? accent = null)
    {
        DrawFrame.Invoke(card, [cover, song, frame, options, milliseconds, reducedMotion, accent]);
        card.UpdateLayout();
        int width = (int)card.ActualWidth;
        int height = (int)card.ActualHeight;
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(card);
        var data = new byte[width * height * 4];
        rendered.CopyPixels(data, width * 4, 0);
        return data;
    }

    private static BitmapSource CreateArtwork(Color first, Color second)
    {
        const int width = 128;
        const int height = 96;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var color = (x / 7 + y / 13) % 2 == 0 ? first : second;
            int i = (width * y + x) * 4;
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = byte.MaxValue;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static int DifferentPixels(byte[] first, byte[] second)
    {
        int changed = 0;
        for (int i = 0; i < first.Length; i += 4)
            if (Math.Abs(first[i] - second[i]) + Math.Abs(first[i + 1] - second[i + 1]) + Math.Abs(first[i + 2] - second[i + 2]) > 5)
                changed++;
        return changed;
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
}
