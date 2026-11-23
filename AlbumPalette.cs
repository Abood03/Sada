using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sada;

internal sealed record AlbumPalette(Color First, Color Middle, Color Last)
{
    static readonly ConditionalWeakTable<BitmapSource, AlbumPalette> Cache = new();
    static readonly AlbumPalette Empty = new(Colors.Transparent, Colors.Transparent, Colors.Transparent);

    public static AlbumPalette? From(BitmapSource? image)
    {
        if (image is null) return null;
        var result = Cache.GetValue(image, Extract);
        return ReferenceEquals(result, Empty) ? null : result;
    }

    static AlbumPalette Extract(BitmapSource image)
    {
        int edge = Math.Min(image.PixelWidth, image.PixelHeight);
        var crop = new CroppedBitmap(image, new Int32Rect((image.PixelWidth - edge) / 2, (image.PixelHeight - edge) / 2, edge, edge));
        var scale = 32.0 / edge;
        var sampled = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
        var bitmap = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[bitmap.PixelHeight * stride];
        bitmap.CopyPixels(pixels, stride, 0);
        var buckets = new Dictionary<int, (int R, int G, int B, int Count)>();
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            if (pixels[pixel + 3] < 125) continue;
            int red = pixels[pixel + 2], green = pixels[pixel + 1], blue = pixels[pixel];
            int key = (red / 32) * 64 + (green / 32) * 8 + blue / 32;
            var sum = buckets.GetValueOrDefault(key);
            buckets[key] = (sum.R + red, sum.G + green, sum.B + blue, sum.Count + 1);
        }

        if (buckets.Count == 0) return Empty;
        var candidates = buckets.Values.Select(sum =>
        {
            var color = Color.FromRgb((byte)(sum.R / sum.Count), (byte)(sum.G / sum.Count), (byte)(sum.B / sum.Count));
            return (Color: color, sum.Count, Chroma: Chroma(color));
        }).ToArray();
        var colorful = candidates.Where(entry => entry.Count >= 4 && entry.Chroma >= 43)
            .OrderByDescending(entry => entry.Count * (.7 + entry.Chroma / 255d)).ToArray();
        if (colorful.Length == 0)
        {
            var neutral = candidates.OrderByDescending(entry => entry.Count).FirstOrDefault().Color;
            var reference = Color.FromRgb(neutral.R, neutral.G, neutral.B);
            return new AlbumPalette(Brighten(reference, 124), Brighten(reference, 181), Brighten(reference, 233));
        }

        var main = Brighten(colorful[0].Color, 166);
        var accents = colorful.Skip(1).Where(entry => entry.Count >= Math.Max(6, colorful[0].Count * .1))
            .Select(entry => Brighten(entry.Color, 174))
            .Where(color => Distance(color, main) >= 92).ToArray();
        var accent = accents.Length > 0 ? accents[0] : Tint(main, .42);
        var highlight = accents.Skip(1).FirstOrDefault(color => Distance(color, accent) >= 92);
        return new AlbumPalette(main, accent, highlight == default ? Tint(accent, accents.Length > 0 ? .22 : .35) : highlight);
    }

    static int Peak(Color color) => Math.Max(color.R, Math.Max(color.G, color.B));
    static int Chroma(Color color) => Peak(color) - Math.Min(color.R, Math.Min(color.G, color.B));

    static Color Brighten(Color color, double minLight)
    {
        int brightness = Peak(color);
        return brightness >= minLight ? color : Tint(color, (minLight - brightness) / (255 - brightness));
    }

    static Color Tint(Color color, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (255 - color.R) * amount),
        (byte)Math.Round(color.G + (255 - color.G) * amount),
        (byte)Math.Round(color.B + (255 - color.B) * amount));

    static double Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    public static string RgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
