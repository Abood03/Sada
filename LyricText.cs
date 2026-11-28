using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Sada;

internal sealed class LyricText
{
    readonly record struct TimedWord(Run Run, int TextStart, int[] LetterEnds, TimeSpan At, TimeSpan? End,
        TextEffectTarget[]?[] PrefixEffects, TextEffectTarget[]?[] FadeEffects);

    readonly TextBlock block;
    readonly Brush defaultForeground;
    readonly Color defaultForegroundColor;
    readonly SolidColorBrush coverForeground = new();
    readonly SolidColorBrush activeBrush = new(StudioUi.Mint.Color);
    readonly SolidColorBrush fadeBrush = new();
    readonly List<TimedWord> words = [];
    Color? coverColor;
    LyricsTrack? source;
    int line = -1;
    string text = "";
    double width;
    double height;
    double size;
    int displayedWord = -1;
    int highlightedLetters;
    TextEffectTarget[] activeEffects = [];
    TextEffectTarget[] fadeEffects = [];
    int fadedLetter = -1;
    double fadeMix;

    public LyricText(TextBlock block)
    {
        this.block = block;
        defaultForeground = block.Foreground;
        defaultForegroundColor = (defaultForeground as SolidColorBrush)?.Color ?? Colors.White;
        block.TextTrimming = TextTrimming.None;
    }

    internal void SetCoverColor(Color? color)
    {
        if (coverColor == color) return;
        coverColor = color;
        if (color is { } hue)
        {
            coverForeground.Color = ReadableCoverColor(hue);
            activeBrush.Color = Mix(coverForeground.Color, Colors.White, .62);
            block.Foreground = coverForeground;
        }
        else
        {
            activeBrush.Color = StudioUi.Mint.Color;
            block.Foreground = defaultForeground;
        }

        if (fadedLetter >= 0) UpdateFadeBrush(fadeMix);
    }

    internal static Color ReadableCoverColor(Color color)
    {
        // Move only toward white or black so the cover hue stays recognizable.
        // Leave a separate brightness band for the active letters.
        double brightness = color.R * .2126 + color.G * .7152 + color.B * .0722;
        if (brightness < 164) return Mix(color, Colors.White, (164 - brightness) / (255 - brightness));
        if (brightness > 205) return Mix(color, Colors.Black, (brightness - 205) / brightness);
        return Color.FromRgb(color.R, color.G, color.B);
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
            var part = new Run(text[end..nextEnd]);
            var wordStart = Math.Clamp(word.Start, end, nextEnd);
            var wordEnd = Math.Clamp(word.Start + word.Length, wordStart, nextEnd);
            while (wordStart < wordEnd && char.IsWhiteSpace(text[wordStart])) wordStart++;
            while (wordEnd > wordStart && char.IsWhiteSpace(text[wordEnd - 1])) wordEnd--;
            var letters = StringInfo.ParseCombiningCharacters(text[wordStart..wordEnd]);
            for (var i = 0; i < letters.Length; i++)
                letters[i] = i + 1 < letters.Length ? letters[i + 1] : wordEnd - wordStart;
            words.Add(new TimedWord(part, wordStart - end, letters, word.At, word.End,
                new TextEffectTarget[letters.Length][], new TextEffectTarget[letters.Length][]));
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
        var active = currentLine ? WordAt(position) : -1;

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
        var progression = elapsed * word.LetterEnds.Length;
        int count = Math.Min(word.LetterEnds.Length, 1 + (int)progression);
        if (active != displayedWord || count != highlightedLetters)
        {
            ClearHighlight();
            displayedWord = active;
            highlightedLetters = count;
            activeEffects = word.PrefixEffects[count - 1] ??= ResolveRange(word, 0, word.LetterEnds[count - 1], activeBrush);
            foreach (var effect in activeEffects) effect.Enable();
        }

        var fraction = progression - Math.Floor(progression);
        if (count >= word.LetterEnds.Length || fraction < .18 || !SystemParameters.ClientAreaAnimation)
        {
            ClearFade();
            return;
        }
        UpdateFadeBrush(Math.Clamp((fraction - .18) / .82, 0, 1));
        if (fadedLetter == count) return;
        ClearFade();
        fadedLetter = count;
        fadeEffects = word.FadeEffects[count] ??= ResolveRange(word, word.LetterEnds[count - 1], word.LetterEnds[count], fadeBrush);
        foreach (var effect in fadeEffects) effect.Enable();
    }

    void UpdateFadeBrush(double mix)
    {
        fadeMix = mix;
        var color = Mix(coverColor.HasValue ? coverForeground.Color : defaultForegroundColor, activeBrush.Color, mix);
        if (fadeBrush.Color != color) fadeBrush.Color = color;
    }

    int WordAt(TimeSpan position)
    {
        int low = 0, high = words.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (words[mid].At <= position) low = mid + 1;
            else high = mid;
        }
        return low - 1;
    }

    static TextEffectTarget[] ResolveRange(TimedWord word, int from, int to, Brush color)
    {
        var start = word.Run.ContentStart.GetPositionAtOffset(word.TextStart + from);
        var stop = word.Run.ContentStart.GetPositionAtOffset(word.TextStart + to);
        if (start is null || stop is null) return [];
        var targets = TextEffectResolver.Resolve(start, stop, new TextEffect());
        // Attach the shared brush after WPF clones the effect for each text range.
        foreach (var target in targets) target.TextEffect.Foreground = color;
        return targets;
    }

    static byte Blend(byte from, byte to, double mix) => (byte)Math.Round(from + (to - from) * mix);
    static Color Mix(Color from, Color to, double mix) =>
        Color.FromRgb(Blend(from.R, to.R, mix), Blend(from.G, to.G, mix), Blend(from.B, to.B, mix));

    void ClearFade()
    {
        foreach (var effect in fadeEffects) effect.Disable();
        fadeEffects = [];
        fadedLetter = -1;
    }

    void ClearHighlight()
    {
        ClearFade();
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
            pointSize, block.Foreground, dpi);

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
