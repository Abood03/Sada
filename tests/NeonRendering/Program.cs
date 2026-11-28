using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sada;

internal static class Program
{
    static readonly MethodInfo Update = typeof(App).Assembly.GetType("Sada.NeonSign")!
        .GetMethod("RenderAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly AudioFrame Quiet = new([], [], [], 0, 0);
    static readonly AudioFrame Beat = new([], [], [.86f, .53f, .94f, .68f], .82f, 1);

    [STAThread]
    static void Main()
    {
        var sign = Create(435, 145);
        var options = new NeonOptions { Reaction = NeonReaction.Steady, Flicker = NeonFlicker.Steady };
        var latin = Playing("A pulse somewhere around midnight", "Midnight Radio");
        var arabic = Playing("لَحنٌ من بعيد في آخر الليل فوق المدينة", "صدى المساء");

        long initial = Draw(sign, latin, Quiet, options, 0);
        long partial = Draw(sign, latin, Quiet, options, 360);
        long steady = Draw(sign, latin, Quiet, options, 1100);
        Assert(steady > 100_000, "the sign renders its title and artist");
        if (SystemParameters.ClientAreaAnimation)
            Assert(initial < partial && partial < steady, "the title reveals incrementally");

        Draw(sign, arabic, Quiet, options, 1260);
        long change = Draw(sign, arabic, Quiet, options, 1560);
        long settled = Draw(sign, arabic, Quiet, options, 2370);
        Assert(settled > 100_000, "long Arabic title shapes and renders after a track switch");
        if (SystemParameters.ClientAreaAnimation)
            Assert(change < steady / 2, "the old track dims before the new title lights");

        var stopped = arabic with { IsPlaying = false };
        Draw(sign, stopped, Quiet, options, 3370);
        var off = Pixels(sign, stopped, Quiet, options, 3670);
        var paused = Energy(off);
        Assert(paused > 10_000 && paused < settled / 2, "dark glass remains visible after the light goes out");
        var stillOff = Pixels(sign, stopped, Quiet, options, 4370);
        Assert(off.AsSpan().SequenceEqual(stillOff), "after the cutoff, the dark glass is stable without a residual beat glow");

        var idle = Update.DeclaringType!.GetProperty("IdleOpacity", BindingFlags.NonPublic | BindingFlags.Instance)!;
        double MountOpacity() => (double)idle.GetValue(sign)!;
        options.Idle = NeonIdle.Fade;
        Draw(sign, stopped, Quiet, options, 8970);
        Assert(MountOpacity() == 1, "the dark sign stays in view during the idle grace period");
        Draw(sign, stopped, Quiet, options, 10000);
        Assert(Math.Abs(MountOpacity() - .30) < .001, "fade mode dims the entire mount after the idle grace period");
        options.Idle = NeonIdle.Hide;
        Draw(sign, stopped, Quiet, options, 10100);
        Assert(MountOpacity() == 0, "hide mode hides the glass and its backing together");
        var restarting = Draw(sign, arabic, Quiet, options, 10116);
        Assert(MountOpacity() == 1, "resume restores the mount");
        var litAgain = Draw(sign, arabic, Quiet, options, 11200);
        if (SystemParameters.ClientAreaAnimation)
            Assert(restarting < litAgain / 2, "the title relights progressively after a pause");

        Assert(new NeonOptions().Colors == NeonColors.Album, "new neon widgets start with album cover colors");
        var inkSign = Create(435, 145);
        Draw(inkSign, latin, Quiet, options, 0, Colors.Red, Colors.Red);
        Draw(inkSign, latin, Quiet, options, 1200, Colors.Red, Colors.Red);
        var oldInk = Pixels(inkSign, arabic, Quiet, options, 1300, Colors.Blue, Colors.Blue);
        var newInk = Pixels(inkSign, arabic, Quiet, options, 2450, Colors.Blue, Colors.Blue);
        int InkBias(byte[] data) => Enumerable.Range(0, data.Length / 4)
            .Sum(pixel => data[pixel * 4 + 2] - data[pixel * 4]);
        if (SystemParameters.ClientAreaAnimation)
            Assert(InkBias(oldInk) > 10_000, "the outgoing tubes retain their own album color");
        Assert(InkBias(newInk) < -10_000, "the new title and accent use the new album color");
        var lateInk = Pixels(inkSign, arabic, Quiet, options, 2470, Colors.Red, Colors.Red);
        Assert(InkBias(lateInk) > 10_000, "album artwork arriving for the current track updates the tubes");
        CheckAlbumArtwork(latin);
        CheckGlassAndGlow(latin);

        if (SystemParameters.ClientAreaAnimation)
        {
            var reactive = Create(435, 145);
            var theme = new NeonOptions { Reaction = NeonReaction.Flare };
            Draw(reactive, latin, Quiet, theme, 0);
            var rest = Draw(reactive, latin, Quiet, theme, 1100);
            var pulse = Draw(reactive, latin, Beat, theme, 1200);
            Assert(pulse > rest, "flare increases rendered glow when a beat arrives");
            CheckReactiveModes(latin);
        }

        foreach (var style in Enum.GetValues<NeonLettering>())
        foreach (var mode in Enum.GetValues<NeonReaction>())
        {
            var theme = new NeonOptions { Lettering = style, Reaction = mode, ShowArtist = false, ShowNote = false };
            var narrow = Create(200, 105);
            var combining = Playing("سَلام — A very long mixed-script title", "Hidden Artist");
            Draw(narrow, combining, Quiet, theme, 0, Colors.Magenta, Colors.Goldenrod);
            var pixels = Draw(narrow, combining, Beat, theme, 1000, Colors.Magenta, Colors.Goldenrod);
            Assert(pixels > 20_000, $"{style} / {mode} renders at a narrow widget size");
        }
        Console.WriteLine("PASS neon cover colors, title reveal, dark glass, pause, idle and reactions");
    }

    static void CheckGlassAndGlow(MediaSnapshot track)
    {
        var sign = Create(560, 185);
        var options = new NeonOptions { GlowIntensity = 0, GlassTrim = true, ShowArtist = false, ShowNote = false,
            Flicker = NeonFlicker.Steady, Reaction = NeonReaction.Steady };
        Draw(sign, track, Quiet, options, 0);
        var filament = Pixels(sign, track, Quiet, options, 1250, Colors.DeepSkyBlue);
        options.GlowIntensity = 2;
        var halo = Pixels(sign, track, Quiet, options, 1275, Colors.DeepSkyBlue);
        int illuminatedOutside = 0;
        for (int i = 3; i < halo.Length; i += 4)
            if (filament[i] == 0 && halo[i] >= 5) illuminatedOutside++;
        Assert(Energy(filament) > 100_000, "filament stays visibly lit with the bloom switched off");
        Assert(illuminatedOutside > 500 && Energy(halo) > Energy(filament), "the glow control projects a halo beyond the glass");

        options.GlowIntensity = 0;
        options.GlassTrim = false;
        var plain = Pixels(sign, track, Quiet, options, 1300, Colors.DeepSkyBlue);
        Assert(!plain.AsSpan().SequenceEqual(filament), "glass trim changes the lit edge independently from the halo");
        var paused = track with { IsPlaying = false };
        Draw(sign, paused, Quiet, options, 1320, Colors.DeepSkyBlue);
        var untrimmedDark = Pixels(sign, paused, Quiet, options, 1620, Colors.DeepSkyBlue);
        options.GlassTrim = true;
        var trimmedDark = Pixels(sign, paused, Quiet, options, 1640, Colors.DeepSkyBlue);
        Assert(Energy(trimmedDark) > Energy(untrimmedDark), "glass trim adds a reflection on the unlit sign");
    }

    static void CheckReactiveModes(MediaSnapshot track)
    {
        const int width = 560;
        var sign = Create(width, 125);
        track = track with { Title = "WIDE HORIZON" };
        var options = new NeonOptions { Lettering = NeonLettering.Block, Reaction = NeonReaction.Equalizer,
            ShowNote = false, ShowArtist = false, Flicker = NeonFlicker.Steady, GlowIntensity = 0 };
        Draw(sign, track, Quiet, options, 0);
        var rest = Pixels(sign, track, Quiet, options, 1150);
        var lowBands = Pixels(sign, track, Beat with { Spectrum = [1, .9f, .01f, .01f] }, options, 2050);
        var highBands = Pixels(sign, track, Beat with { Spectrum = [.01f, .01f, .9f, 1] }, options, 2950);
        int left = width, right = 0;
        for (int i = 3; i < rest.Length; i += 4)
        {
            if (rest[i] < 30) continue;
            int x = i / 4 % width;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }
        int middle = (left + right) / 2;
        long Delta(byte[] lit, bool leftSide)
        {
            long delta = 0;
            for (int i = 0; i < lit.Length; i += 4)
            {
                if ((i / 4 % width < middle) != leftSide) continue;
                delta += lit[i] - rest[i] + lit[i + 1] - rest[i + 1] + lit[i + 2] - rest[i + 2];
            }
            return delta;
        }
        Assert(Delta(lowBands, leftSide: true) > Delta(lowBands, leftSide: false) &&
            Delta(highBands, leftSide: false) > Delta(highBands, leftSide: true),
            "equalizer lights the part of the title corresponding to the active spectrum");

        options.Reaction = NeonReaction.Surge;
        long quiet = Draw(sign, track, Quiet, options, 3800);
        var strong = Beat with { Beat = 0 };
        long breath = Draw(sign, track, strong, options, 4650);
        long dip = Draw(sign, track, Beat, options, 4700);
        long recovered = Draw(sign, track, strong, options, 5450);
        Assert(breath > quiet && dip < breath && recovered > dip, "surge follows volume and dips briefly on a strong kick");
    }

    static void CheckAlbumArtwork(MediaSnapshot track)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = new App();
        typeof(App).GetProperty("Prefs", instance)!.SetValue(app, new Preferences());
        var media = (MediaService)typeof(App).GetProperty("Media", instance)!.GetValue(app)!;
        var current = typeof(MediaService).GetField("current", instance)!;
        var type = typeof(App).Assembly.GetType("Sada.WidgetView", throwOnError: true)!;
        var widget = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [app, WidgetKind.Neon, false], null)!;
        var render = type.GetMethod("Render")!;
        var inks = type.GetField("neonColors", instance)!;

