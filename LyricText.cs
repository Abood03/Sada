using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Sada;

internal sealed class LyricText
{
    readonly record struct TimedWord(Run Run, int TextStart, int[] LetterEnds, TimeSpan At, TimeSpan? End);

    readonly TextBlock block;
    readonly Brush foreground;
    readonly List<TimedWord> words = [];
    LyricsTrack? source;
    int line = -1;
    string text = "";
    double width;
    double height;
    double size;
    int displayedWord = -1;
    int highlightedLetters;
    TextEffectTarget[] activeEffects = [];

    public LyricText(TextBlock block)
    {
        this.block = block;
        foreground = block.Foreground;
        block.TextTrimming = TextTrimming.None;
    }

    public void SetTimed(LyricsTrack track, int index)
    {
        if (ReferenceEquals(source, track) && line == index) return;
        if (index < 0 || index >= track.Lines.Count)
        {
            SetPlain("");
            return;
        }

        source = track;
        line = index;
        text = track.Lines[index].Text;
        block.FlowDirection = LyricFlow.DirectionFor(text);
        var timing = track.WordsForLine(index);
        ClearHighlight();
        words.Clear();
        block.Inlines.Clear();
        var end = 0;
        for (var n = 0; n < timing.Count; n++)
        {
            var word = timing[n];
            var nextEnd = n == timing.Count - 1 ? text.Length : Math.Min(text.Length, word.Start + word.Length);
            if (nextEnd <= end) continue;
            var part = new Run(text[end..nextEnd]) { Foreground = foreground };
            var wordStart = Math.Clamp(word.Start, end, nextEnd);
            var wordEnd = Math.Clamp(word.Start + word.Length, wordStart, nextEnd);
            while (wordStart < wordEnd && char.IsWhiteSpace(text[wordStart])) wordStart++;
            while (wordEnd > wordStart && char.IsWhiteSpace(text[wordEnd - 1])) wordEnd--;
            var letters = StringInfo.ParseCombiningCharacters(text[wordStart..wordEnd]);
            for (var i = 0; i < letters.Length; i++)
                letters[i] = i + 1 < letters.Length ? letters[i + 1] : wordEnd - wordStart;
            words.Add(new TimedWord(part, wordStart - end, letters, word.At, word.End));
            end = nextEnd;
            block.Inlines.Add(part);
        }
        if (words.Count == 0) block.Text = text;
        InvalidateMeasure();
    }

    public void SetPlain(string value)
    {
        if (source is null && text == value) return;
        source = null;
        line = -1;
        text = value;
        ClearHighlight();
        words.Clear();
        block.Inlines.Clear();
        block.Text = value;
        block.FlowDirection = LyricFlow.DirectionFor(value);
        InvalidateMeasure();
    }

    public void HighlightAt(TimeSpan position, bool currentLine = true)
    {
        if (words.Count == 0) return;
        var active = -1;
        if (currentLine)
            for (var i = 0; i < words.Count && words[i].At <= position; i++) active = i;

        if (active < 0)
        {
            if (displayedWord >= 0) ClearHighlight();
            return;
        }

        var word = words[active];
        var next = active + 1 < words.Count ? words[active + 1].At : word.At + TimeSpan.FromMilliseconds(450);
        var end = word.End ?? next;
        if (word.LetterEnds.Length == 0 || position > end + TimeSpan.FromMilliseconds(100))
        {
            if (displayedWord >= 0) ClearHighlight();
            return;
        }
        var elapsed = Math.Clamp((position - word.At).TotalMilliseconds / Math.Max(1, (end - word.At).TotalMilliseconds), 0, 1);
        int count = Math.Min(word.LetterEnds.Length, 1 + (int)(elapsed * word.LetterEnds.Length));
        if (active == displayedWord && count == highlightedLetters) return;
        ClearHighlight();
        displayedWord = active;
        highlightedLetters = count;

        var start = word.Run.ContentStart.GetPositionAtOffset(word.TextStart);
        var stop = word.Run.ContentStart.GetPositionAtOffset(word.TextStart + word.LetterEnds[count - 1]);
        if (start is null || stop is null) return;
        activeEffects = TextEffectResolver.Resolve(start, stop, new TextEffect { Foreground = StudioUi.Mint });
        foreach (var effect in activeEffects) effect.Enable();
    }

    void ClearHighlight()
    {
        foreach (var effect in activeEffects) effect.Disable();
        activeEffects = [];
        displayedWord = -1;
        highlightedLetters = 0;
    }

    public void Fit(double availableWidth, double availableHeight, double preferredSize)
    {
        if (availableWidth < 8 || availableHeight < 8) return;
        if (Math.Abs(width - availableWidth) < .5 && Math.Abs(height - availableHeight) < .5 && size == preferredSize) return;
        width = availableWidth;
        height = availableHeight;
        size = preferredSize;

        var family = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(block).PixelsPerDip;
        FormattedText Measure(double pointSize) => new(text, CultureInfo.CurrentUICulture, block.FlowDirection, family,
            pointSize, foreground, dpi);

        if (text.Length == 0)
        {
            block.FontSize = preferredSize;
            return;
        }
        var natural = Measure(preferredSize);
        double singleLineSize = Math.Min(preferredSize, (width - 2) * preferredSize / Math.Max(1, natural.WidthIncludingTrailingWhitespace));
        if (singleLineSize >= 14 && natural.Height * singleLineSize / preferredSize <= height)
        {
            block.TextWrapping = TextWrapping.NoWrap;
            block.FontSize = Math.Floor(singleLineSize * 4) / 4;
            return;
        }

        block.TextWrapping = TextWrapping.Wrap;
        double smaller = 5, larger = preferredSize;
        for (var n = 0; n < 9; n++)
        {
            var probe = (smaller + larger) / 2;
            var wrapped = Measure(probe);
            wrapped.MaxTextWidth = Math.Max(1, width - 2);
            if (wrapped.Height <= height) smaller = probe;
            else larger = probe;
        }
        block.FontSize = Math.Floor(smaller * 4) / 4;
    }

    void InvalidateMeasure() => width = 0;
}
