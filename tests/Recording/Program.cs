using Sada;

static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

var source = LyricsTrack.Parse("[00:01.00]<00:01.00>نَجْم <00:01.45>ليلي<00:01.90>\n[00:04.00]<00:04.00>Café <00:04.50>stars<00:05.00>", duration: Ms(8000));
var a = new MediaSnapshot("النجم", "Example artist", "Original", null, Ms(0), Ms(8000), true, "Spotify.exe");
var b = a with { Duration = Ms(8050) };
var c = a with { Album = "Live version" };
var key = LyricRecordingKey.From(a)!.Value;
Check(LyricRecordingKey.From(a with { Title = "   النجم ", Artist = " EXAMPLE  ARTIST ", Duration = a.Duration.Add(TimeSpan.FromTicks(1)) }) == key,
    "key survives normalization and sub-millisecond reporting jitter");
Check(LyricRecordingKey.From(a with { Duration = TimeSpan.Zero }) is null &&
    LyricRecordingKey.From(b) != key && LyricRecordingKey.From(c) != key, "key distinguishes recordings and requires a valid duration");

var draft = new LyricTimingDraft(source, Ms(8000), Ms(200));
var firstPreview = draft.Preview();
Check(draft.Lines[0].At == Ms(800) && draft.WordsForLine(0)[0].At == Ms(800) &&
    draft.WordsForLine(0)[0].End == Ms(1250), "effective display offset is subtracted once at editor open");
Check(draft.HasAuthoredTiming(0) && draft.HasEditedTiming(0) && ReferenceEquals(draft.Preview(), firstPreview), "timing badges and immutable preview caching");
Check(draft.Shift(Ms(100)) && draft.Lines[0].At == Ms(900) && draft.Shift(Ms(-100)) &&
    draft.Lines[0].At == Ms(800), "both exact 100 ms whole-record directions");
Check(draft.MarkLine(0, Ms(1400)) && draft.WordsForLine(0)[0].At == Ms(1400) &&
    draft.WordsForLine(0)[0].End == Ms(1850), "tap moves line and all word intervals in raw playback units");
Check(draft.SetWordEnd(0, 0, Ms(1750)) && draft.SetWordStart(0, 1, Ms(1700)) &&
    draft.WordsForLine(0)[1].At == Ms(1750), "word boundaries preserve non-overlap");
Check(draft.SetWordEnd(0, 1, Ms(5000)) && draft.WordsForLine(0)[1].End == draft.Lines[1].At,
    "last word cannot cross the next line");
Check(firstPreview.Lines[0].At == Ms(800) && source.Lines[0].At == Ms(1000), "edits do not affect immutable or provider previews");
Check(draft.Preview().Lines[1].Text == "Café stars" && draft.Preview().WordsForLine(1)[0].Text == "Café" &&
    draft.Preview().WordsForLine(0)[0].Text == "نَجْم", "grapheme, RTL and word spans preserved");

var contiguous = new LyricTimingDraft(LyricsTrack.Parse("[00:01.00]First long line\n[00:03.00]Next line"), Ms(8000));
Check(contiguous.MarkLine(0, Ms(1800)) && contiguous.Lines[0].At == Ms(1800) &&
    contiguous.WordsForLine(0).Select(word => word.At).Distinct().Count() == 3 &&
    contiguous.WordsForLine(0)[^1].End == Ms(3000), "late tap sets exact line start and proportionally fits contiguous words");
Check(contiguous.MarkLine(1, Ms(2825)) && contiguous.Lines[1].At == Ms(2825) &&
    contiguous.WordsForLine(0)[^1].End == Ms(3000), "early tap accepts an overlap without editing a neighboring line");

var untimed = new LyricTimingDraft(LyricsTrack.Parse("First line\nثاني سطر"), Ms(9000));
Check(untimed.Lines.Count == 2 && !untimed.HasAuthoredTiming(0) && untimed.WordsForLine(1).Count == 2,
    "plain lyrics are editable with explicitly estimated timings");
var storeDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
var store = new LyricRecordingStore(storeDirectory);
Check(store.Load(key) is null, "missing override allows provider fallback");
store.Save(key, draft);
store = new LyricRecordingStore(storeDirectory);
var reread = store.Load(key)!;
Check(reread.Lines[0].At == draft.Lines[0].At && reread.WordsForLine(0).SequenceEqual(draft.WordsForLine(0)) &&
    new LyricTimingDraft(reread, Ms(8000)).HasEditedTiming(0), "exact edited words and badges persist without reapplying an offset");
Check(store.Load(LyricRecordingKey.From(b)!.Value) is null && store.Load(LyricRecordingKey.From(c)!.Value) is null,
    "saved correction applies only to the pinned recording after song change");
Check(store.Remove(key) && store.Load(key) is null && source.WordsForLine(0)[0].At == Ms(1000),
    "restore removes override while preserving provider data");
store.Save(key, draft);
var file = Directory.GetFiles(storeDirectory, "*.json").Single();
File.WriteAllText(file, "{invalid JSON");
Check(store.Load(key) is null && store.Remove(key) && Directory.GetFiles(storeDirectory).Length == 0,
    "corrupt local correction safely falls back");
