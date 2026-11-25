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
        TestLyricFlow();
    }

    static void TestLyricFlow()
    {
        var song = LyricsTrack.Parse("[00:00.10]First\n[00:01.00]Second\n[00:02.00]Third\n[00:03.00]السَّطر الرابع\n[00:04.00]Fifth\n[00:06.00]Sixth");
        var type = typeof(App).Assembly.GetType("Sada.LyricFlow")!;
        var flow = (Grid)Activator.CreateInstance(type)!;
        var render = type.GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.DeclaredOnly)!;
        flow.Measure(new Size(320, 142));
        flow.Arrange(new Rect(0, 0, 320, 142));

        Update(song, 1950, 1950);
        Assert(Lines().Count() == 3 && Offset(Line("Second")) == 0, "steady layout shows previous, current and next rows");
        double priorNextOffset = Offset(Line("Third"));

        Update(song, 2001, 2001);
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert(Math.Abs(Offset(Line("Third")) - priorNextOffset) < .001, "the incoming current line starts at its previous position");
            Assert(Line("First").Opacity > 0 && Line("السَّطر الرابع").Opacity == 0, "the recycled edge rows do not pop in or out");
            Update(song, 2105, 2105);
            Assert(Offset(Line("Third")) > 0 && Offset(Line("Third")) < priorNextOffset, "the current line rises between adjacent frames");
            Assert(Line("First").Opacity < .72 && Line("السَّطر الرابع").Opacity > 0, "outgoing and incoming edge rows crossfade");
        }
        else Assert(Offset(Line("Third")) == 0 && Lines().Count() == 3, "reduced motion settles immediately");

        Update(song, 2110, 2290);
        Assert(Lines().Count() == 3 && Offset(Line("Third")) == 0, "settles at exactly three rows even when playback pauses");
        var effects = Line("Third").Inlines.OfType<Run>().SelectMany(run => run.TextEffects).ToArray();
        Update(song, 2110, 2390);
        Assert(Offset(Line("Third")) == 0 && Line("Third").Inlines.OfType<Run>().SelectMany(run => run.TextEffects).SequenceEqual(effects),
            "a steady paused playhead keeps its letter prefix");

        Update(song, 4100, 2406);
        Assert(Offset(Line("Fifth")) == 0 && Lines().Count() == 3, "forward seek snaps to the correct row");
        Update(song, 3010, 2422);
        Assert(Offset(Line("السَّطر الرابع")) == 0 && Line("السَّطر الرابع").FlowDirection == FlowDirection.RightToLeft,
            "backward seek snaps to the complete right-to-left row");

        var rapid = LyricsTrack.Parse("[00:01.00]One\n[00:01.04]Two\n[00:01.08]Three\n[00:01.12]Four\n[00:06.00]Five");
        Update(rapid, 1025, 3000);
        Update(rapid, 1042, 3017);
        Update(rapid, 1084, 3059);
        Update(rapid, 1125, 3100);
        Update(rapid, 1125, 3400);
        Assert(Lines().Count() == 3 && Offset(Line("Four")) == 0 && Line("Three").Opacity > 0 && Line("Five").Opacity > 0,
            "rapid line transitions recycle stable neighboring text");
        Console.WriteLine("PASS lyric flow rise, edge fades, pause, seeks and rapid line changes.");

        void Update(LyricsTrack track, int position, int clock) =>
            render.Invoke(flow, [track, TimeSpan.FromMilliseconds(position), (long)clock, "", 21d]);
        IEnumerable<TextBlock> Lines() => flow.Children.OfType<TextBlock>().Where(row => row.Visibility == Visibility.Visible);
        TextBlock Line(string text) => Lines().Single(row => string.Concat(row.Inlines.OfType<Run>().Select(run => run.Text)) == text);
        static double Offset(TextBlock row) => ((TranslateTransform)((TransformGroup)row.RenderTransform).Children[1]).Y;
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
        var firstEffect = runs[0].TextEffects.Single();
        Assert(runs[1].TextEffects.Count == 0, "upcoming word stays normal");
        Assert(opening.MintPixels > 0 && opening.MintPixels < opening.TextPixels, "one letter actually rendered in mint");
        if (SystemParameters.ClientAreaAnimation)
        {
            Render(1000 + 600 / clusterStarts.Length);
            var fade = runs[0].TextEffects.Single(effect => !ReferenceEquals(effect, firstEffect));
            Assert(fade.PositionCount == clusterStarts[2] - clusterStarts[1] &&
                ((SolidColorBrush)fade.Foreground).Color != ((SolidColorBrush)firstEffect.Foreground).Color,
                "incoming grapheme blends independently without breaking its shaped letter");
        }
        var ending = Render(2170);
        Assert(ending.MintPixels > opening.MintPixels, "more letters actually render in mint as time advances");
        Assert(runs[0].TextEffects.Single().PositionCount == first.Length, "full word colored near the end");
        Assert(Render(2410).MintPixels == 0, "explicit gap clears highlight");
        Assert(Render(2715).MintPixels > 0 && runs[0].TextEffects.Count == 0 && runs[1].TextEffects.Count == 1, "next word starts independently");
        Assert(Render(1010).MintPixels == opening.MintPixels, "backward seek redraws initial letter step");
        Assert(ReferenceEquals(firstEffect, runs[0].TextEffects.Single()), "backward seek reuses the shaped letter prefix");
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
