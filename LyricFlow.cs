using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sada;

internal sealed class LyricFlow : Grid
{
    readonly record struct Row(TextBlock Text, LyricText Content, TranslateTransform Offset, ScaleTransform Scale);

    readonly Row[] rows = new Row[3];
    readonly TextBlock message;
    LyricsTrack? track;
    int line = int.MinValue;
    long lastFrame;
    TimeSpan lastPosition;
    long transitionStart;
    double transitionLength;
    double fromLine;
    double targetLine;
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
            var naturalAdvance = !changedTrack && !seek && next == line + 1;
            fromLine = naturalAdvance ? PositionAt(now) : next;
            targetLine = next;
            transitionStart = now;
            var remaining = next + 1 < lyrics.Lines.Count ? (lyrics.Lines[next + 1].At - position).TotalMilliseconds : 500;
            transitionLength = naturalAdvance ? Math.Clamp(remaining * .65, 80, 220) : 0;
            track = lyrics;
            line = next;
            message.Visibility = Visibility.Collapsed;

            for (var i = next - 1; i <= next + 1; i++)
            {
                var row = RowAt(i);
                string text = i >= 0 && i < lyrics.Lines.Count ? lyrics.Lines[i].Text : "";
                row.Content.SetTimed(lyrics, i);
                row.Text.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        else if (seek)
        {
            transitionLength = 0;
        }
        else if (transitionLength == 0 && previousMessage == status && previousSize == textSize)
        {
            RowAt(line).Content.HighlightAt(position);
            return;
        }
        previousMessage = status;
        previousSize = textSize;

        if (now >= transitionStart + transitionLength) transitionLength = 0;
        var scroll = PositionAt(now);
        var spacing = Math.Clamp(ActualHeight / 3.9, 25, 41);
        for (var i = line - 1; i <= line + 1; i++)
        {
            var row = RowAt(i);
            if (row.Text.Visibility != Visibility.Visible) continue;
            row.Content.HighlightAt(position, currentLine: i == line);
            row.Content.Fit(ActualWidth - 26, Math.Min(55, Math.Max(25, ActualHeight * .38)), textSize);
            var distance = i - scroll;
            var context = Math.Min(Math.Abs(distance), 1);
            row.Offset.Y = Math.Clamp(distance, -1.2, 1.2) * spacing;
            row.Scale.ScaleX = row.Scale.ScaleY = 1 - context * .15;
            row.Text.Opacity = 1 - context * .28;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        previousMessage = "\0";
    }

    Row RowAt(int index) => rows[(index % rows.Length + rows.Length) % rows.Length];

    double PositionAt(long now)
    {
        if (transitionLength <= 0) return targetLine;
        var t = Math.Clamp((now - transitionStart) / transitionLength, 0, 1);
        return fromLine + (targetLine - fromLine) * (1 - Math.Pow(1 - t, 3));
    }

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
