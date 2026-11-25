using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sada;

internal readonly record struct LyricRecordingKey(string Value)
{
    internal static LyricRecordingKey? From(MediaSnapshot song)
    {
        if (!song.HasTrack || song.Duration <= TimeSpan.Zero)
            return null;

        var identity = song.YouTubeVideoId is { } videoId ? "youtube:" + videoId : JsonSerializer.Serialize(new[]
        {
            Normalize(song.Source), Normalize(song.Artist), Normalize(song.Title), Normalize(song.Album),
            Math.Round(song.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        });
        return new LyricRecordingKey(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

internal sealed record LyricRecordingData(TimeSpan Duration, string PlainText, LyricLine[] Lines,
    LyricWord[][] Words, bool[] Authored, bool[] Edited);

internal sealed class LyricTimingDraft
{
    readonly LyricLine[] lines;
    readonly IReadOnlyList<LyricWord>[] words;
    readonly bool[] authored;
    readonly bool[] edited;
    readonly string plainText;
    LyricsTrack? preview;

    public LyricTimingDraft(LyricsTrack source, TimeSpan duration, TimeSpan currentOffset = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        Duration = duration;
        if (source.Lines.Count == 0 && !string.IsNullOrWhiteSpace(source.PlainText))
            source = LyricsTrack.CreateStudioEstimate(source.PlainText, duration);

        plainText = source.PlainText;
        lines = source.Lines.ToArray();
        Lines = Array.AsReadOnly(lines);
        words = new IReadOnlyList<LyricWord>[lines.Length];
        authored = new bool[lines.Length];
        edited = new bool[lines.Length];
        for (int index = 0; index < lines.Length; index++)
        {
            words[index] = Array.AsReadOnly(source.WordsForLine(index).ToArray());
            authored[index] = source.HasWordTiming(index);
            edited[index] = source.HasStudioEdit(index);
        }
        ShiftTicks(-(decimal)currentOffset.Ticks);
    }

    public TimeSpan Duration { get; }
    public IReadOnlyList<LyricLine> Lines { get; }

    public IReadOnlyList<LyricWord> WordsForLine(int index) =>
        IsLine(index) ? words[index] : Array.Empty<LyricWord>();

    public bool HasAuthoredTiming(int index) => IsLine(index) && authored[index];
    public bool HasEditedTiming(int index) => IsLine(index) && edited[index];

    public bool Shift(TimeSpan amount) => ShiftTicks(amount.Ticks);

    public bool MarkLine(int index, TimeSpan now)
    {
        if (!IsLine(index)) return false;

        var line = lines[index];
        var earliest = index > 0 ? lines[index - 1].At : TimeSpan.Zero;
        var boundary = NextLine(index);
        // A tap sets the actual line start even when the preceding lyric's
        // recorded tail overlaps it. Only the line being moved is edited.
        var latest = index + 1 < lines.Length && boundary > earliest && boundary > line.At
            ? boundary - TimeSpan.FromTicks(1) : boundary;
        var at = Clamp(now, earliest, latest);
        if (at == line.At) return false;

        var ending = words[index].Aggregate(line.At, (last, word) => Max(last, word.End ?? word.At));
        var span = (ending - line.At).Ticks;
        var available = (boundary - at).Ticks;
        var scale = span > available ? (decimal)available / span : 1;
        ShiftLine(index, (at - line.At).Ticks, boundary, scale);
        Edited(index);
        return true;
    }

    public bool SetWordStart(int line, int word, TimeSpan at)
    {
        if (!IsWord(line, word)) return false;

        var list = words[line];
        var item = list[word];
        var earliest = word == 0 ? lines[line].At : Min(list[word - 1].End ?? list[word - 1].At, item.At);
        var latest = item.End ?? WordBoundary(line, word);
        var next = Clamp(at, earliest, Max(earliest, latest));
        return UpdateWord(line, word, item with { At = next });
    }

    public bool SetWordEnd(int line, int word, TimeSpan end)
    {
        if (!IsWord(line, word)) return false;

        var item = words[line][word];
        var next = Clamp(end, item.At, WordBoundary(line, word));
        return UpdateWord(line, word, item with { End = next });
    }

    TimeSpan WordBoundary(int line, int word)
    {
        var list = words[line];
        var next = word + 1 < list.Count ? list[word + 1].At : NextLine(line);
        return Max(list[word].At, Min(next, Duration));
    }

    TimeSpan NextLine(int line) => line + 1 < lines.Length ? lines[line + 1].At : Duration;

    bool UpdateWord(int line, int word, LyricWord next)
    {
        if (words[line][word] == next) return false;
        var replacement = words[line].ToArray();
        replacement[word] = next;
        words[line] = Array.AsReadOnly(replacement);
        Edited(line);
        return true;
    }

    bool ShiftTicks(decimal ticks)
    {
        bool changed = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!ShiftLine(index, ticks)) continue;
            Edited(index);
            changed = true;
        }
        return changed;
    }

    bool ShiftLine(int index, decimal ticks, TimeSpan? boundary = null, decimal scale = 1)
    {
        var previous = lines[index];
        var limit = boundary ?? Duration;
        var at = ShiftTime(previous.At, ticks, limit);
        bool changed = at != previous.At;
        var replacement = new LyricWord[words[index].Count];
        for (int word = 0; word < replacement.Length; word++)
        {
            var item = words[index][word];
            replacement[word] = item with
            {
                At = ShiftTime(previous.At, ticks + (item.At - previous.At).Ticks * scale, limit),
                End = item.End is { } end ? ShiftTime(previous.At, ticks + (end - previous.At).Ticks * scale, limit) : null
            };
            changed |= replacement[word] != item;
        }
        if (!changed) return false;
        lines[index] = previous with { At = at };
        words[index] = Array.AsReadOnly(replacement);
        return true;
    }

    static TimeSpan ShiftTime(TimeSpan at, decimal ticks, TimeSpan upperBound) =>
        TimeSpan.FromTicks((long)Math.Clamp(at.Ticks + ticks, 0, (decimal)upperBound.Ticks));

    void Edited(int line)
    {
        edited[line] = true;
        preview = null;
    }

    bool IsLine(int index) => (uint)index < (uint)lines.Length;
    bool IsWord(int line, int word) => IsLine(line) && (uint)word < (uint)words[line].Count;
    static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
    static TimeSpan Clamp(TimeSpan value, TimeSpan lower, TimeSpan upper) => Max(lower, Min(value, upper));

    public LyricsTrack Preview() => preview ??= LyricsTrack.FromStudioTiming(Snapshot());

    internal LyricRecordingData Snapshot() => new(Duration, plainText, lines.ToArray(),
        words.Select(line => line.ToArray()).ToArray(), authored.ToArray(), edited.ToArray());
}

internal sealed class LyricRecordingStore(string folder)
{
    const long MaximumFileSize = 12 * 1024 * 1024;

    public LyricsTrack? Load(LyricRecordingKey key)
    {
        string path = RecordingPath(key);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumFileSize) return null;
            var data = JsonSerializer.Deserialize<LyricRecordingData>(stream);
            return data is not null && Validate(data) ? LyricsTrack.FromStudioTiming(data) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(LyricRecordingKey key, LyricTimingDraft draft)
    {
        string path = RecordingPath(key);
        var data = draft.Snapshot();
        if (!Validate(data))
            throw new InvalidDataException("The recording contains invalid lyric timestamps.");

        Directory.CreateDirectory(folder);
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data);
                if (stream.Length > MaximumFileSize)
                    throw new InvalidDataException("The edited lyrics are too large to save.");
                stream.Flush(flushToDisk: true);
            }
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    public bool Remove(LyricRecordingKey key)
    {
        string path = RecordingPath(key);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    string RecordingPath(LyricRecordingKey key)
    {
        if (key.Value is not { Length: 64 } || !key.Value.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid lyric recording identity.", nameof(key));
        return Path.Combine(folder, key.Value + ".json");
    }

    static bool Validate(LyricRecordingData data)
    {
        if (data.Duration <= TimeSpan.Zero || data.PlainText is null || data.Lines is null ||
            data.Words is null || data.Authored is null || data.Edited is null ||
            data.Lines.Length == 0 || data.Lines.Length != data.Words.Length ||
            data.Lines.Length != data.Authored.Length || data.Lines.Length != data.Edited.Length)
            return false;

        var previousLine = TimeSpan.Zero;
        for (int index = 0; index < data.Lines.Length; index++)
        {
            var line = data.Lines[index];
            if (line.Text is null || data.Words[index] is not { } words ||
                line.At < previousLine || line.At > data.Duration) return false;

            previousLine = line.At;
            var previousWord = line.At;
            int previousEnd = 0;
            foreach (var word in words)
            {
                if (word.Text is null || word.Start < previousEnd || word.Length <= 0 ||
                    word.Start > line.Text.Length - word.Length || word.At < previousWord ||
                    word.At > data.Duration || word.End is { } end && (end < word.At || end > data.Duration) ||
                    !line.Text.AsSpan(word.Start, word.Length).SequenceEqual(word.Text)) return false;
                previousEnd = word.Start + word.Length;
                previousWord = word.End ?? word.At;
            }
        }
        return true;
    }
}

public sealed partial class LyricsTrack
{
    private readonly bool[] studioEdited = [];

    private LyricsTrack(LyricRecordingData data)
    {
        Lines = Array.AsReadOnly(data.Lines.ToArray());
        PlainText = data.PlainText;
        wordTimings = data.Words.Select(line => (IReadOnlyList<LyricWord>)Array.AsReadOnly(line.ToArray())).ToArray();
        authoredTimings = data.Authored.ToArray();
        studioEdited = data.Edited.ToArray();
        HasAuthoredWordTiming = Array.Exists(authoredTimings, authored => authored);
    }

    internal static LyricsTrack FromStudioTiming(LyricRecordingData data) => new(data);

    internal bool HasStudioEdit(int index) => (uint)index < (uint)studioEdited.Length && studioEdited[index];

    internal static LyricsTrack CreateStudioEstimate(string plainText, TimeSpan duration)
    {
        var lines = plainText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var estimated = lines.Select((text, index) => new ParsedLine(
            new LyricLine(TimeSpan.FromTicks((long)((decimal)duration.Ticks * index / lines.Length)), text),
            Array.Empty<LyricWord>()));
        return new LyricsTrack(estimated, plainText, duration);
    }
}
