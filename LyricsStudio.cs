using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static Sada.StudioUi;

namespace Sada;

internal sealed class LyricsStudio : Window
{
    readonly record struct TimingRow(string Time, string Lyric, FlowDirection Direction);
    readonly record struct Range(TimeSpan Start, TimeSpan End);

    readonly LyricTimingDraft draft;
    readonly Func<MediaSnapshot?> pinnedPlayback;
    readonly Func<bool, Task<bool>> setPlaying;
    readonly Func<TimeSpan, Task<bool>> seek;
    readonly Func<LyricTimingDraft, bool> save;
    readonly Func<bool> restoreOriginal;
    readonly double textSize;
    readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(24) };
    readonly LyricFlow preview = new() { Height = 142 };
    readonly ListBox lines = CreateList("Lyric lines");
    readonly ListBox words = CreateList("Timed words");
    readonly Slider timeline = new() { Minimum = 0, SmallChange = .1, LargeChange = 1, IsMoveToPointEnabled = true };
    readonly TextBlock timingSource = Text("", 12, Mint, true);
    readonly TextBlock playerStatus = Text("", 12, Soft);
    readonly TextBlock timeReadout = Text("", 13, Paper, true);
    readonly TextBlock feedback = Text("", 12, Soft);
    readonly TextBox lineStart = TimeInput("Line start");
    readonly TextBox wordStart = TimeInput("Word start");
    readonly TextBox wordEnd = TimeInput("Word end");
    readonly CueStrip cues = new() { Height = 18, Margin = new Thickness(6, 6, 6, 0), ClipToBounds = true };
    readonly Button playPause;
    readonly Button mark;
    readonly Button seekHere;
    readonly Button replay;
    readonly Button follow;
    readonly Button applyLine;
    readonly Button applyStart;
    readonly Button applyEnd;
    readonly Button liveStart;
    readonly Button liveEnd;
    LyricsTrack previewTrack;
    TimeSpan previewPosition;
    Range? replayRange;
    long replayStartedAt;
    bool replayReachedStart;
    bool busy;
    bool closing;
    bool rebuilding;
    bool following = true;
    bool movingTimeline;
    bool changed;
    readonly bool savedTiming;
    byte[]? previewArtwork;

    internal LyricsStudio(MediaSnapshot recording, LyricTimingDraft draft, bool hasSavedTiming,
        Func<MediaSnapshot?> pinnedPlayback, Func<bool, Task<bool>> setPlaying,
        Func<TimeSpan, Task<bool>> seek, Func<LyricTimingDraft, bool> save,
        Func<bool> restoreOriginal, double textSize = 21)
    {
        this.draft = draft;
        this.pinnedPlayback = pinnedPlayback;
        this.setPlaying = setPlaying;
        this.seek = seek;
        this.save = save;
        this.restoreOriginal = restoreOriginal;
        this.textSize = textSize;
        savedTiming = hasSavedTiming;
        previewTrack = draft.Preview();

        Title = "Sada — Lyrics studio";
        Background = Night;
        Foreground = Paper;
        Width = 1100;
        Height = 810;
        MinWidth = 790;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        playPause = Button("Pause", () => _ = TogglePlayback());
        mark = Button("Mark line at playhead", MarkLineAtPlayback, true);
        seekHere = Button("Seek to preview time", () => _ = SeekHere());
        replay = Button("Replay selection", () => _ = ReplaySelection());
        follow = Button("Follow playback", () => { following = true; UpdatePlayback(); });
        applyLine = Button("Set line start", () => ApplyTime(lineStart, time => draft.MarkLine(lines.SelectedIndex, time)));
        applyStart = Button("Set word start", () => ApplyTime(wordStart, time => draft.SetWordStart(lines.SelectedIndex, words.SelectedIndex, time)));
        applyEnd = Button("Set word end", () => ApplyTime(wordEnd, time => draft.SetWordEnd(lines.SelectedIndex, words.SelectedIndex, time)));
        liveStart = Button("Start at playhead", () => ApplyLiveTime(time => draft.SetWordStart(lines.SelectedIndex, words.SelectedIndex, time)));
        liveEnd = Button("End at playhead", () => ApplyLiveTime(time => draft.SetWordEnd(lines.SelectedIndex, words.SelectedIndex, time)));
        EnterApplies(lineStart, applyLine);
        EnterApplies(wordStart, applyStart);
        EnterApplies(wordEnd, applyEnd);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        UpdateCoverColor(recording.Artwork);
        if (WidgetView.GetArtwork(recording.Artwork) is { } artwork)
        {
            var image = new Image { Source = artwork, Width = 62, Height = 62, Stretch = Stretch.UniformToFill };
            var cover = new Border { Child = image, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 13, 0) };
            DockPanel.SetDock(cover, Dock.Left);
            header.Children.Add(cover);
        }
        header.Children.Add(Stack(Text(recording.Title, 24, Paper, true), Text(
            string.Join(" · ", new[] { recording.Artist, recording.Album }.Where(value => !string.IsNullOrWhiteSpace(value))), 13, Soft), timingSource));

        timeline.Maximum = Math.Max(.01, draft.Duration.TotalSeconds);
        timeline.Margin = new Thickness(0, 0, 0, 7);
        AutomationProperties.SetName(timeline, "Lyrics preview position");
        timeline.ValueChanged += (_, e) =>
        {
            if (movingTimeline) return;
            following = false;
            ShowTime(TimeSpan.FromSeconds(e.NewValue));
            UpdateButtons(pinnedPlayback());
        };

        var previewCard = Card(Stack(preview, cues, timeline, Wrap(playPause, seekHere, replay, follow), timeReadout, playerStatus), Side, 14);
        previewCard.Margin = new Thickness(0, 0, 0, 14);
        var content = new Grid();
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(13) });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1.07, GridUnitType.Star) });
        var lineActions = Wrap(mark, Button("Earlier 0.10 s", () => Apply(() => draft.Shift(TimeSpan.FromMilliseconds(-100)))),
            Button("Later 0.10 s", () => Apply(() => draft.Shift(TimeSpan.FromMilliseconds(100)))));
        var editorLeft = BuildColumn("Lines", lines, Stack(lineActions, TimeField("Line start", lineStart, applyLine)));
        content.Children.Add(editorLeft);
        var editorRight = BuildColumn("Words in selected line", words, Stack(
            TimeField("Word start", wordStart, applyStart), TimeField("Word end", wordEnd, applyEnd),
            Wrap(liveStart, liveEnd)));
        Grid.SetColumn(editorRight, 2);
        content.Children.Add(editorRight);

        lines.SelectionChanged += (_, _) => { if (!rebuilding) ShowSelection(-1); };
        words.SelectionChanged += (_, _) => { if (!rebuilding) ShowWord(); };
        var footerActions = Wrap(Button("Cancel", Close), Button("Save timing", SaveTiming, true));
        if (hasSavedTiming)
            footerActions.Children.Insert(0, Button("Restore source timing", RestoreSource));
        footerActions.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 13, 0, 0) };
        DockPanel.SetDock(footerActions, Dock.Right);
        footer.Children.Add(footerActions);
        feedback.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(feedback);

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(header);
        Grid.SetRow(previewCard, 1);
        root.Children.Add(previewCard);
        Grid.SetRow(content, 2);
        root.Children.Add(content);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        timer.Tick += (_, _) => UpdatePlayback();
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) =>
        {
            closing = true;
            timer.Stop();
            replayRange = null;
        };
        PreviewKeyDown += HandleKey;
        Rebuild(Math.Max(0, previewTrack.IndexAt(recording.Position)), -1);
        UpdatePlayback();
    }

    static Border BuildColumn(string title, ListBox list, UIElement tools)
    {
        var titleText = Text(title, 15, Paper, true);
        titleText.Margin = new Thickness(0, 0, 0, 9);
        var column = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleText, Dock.Top);
        column.Children.Add(titleText);
        DockPanel.SetDock(tools, Dock.Bottom);
        column.Children.Add(tools);
        list.Margin = new Thickness(0, 0, 0, 10);
        column.Children.Add(list);
        return Card(column, StudioUi.Panel, 13);
    }

    static UIElement TimeField(string label, TextBox input, Button apply)
    {
        var heading = Text(label, 12, Soft);
        heading.Width = 76;
        heading.VerticalAlignment = VerticalAlignment.Center;
        return Wrap(heading, input, apply);
    }

    static TextBox TimeInput(string name)
    {
        var field = new TextBox { Width = 113, Height = 32, Margin = new Thickness(0, 0, 7, 7), VerticalContentAlignment = VerticalAlignment.Center };
        field.ToolTip = "Minutes:seconds or decimal seconds";
        AutomationProperties.SetName(field, name);
        return field;
    }

    static void EnterApplies(TextBox input, Button button)
    {
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !button.IsEnabled) return;
            e.Handled = true;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        };
    }

    static ListBox CreateList(string name)
    {
        var list = new ListBox
        {
            Background = Night, BorderBrush = Line, Foreground = Paper,
            BorderThickness = new Thickness(1), Padding = new Thickness(3)
        };
        AutomationProperties.SetName(list, name);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.Resources[SystemColors.HighlightBrushKey] = Field;
        list.Resources[SystemColors.HighlightTextBrushKey] = Paper;
        list.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Field;
        list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Paper;
        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        container.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 5, 6, 5)));
        list.ItemContainerStyle = container;
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var stamp = new FrameworkElementFactory(typeof(TextBlock));
        stamp.SetBinding(TextBlock.TextProperty, new Binding(nameof(TimingRow.Time)));
        stamp.SetValue(TextBlock.ForegroundProperty, Mint);
        stamp.SetValue(FrameworkElement.WidthProperty, 91d);
        stamp.SetValue(DockPanel.DockProperty, Dock.Left);
        row.AppendChild(stamp);
        var lyric = new FrameworkElementFactory(typeof(TextBlock));
        lyric.SetBinding(TextBlock.TextProperty, new Binding(nameof(TimingRow.Lyric)));
        lyric.SetBinding(FlowDirectionProperty, new Binding(nameof(TimingRow.Direction)));
        lyric.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        lyric.SetValue(TextBlock.ForegroundProperty, Paper);
        row.AppendChild(lyric);
        list.ItemTemplate = new DataTemplate(typeof(TimingRow)) { VisualTree = row };
        return list;
    }

    void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        if (e.Key == Key.Enter && Keyboard.FocusedElement is not (TextBox or System.Windows.Controls.Button) && mark.IsEnabled)
        {
            e.Handled = true;
            MarkLineAtPlayback();
        }
    }

    void Rebuild(int selectedLine, int selectedWord)
    {
        previewTrack = draft.Preview();
        rebuilding = true;
        lines.ItemsSource = draft.Lines.Select(line => Row(line.At, line.Text)).ToArray();
        lines.SelectedIndex = draft.Lines.Count == 0 ? -1 : Math.Clamp(selectedLine, 0, draft.Lines.Count - 1);
        rebuilding = false;
        ShowSelection(selectedWord);
        if (lines.SelectedItem is { } item)
            lines.ScrollIntoView(item);
        cues.Replace(draft.Duration, draft.Lines.Select(line => line.At).ToArray());
        RenderPreview();
    }

    void ShowSelection(int selectedWord)
    {
        rebuilding = true;
        int line = lines.SelectedIndex;
        var list = draft.WordsForLine(line);
        words.ItemsSource = list.Select(word => Row(word.At, word.Text)).ToArray();
        words.SelectedIndex = list.Count == 0 ? -1 : Math.Clamp(selectedWord, -1, list.Count - 1);
        lineStart.Text = line < 0 ? "" : Format(draft.Lines[line].At);
        rebuilding = false;
        ShowWord();
    }

    void ShowWord()
    {
        int line = lines.SelectedIndex;
        var wordList = draft.WordsForLine(line);
        int index = words.SelectedIndex;
        LyricWord? word = index >= 0 && index < wordList.Count ? wordList[index] : null;
        wordStart.Text = word is { } current ? Format(current.At) : "";
        wordEnd.Text = word?.End is { } end ? Format(end) : "";
        cues.Selection = SelectionRange();
        cues.InvalidateVisual();
        var source = draft.HasEditedTiming(line) ? "Edited timing" : savedTiming ? "Local timing" :
            draft.HasAuthoredTiming(line) ? "Source word timing" : "Estimated word timing";
        timingSource.Text = changed ? source + " · unsaved changes" : source;
        var playback = pinnedPlayback();
        if ((!following || playback is null) && line >= 0)
            SetTimeline(word?.At ?? draft.Lines[line].At);
        UpdateButtons(playback);
    }

    Range? SelectionRange()
    {
        int line = lines.SelectedIndex;
        if (line < 0 || line >= draft.Lines.Count) return null;
        var list = draft.WordsForLine(line);
        int word = words.SelectedIndex;
        if (word >= 0 && word < list.Count)
        {
            var selected = list[word];
            return new Range(selected.At, selected.End ?? selected.At + TimeSpan.FromSeconds(.6));
        }
        var start = draft.Lines[line].At;
        var end = line + 1 < draft.Lines.Count ? draft.Lines[line + 1].At : draft.Duration;
        return new Range(start, end);
    }

    void MarkLineAtPlayback()
    {
        var playback = pinnedPlayback();
        if (playback is null || lines.SelectedIndex < 0) return;
        following = true;
        int index = lines.SelectedIndex;
        if (Apply(() => draft.MarkLine(index, playback.Position)) && index + 1 < draft.Lines.Count)
        {
            lines.SelectedIndex = index + 1;
            if (lines.SelectedItem is { } next) lines.ScrollIntoView(next);
        }
    }

    void ApplyTime(TextBox input, Func<TimeSpan, bool> change)
    {
        if (!TryReadTime(input.Text, out var time))
        {
            feedback.Text = "Enter a time such as 00:24.300 or 24.300.";
            input.Focus();
            return;
        }
        if (Apply(() => change(time))) return;
        ShowSelection(words.SelectedIndex);
    }

    void ApplyLiveTime(Func<TimeSpan, bool> action)
    {
        if (pinnedPlayback() is { } playback) Apply(() => action(playback.Position));
    }

    bool Apply(Func<bool> mutation)
    {
        if (!mutation())
        {
            feedback.Text = "Timing is unchanged. Check the adjacent words or lines.";
            return false;
        }

        changed = true;
        int line = lines.SelectedIndex;
        int word = words.SelectedIndex;
        Rebuild(line, word);
        feedback.Text = "Timing updated in the preview.";
        return true;
    }

    void SaveTiming()
    {
        try
        {
            if (save(draft)) Close();
            else feedback.Text = "Could not save timing for this recording.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            feedback.Text = "Could not save timing for this recording.";
        }
    }

    void RestoreSource()
    {
        if (MessageBox.Show(this, "Discard the saved timing for this recording and return to its source timing?",
                "Restore source timing", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            if (restoreOriginal()) Close();
            else feedback.Text = "Could not restore source timing for this recording.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            feedback.Text = "Could not restore source timing for this recording.";
        }
    }

    Task TogglePlayback() => RunTransport(async () =>
    {
        if (pinnedPlayback() is not { } playback) return;
        replayRange = null;
        if (!await setPlaying(!playback.IsPlaying)) { feedback.Text = "The player could not change playback."; return; }
        following = true;
    });

    Task SeekHere() => RunTransport(async () =>
    {
        if (pinnedPlayback()?.CanSeek != true) return;
        replayRange = null;
        if (!await seek(previewPosition)) { feedback.Text = "The player could not seek to this time."; return; }
        following = true;
    });

    Task ReplaySelection() => RunTransport(async () =>
    {
        var playback = pinnedPlayback();
        if (playback is not { CanSeek: true } || SelectionRange() is not { } selected) return;
        var start = Max(TimeSpan.Zero, selected.Start - TimeSpan.FromSeconds(1));
        var end = Min(draft.Duration, Min(start + TimeSpan.FromSeconds(8),
            Max(start + TimeSpan.FromSeconds(2), selected.End + TimeSpan.FromSeconds(1))));
        replayRange = null;
        if (!await seek(start)) { feedback.Text = "The player could not replay this section."; return; }
        if (closing || pinnedPlayback() is not { } current) return;
        if (!current.IsPlaying && !await setPlaying(true)) { feedback.Text = "The player could not start the replay."; return; }
        if (closing || pinnedPlayback() is null) return;
        replayRange = new Range(start, end);
        replayStartedAt = Environment.TickCount64;
        replayReachedStart = false;
        following = true;
    });

    async Task RunTransport(Func<Task> action)
    {
        if (busy || closing || pinnedPlayback() is null) return;
        busy = true;
        UpdateButtons(pinnedPlayback());
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        { feedback.Text = "The current player could not complete that action."; }
        finally
        {
            busy = false;
            if (!closing) UpdatePlayback();
        }
    }

    void UpdatePlayback()
    {
        var playback = pinnedPlayback();
        if (playback is null)
        {
            replayRange = null;
            playerStatus.Text = "This recording is no longer playing. Preview and edit the pinned recording here.";
        }
        else
        {
            if (playback.Artwork is not null) UpdateCoverColor(playback.Artwork);
            if (following && !timeline.IsMouseCaptureWithin) SetTimeline(playback.Position);
            playerStatus.Text = playback.YouTubeVideoId is not null
                ? "Use YouTube to play, pause, or seek. This preview follows the video."
                : playback.CanSeek
                ? (following ? "Following the live playhead" : "Previewing a time on the timeline")
                : "This player cannot seek. You can still mark lines and words during playback.";
            if (replayRange is { } range) CheckReplay(playback, range);
        }
        RenderPreview();
        UpdateButtons(playback);
    }

    void UpdateCoverColor(byte[]? artwork)
    {
        if (ReferenceEquals(previewArtwork, artwork)) return;
        previewArtwork = artwork;
        preview.SetCoverColor(AlbumPalette.From(WidgetView.GetArtwork(artwork))?.First);
    }

    void CheckReplay(MediaSnapshot playback, Range range)
    {
        if (!replayReachedStart)
        {
            if (playback.Position >= range.Start - TimeSpan.FromMilliseconds(500) &&
                playback.Position <= range.Start + TimeSpan.FromMilliseconds(600))
                replayReachedStart = true;
            else if (Environment.TickCount64 - replayStartedAt > 2400)
                replayRange = null;
            return;
        }
        if (!playback.IsPlaying || playback.Position < range.Start - TimeSpan.FromSeconds(1) ||
            playback.Position > range.End + TimeSpan.FromSeconds(1))
        {
            replayRange = null;
            return;
        }
        if (playback.Position < range.End || busy) return;
        replayRange = null;
        _ = RunTransport(async () =>
        {
            if (pinnedPlayback()?.IsPlaying == true) await setPlaying(false);
        });
    }

    void UpdateButtons(MediaSnapshot? playback)
    {
        bool sameTrack = playback is not null && !busy;
        bool hasLine = lines.SelectedIndex >= 0;
        bool hasWord = words.SelectedIndex >= 0;
        var buttonLabel = playback?.IsPlaying == true ? "Pause" : "Play";
        if (!Equals(playPause.Content, buttonLabel)) playPause.Content = buttonLabel;
        playPause.IsEnabled = sameTrack && playback?.YouTubeVideoId is null;
        mark.IsEnabled = sameTrack && hasLine;
        seekHere.IsEnabled = sameTrack && playback?.CanSeek == true;
        replay.IsEnabled = seekHere.IsEnabled && hasLine;
        follow.IsEnabled = sameTrack && !following;
        applyLine.IsEnabled = hasLine;
        applyStart.IsEnabled = applyEnd.IsEnabled = hasWord;
        liveStart.IsEnabled = liveEnd.IsEnabled = sameTrack && hasWord;
    }

    void SetTimeline(TimeSpan value)
    {
        movingTimeline = true;
        timeline.Value = Math.Clamp(value.TotalSeconds, 0, timeline.Maximum);
        movingTimeline = false;
        ShowTime(TimeSpan.FromSeconds(timeline.Value));
    }

    void ShowTime(TimeSpan value)
    {
        previewPosition = value;
        timeReadout.Text = Format(value) + " / " + Format(draft.Duration);
        RenderPreview();
    }

    void RenderPreview() =>
        preview.Render(previewTrack, previewPosition, Environment.TickCount64, "This recording has no timed lyrics.", textSize);

    static TimingRow Row(TimeSpan time, string text) => new(Format(time), text, LyricFlow.DirectionFor(text));
    static string Format(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
    static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    static bool TryReadTime(string text, out TimeSpan time)
    {
        time = default;
        var pieces = text.Trim().Split(':');
        if (pieces.Length > 2) return false;
        if (!double.TryParse(pieces[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds)) return false;
        if (pieces.Length == 2)
        {
            if (!int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                minutes > 1440 || seconds >= 60) return false;
            seconds += minutes * 60d;
        }
        if (seconds < 0 || seconds > 86400) return false;
        time = TimeSpan.FromSeconds(seconds);
        return true;
    }

    sealed class CueStrip : FrameworkElement
    {
        TimeSpan duration;
        IReadOnlyList<TimeSpan> lines = [];
        readonly Pen baseline = new(Line, 1);
        readonly Pen tick = new(Soft, 1);
        public Range? Selection { get; set; }

        public void Replace(TimeSpan length, IReadOnlyList<TimeSpan> markers)
        {
            duration = length;
            lines = markers;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            if (duration <= TimeSpan.Zero || width < 1) return;
            double X(TimeSpan mark) => width * Math.Clamp(mark.TotalSeconds / duration.TotalSeconds, 0, 1);
            if (Selection is { } selected)
            {
                double start = X(selected.Start);
                double end = X(selected.End);
                dc.DrawRoundedRectangle(Field, null, new Rect(start, 0, Math.Max(2, end - start), 16), 3, 3);
            }
            dc.DrawLine(baseline, new Point(0, 12), new Point(width, 12));
            foreach (var mark in lines)
            {
                var x = X(mark);
                dc.DrawLine(tick, new Point(x, 7), new Point(x, 13));
            }
        }
    }
}