        Color? TitleWith(byte[]? artwork)
        {
            current.SetValue(media, track with { Artwork = artwork });
            render.Invoke(widget, [Quiet, new Point()]);
            var palette = ((Color? Title, Color? Artist))inks.GetValue(widget)!;
            return palette.Title;
        }

        Assert(TitleWith(Cover(Colors.Red)) == Colors.Red, "widget samples the dominant color without the global mood setting");
        Assert(TitleWith(Cover(Colors.Blue)) == Colors.Blue, "a newly delivered cover changes the widget palette");
        Assert(TitleWith(null) is null, "a track without artwork clears the previous cover color");
    }

    static byte[] Cover(Color color)
    {
        byte[] pixels = new byte[32 * 32 * 4];
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            pixels[pixel] = color.B;
            pixels[pixel + 1] = color.G;
            pixels[pixel + 2] = color.R;
            pixels[pixel + 3] = 255;
        }
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static MediaSnapshot Playing(string title, string artist) => new(title, artist, "Test", null,
        TimeSpan.Zero, TimeSpan.FromSeconds(200), true, "TestPlayer");

    static FrameworkElement Create(int width, int height)
    {
        var element = (FrameworkElement)Activator.CreateInstance(Update.DeclaringType!, nonPublic: true)!;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        return element;
    }

    static long Draw(FrameworkElement sign, MediaSnapshot track, AudioFrame audio, NeonOptions spec,
        long milliseconds, Color? title = null, Color? artist = null) =>
        Energy(Pixels(sign, track, audio, spec, milliseconds, title, artist));

    static byte[] Pixels(FrameworkElement sign, MediaSnapshot track, AudioFrame audio, NeonOptions spec,
        long milliseconds, Color? title = null, Color? artist = null)
    {
        Update.Invoke(sign, [track, audio, spec, milliseconds, title, artist]);
        sign.UpdateLayout();
        int width = (int)sign.ActualWidth, height = (int)sign.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sign);
        var data = new byte[width * height * 4];
        bitmap.CopyPixels(data, width * 4, 0);
        return data;
    }

    static long Energy(byte[] data)
    {
        long energy = 0;
        for (int pixel = 0; pixel < data.Length; pixel += 4)
            energy += data[pixel] + data[pixel + 1] + data[pixel + 2];
        return energy;
    }

    static void Assert(bool condition, string requirement)
    {
        if (!condition) throw new InvalidOperationException(requirement);
    }
}
