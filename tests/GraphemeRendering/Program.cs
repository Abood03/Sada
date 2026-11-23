using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sada;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        TestWord("WAVES", "mellow", false);
        TestWord("سَلام", "هادي", true);
        TestWord("ábc", "down", false);
    }

    static void TestWord(string first, string second, bool rtl)
    {
        var model = LyricsTrack.ParseLyricsFile(
            $"version: 1.0\nlines:\n  - text: '{first} {second}'\n    start_ms: 1000\n    end_ms: 3400\n    words:\n      - text: '{first}'\n        start_ms: 1000\n        end_ms: 2200\n      - text: '{second}'\n        start_ms: 2700\n        end_ms: 3400\n")!;
        var type = typeof(App).Assembly.GetType("Sada.LyricText")!;
        var block = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(247, 249, 255)),
            FontSize = 38,
            FontFamily = new FontFamily("Segoe UI"),
            TextAlignment = TextAlignment.Center
        };
        var display = Activator.CreateInstance(type, block)!;
        type.GetMethod("SetTimed")!.Invoke(display, [model, 0]);
        var update = type.GetMethod("HighlightAt")!;
        var runs = block.Inlines.OfType<Run>().ToArray();
        var clusterStarts = StringInfo.ParseCombiningCharacters(first);

        var normal = Render(950);
        Assert(normal.MintPixels == 0, "no highlight prior to the word");
        var opening = Render(1001);
        Assert(runs[0].TextEffects.Single().PositionCount == clusterStarts[1], "first whole Unicode letter");
        Assert(runs[1].TextEffects.Count == 0, "upcoming word stays normal");
        Assert(opening.MintPixels > 0 && opening.MintPixels < opening.TextPixels, "one letter actually rendered in mint");
        var ending = Render(2170);
        Assert(ending.MintPixels > opening.MintPixels, "more letters actually render in mint as time advances");
        Assert(runs[0].TextEffects.Single().PositionCount == first.Length, "full word colored near the end");
        Assert(Render(2410).MintPixels == 0, "explicit gap clears highlight");
        Assert(Render(2715).MintPixels > 0 && runs[0].TextEffects.Count == 0 && runs[1].TextEffects.Count == 1, "next word starts independently");
        Assert(Render(1010).MintPixels == opening.MintPixels, "backward seek redraws initial letter step");
        Assert(Render(1500, false).MintPixels == 0, "neighboring lyric stays unhighlighted");
        Assert(block.FlowDirection == (rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight), "script direction is preserved");
        Assert(string.Concat(runs.Select(run => run.Text)) == first + " " + second && block.TextTrimming == TextTrimming.None,
            "every lyric letter remains present");
        Assert(Math.Abs(normal.TextPixels - ending.TextPixels) < normal.TextPixels * .12, "tint preserves glyph coverage");
        Console.WriteLine($"PASS {(rtl ? "RTL" : "LTR")} with {clusterStarts.Length} clusters. Colored pixels {opening.MintPixels} → {ending.MintPixels}.");

        (int TextPixels, int MintPixels) Render(int milliseconds, bool active = true)
        {
            update.Invoke(display, [TimeSpan.FromMilliseconds(milliseconds), active]);
            block.Measure(new Size(550, 70));
            block.Arrange(new Rect(0, 0, 550, 70));
            block.UpdateLayout();
            var bitmap = new RenderTargetBitmap(550, 70, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(block);
            var buffer = new byte[550 * 70 * 4];
            bitmap.CopyPixels(buffer, 550 * 4, 0);
            int textPixels = 0, mintPixels = 0;
            for (int i = 0; i < buffer.Length; i += 4)
            {
                if (buffer[i + 3] < 55) continue;
                textPixels++;
                if (buffer[i + 1] - buffer[i + 2] > 22) mintPixels++;
            }
            return (textPixels, mintPixels);
        }
    }

    static void Assert(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
    }
}
