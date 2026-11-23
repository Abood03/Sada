using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Sada;

internal static class Program
{
    static readonly TimeSpan Duration = TimeSpan.FromSeconds(12);
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    static void Main()
    {
        var source = LyricsTrack.Parse("[00:01]<00:01.1>Aَb <00:02>next<00:03>\n[00:04]<00:04>عربي <00:05>نص<00:06>\n[00:08]Last line",
            duration: Duration);
        var assembly = typeof(LyricsTrack).Assembly;
        var draft = Activator.CreateInstance(assembly.GetType("Sada.LyricTimingDraft")!, source, Duration, TimeSpan.Zero)!;
        var recording = new MediaSnapshot("Fixture track", "Fixture artist", "Fixture album", null,
            TimeSpan.FromSeconds(1.6), Duration, false, "Fixture player");
        var canSeek = typeof(MediaSnapshot).GetProperty("CanSeek", Private)!;
        canSeek.SetValue(recording, true);
        MediaSnapshot? liveSong = recording;
        MediaSnapshot? ReadPlayback() => liveSong;
        var playbackCommands = new List<bool>();
        var seekCommands = new List<TimeSpan>();
        var saveCalls = 0;
        bool restored = false;
        Task<bool> SetPlaying(bool playing)
        {
            if (liveSong is null) return Task.FromResult(false);
            playbackCommands.Add(playing);
            liveSong = liveSong with { IsPlaying = playing };
            return Task.FromResult(true);
        }

        Task<bool> Seek(TimeSpan time)
        {
            if (liveSong is null) return Task.FromResult(false);
            seekCommands.Add(time);
            liveSong = liveSong with { Position = time };
            return Task.FromResult(true);
        }

        var studioType = assembly.GetType("Sada.LyricsStudio")!;
        var ctor = studioType.GetConstructors(Private).Single();
        var saveType = ctor.GetParameters()[6].ParameterType;
        var lambdaArg = System.Linq.Expressions.Expression.Parameter(draft.GetType(), "value");
        var saver = (Func<object, bool>)(value => { Assert(ReferenceEquals(draft, value), "save forwards pinned draft"); saveCalls++; return true; });
        var typedSaver = System.Linq.Expressions.Expression.Lambda(saveType,
            System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(saver), lambdaArg), lambdaArg).Compile();
        var studio = (Window)ctor.Invoke([recording, draft, false, (Func<MediaSnapshot?>)ReadPlayback,
            (Func<bool, Task<bool>>)SetPlaying, (Func<TimeSpan, Task<bool>>)Seek,
            typedSaver, (Func<bool>)(() => { restored = true; return true; }), 21d]);

        var container = (UIElement)studio.Content;
        container.Measure(new Size(1080, 765));
        container.Arrange(new Rect(0, 0, 1080, 765));
        container.UpdateLayout();
        var lines = Field<ListBox>("lines");
        var words = Field<ListBox>("words");
        Assert(lines.Items.Count == 3 && lines.SelectedIndex == 0 && words.Items.Count == 2 && words.SelectedIndex == -1,
            "pinned track opens with current line and word list");
        Assert(Field<TextBlock>("timingSource").Text == "Source word timing", "authored provenance is visible");

        Do("MarkLineAtPlayback");
        Assert(GetLines()[0].At == TimeSpan.FromSeconds(1.6) && lines.SelectedIndex == 1,
            "mark uses raw playhead and advances to the next line");
        Assert(Field<TextBlock>("timingSource").Text.Contains("unsaved"), "dirty draft status");

        var timeline = Field<Slider>("timeline");
        timeline.Value = 4.75;
        Assert(seekCommands.Count == 0, "preview slider doesn't poke the external player");
        ((Task)Do("SeekHere")!).GetAwaiter().GetResult();
        Assert(seekCommands.Single() == TimeSpan.FromSeconds(4.75), "seek command uses the preview position");

        ((Task)Do("ReplaySelection")!).GetAwaiter().GetResult();
        Assert(seekCommands.Last() == TimeSpan.FromSeconds(3) && playbackCommands.SequenceEqual(new[] { true }),
            "replay includes preroll and explicitly starts playback");
        Do("UpdatePlayback");
        liveSong = liveSong! with { Position = TimeSpan.FromSeconds(9.1) };
        Do("UpdatePlayback");
        Assert(playbackCommands.SequenceEqual(new[] { true, false }), "short replay requests explicit pause");

        words.SelectedIndex = 0;
        liveSong = null;
        Do("UpdatePlayback");
        Assert(!Field<Button>("mark").IsEnabled && !Field<Button>("playPause").IsEnabled &&
            !Field<Button>("seekHere").IsEnabled && !Field<Button>("replay").IsEnabled,
            "live transport is disabled when pinned track is no longer playing");
        Assert(Field<Button>("applyStart").IsEnabled && Field<Button>("applyEnd").IsEnabled,
            "manual editing remains available when playback moves on");
        var lineField = Field<TextBox>("lineStart");
        lineField.Text = "bad";
        Click(Field<Button>("applyLine"));
        Assert(Field<TextBlock>("feedback").Text.Contains("Enter a time"), "invalid typed time is rejected");
        lineField.Text = "00:04.250";
        Click(Field<Button>("applyLine"));
        Assert(GetLines()[1].At == TimeSpan.FromSeconds(4.25), "precise offline line edit");
        var wordEnd = Field<TextBox>("wordEnd");
        wordEnd.Text = "00:04.690";
        Click(Field<Button>("applyEnd"));
        var editedWords = (IReadOnlyList<LyricWord>)draft.GetType().GetMethod("WordsForLine")!.Invoke(draft, [1])!;
        Assert(editedWords[0].End == TimeSpan.FromSeconds(4.69), "precise offline word end");
        Assert(!restored && saveCalls == 0, "editing does not write before Save");
        Do("SaveTiming");
        Assert(saveCalls == 1 && !restored, "save works against the original pinned draft after changing songs");
        Console.WriteLine("PASS 14 Lyrics Studio UI checks using an offscreen WPF window and fake playback.");

        T Field<T>(string name) => (T)studioType.GetField(name, Private)!.GetValue(studio)!;
        object? Do(string name) => studioType.GetMethod(name, Private)!.Invoke(studio, null);
        IReadOnlyList<LyricLine> GetLines() => (IReadOnlyList<LyricLine>)draft.GetType().GetProperty("Lines")!.GetValue(draft)!;
    }

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void Assert(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
        Console.WriteLine("PASS " + scenario);
    }
}
