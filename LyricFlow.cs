using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sada;

internal sealed class LyricFlow : Grid
{
    readonly record struct Row(TextBlock Text, LyricText Content, TranslateTransform Offset, ScaleTransform Scale);
    readonly record struct ScrollTransition(double From, double To, double InitialSpeed, long Started, double Duration)
    {
        public bool IsRunning(long now) => Duration > 0 && now - Started < Duration;

        public (double Position, double Speed) At(long now)
        {
            if (!IsRunning(now)) return (To, 0);
            double progress = Math.Clamp((now - Started) / Duration, 0, 1);
            double remaining = 1 - progress;
            double distance = To - From;
            double position = From + distance * progress * progress * (3 - 2 * progress)
                + InitialSpeed * Duration * progress * remaining * remaining;
            double speed = distance * 6 * progress * remaining / Duration + InitialSpeed * remaining * (1 - 3 * progress);
            return (position, speed);
        }
    }

    // Three stationary lines plus one temporary slot so an exiting lyric
    // can finish moving before its row is reused for future text.
    readonly Row[] rows = new Row[4];
    readonly TextBlock message;
    LyricsTrack? track;
    int line = int.MinValue;
    long lastFrame;
    TimeSpan lastPosition;
    ScrollTransition transition;
    bool wasMoving;
    bool sizeChanged;
    string previousMessage = "";
    double previousSize;

    public LyricFlow()
    {
        ClipToBounds = true;
        for (var i = 0; i < rows.Length; i++)
        {
            var text = StudioUi.Text("", 24, StudioUi.Paper, true);
            text.TextAlignment = TextAlignment.Center;
            text.TextWrapping = TextWrapping.NoWrap;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(13, 0, 13, 0);
            text.Opacity = 0;
            text.Visibility = Visibility.Collapsed;
            text.RenderTransformOrigin = new Point(.5, .5);
            var scale = new ScaleTransform(1, 1);
            var offset = new TranslateTransform();
            text.RenderTransform = new TransformGroup { Children = { scale, offset } };
            rows[i] = new Row(text, new LyricText(text), offset, scale);
            Children.Add(text);
        }
        message = StudioUi.Text("", 15, StudioUi.Soft);
        message.TextAlignment = TextAlignment.Center;
        message.VerticalAlignment = VerticalAlignment.Center;
        message.MaxHeight = 90;
        message.Margin = new Thickness(13, 2, 13, 2);
        Children.Add(message);
    }

    internal void Render(LyricsTrack? lyrics, TimeSpan position, long now, string status, double textSize = 21)
    {
        textSize = Math.Clamp(textSize, 12, 34);
        if (lyrics is not { Lines.Count: > 0 })
        {
            if (!ReferenceEquals(track, lyrics) || previousMessage != status || line != int.MinValue)
            {
                var text = lyrics?.PlainText ?? "";
                message.Text = text.Length == 0 ? status : string.Join(Environment.NewLine,
                    text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
                message.FlowDirection = DirectionFor(message.Text);
                message.Visibility = Visibility.Visible;
                foreach (var row in rows) row.Text.Visibility = Visibility.Collapsed;
            }
            track = lyrics;
            previousMessage = status;
            line = int.MinValue;
            wasMoving = false;
            return;
        }

        var next = lyrics.IndexAt(position);
        var changedTrack = !ReferenceEquals(track, lyrics);
        var advanced = next != line;
        var actualDelta = (position - lastPosition).TotalMilliseconds;
        var seek = actualDelta < -80 || actualDelta > Math.Max(0, now - lastFrame) + 300;
        lastPosition = position;
        lastFrame = now;

        if (changedTrack || advanced)
        {
            bool naturalAdvance = !changedTrack && !seek && next == line + 1 && SystemParameters.ClientAreaAnimation;
            if (naturalAdvance)
            {
                var (from, speed) = transition.At(now);
                var remaining = next + 1 < lyrics.Lines.Count ? (lyrics.Lines[next + 1].At - position).TotalMilliseconds : 500;
                var duration = Math.Clamp(remaining * .82, 0, 245);
                if (duration >= 16)
                    transition = new(from, next, Math.Clamp(speed, 0, 2 * (next - from) / duration), now, duration);
                else transition = new(next, next, 0, now, 0);
            }
            else transition = new(next, next, 0, now, 0);
            track = lyrics;
            line = next;
            message.Visibility = Visibility.Collapsed;
        }
        else if (seek || wasMoving && !SystemParameters.ClientAreaAnimation)
            transition = new(line, line, 0, now, 0);

        bool moving = transition.IsRunning(now);
        if (!changedTrack && !advanced && !moving && !wasMoving && !sizeChanged && previousSize == textSize)
        {
            RowAt(line).Content.HighlightAt(position);
            return;
        }
        wasMoving = moving;
        sizeChanged = false;
        previousMessage = status;
        previousSize = textSize;

        var (scroll, _) = transition.At(now);
        double spacing = Math.Clamp(ActualHeight * .28, 15, 47);
        double rowHeight = Math.Min(ActualHeight / 3.1, spacing * .92);
        for (var i = line - 2; i <= line + 1; i++)
        {
            var row = RowAt(i);
            bool inRange = i >= 0 && i < lyrics.Lines.Count && (moving || i >= line - 1);
            if (!inRange || string.IsNullOrWhiteSpace(lyrics.Lines[i].Text))
            {
                row.Text.Visibility = Visibility.Collapsed;
                continue;
            }

            row.Text.Visibility = Visibility.Visible;
            row.Content.SetTimed(lyrics, i);
            row.Content.HighlightAt(position, currentLine: i == line);
            row.Content.Fit(ActualWidth - 26, rowHeight, textSize);
            double distance = i - scroll;
            double context = Math.Min(Math.Abs(distance), 1);
            double edgeOpacity = Math.Clamp(2 - Math.Abs(distance), 0, 1);
            row.Offset.Y = distance * spacing;
            row.Scale.ScaleX = row.Scale.ScaleY = 1 - context * .15;
            row.Text.Opacity = edgeOpacity * (1 - context * .28);
            SetZIndex(row.Text, i == line ? 1 : 0);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        sizeChanged = true;
    }

    Row RowAt(int index) => rows[(index % rows.Length + rows.Length) % rows.Length];

    internal static FlowDirection DirectionFor(string text)
    {
        foreach (var letter in text)
        {
            if (!char.IsLetter(letter)) continue;
            if (letter is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
                return FlowDirection.RightToLeft;
            return FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }
}
