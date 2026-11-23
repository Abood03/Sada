using Sada;
using System.Reflection;
using System.Text.Json;

static void Expect(bool ok, string scenario)
{
    if (!ok) throw new Exception(scenario);
    Console.WriteLine("PASS " + scenario);
}

static int Milliseconds(LyricWord word) => (int)Math.Round(word.At.TotalMilliseconds);
static int EndMilliseconds(LyricWord word) => (int)Math.Round(word.End?.TotalMilliseconds ?? -1);

var enhanced = LyricsTrack.Parse("[offset:+125]\n[00:03.375][00:09.500]<00:03.375>يا <00:03.700>أهلاً!<00:04.100>\n[00:15.5]Keep going, friend !\n[00:17.5]آخر سطر\n[00:30.5]\n");
Expect(enhanced.Lines.Count == 5 && enhanced.Lines[0] == new LyricLine(TimeSpan.FromMilliseconds(3250), "يا أهلاً!"), "enhanced line text unchanged by tags and offset");
Expect(enhanced.WordsForLine(0).Select(Milliseconds).SequenceEqual(new[] { 3250, 3575 }), "enhanced timestamps honor LRC offset and ignore trailing tag");
Expect(enhanced.WordsForLine(1).Select(Milliseconds).SequenceEqual(new[] { 9375, 9700 }), "repeat timestamps translate exact word positions");
Expect(enhanced.WordsForLine(0).Select(EndMilliseconds).SequenceEqual(new[] { 3575, 3975 }) &&
       enhanced.WordsForLine(1).Select(EndMilliseconds).SequenceEqual(new[] { 9700, 10100 }),
       "enhanced LRC uses next and trailing markers, including repeated and offset-signed times");
Expect(enhanced.HasWordTiming(0) && enhanced.HasWordTiming(1) && !enhanced.HasWordTiming(2), "authored and estimated timings distinguishable");
var estimated = enhanced.WordsForLine(2);
Expect(estimated.Count == 3 && estimated[2].Text == "friend !" && estimated[0].At == enhanced.Lines[2].At && estimated[2].At < enhanced.Lines[3].At, "fallback word pacing preserves punctuation before next line");
Expect(estimated.All(word => enhanced.Lines[2].Text.Substring(word.Start, word.Length) == word.Text), "exact character ranges for rendering");
Expect(enhanced.IndexAt(TimeSpan.FromSeconds(18)) == 3 && enhanced.IndexAt(TimeSpan.FromSeconds(10)) == 1, "seeking in both directions locates correct line and its words");
Expect(enhanced.WordsForLine(4).Count == 0 && enhanced.WordsForLine(-1).Count == 0 && LyricsTrack.Parse("Untimed text.").WordsForLine(0).Count == 0, "blank and untimed lyrics have no artificial synchronization");
Expect(enhanced.WordsForLine(3)[^1].At < TimeSpan.FromSeconds(20), "instrumental breaks do not stretch estimated pronunciation");
Expect(enhanced.WordsForLine(3)[^1].End < TimeSpan.FromSeconds(20), "instrumental breaks remain outside the final sweep");
var slowEnglish = LyricsTrack.Parse("[00:01.000]Stay near\n[00:07.400]Next phrase\n[00:14.000]New phrase");
var slowArabic = LyricsTrack.Parse("[00:01.000]خليك قريب\n[00:07.400]جملة ثانية");
Expect(new[] { slowEnglish, slowArabic }.All(track => Milliseconds(track.WordsForLine(0)[1]) is > 1700 and < 2450 &&
       EndMilliseconds(track.WordsForLine(0)[1]) < 3600 && track.IndexAt(TimeSpan.FromSeconds(7.3)) == 0),
       "slow bilingual phrases start the second word before an instrumental rest without advancing the next line");
Expect(Math.Abs(EndMilliseconds(slowEnglish.WordsForLine(0)[^1]) - 1000 -
       (EndMilliseconds(slowEnglish.WordsForLine(1)[^1]) - 7400)) < 100,
       "similar slow gaps just below and above 6.5 seconds produce similar pacing");
var explicitlyHeld = LyricsTrack.Parse("[00:01.000]<00:01.000>Stay <00:04.800>near<00:06.500>\n[00:07.400]Next");
Expect(EndMilliseconds(explicitlyHeld.WordsForLine(0)[^1]) == 6500 && explicitlyHeld.HasWordTiming(0),
       "source word times preserve a slow held note verbatim");
var markerOnly = LyricsTrack.Parse("[00:02.000]One two<00:03.002>");
Expect(!markerOnly.HasWordTiming(0) && EndMilliseconds(markerOnly.WordsForLine(0)[^1]) == 3002,
       "a lone closing marker bounds an estimated line");
var shortFinal = LyricsTrack.Parse("[00:14.000]The final words", duration: TimeSpan.FromMilliseconds(14500));
Expect(shortFinal.WordsForLine(0).Zip(shortFinal.WordsForLine(0).Skip(1)).All(pair => pair.First.End == pair.Second.At) &&
       EndMilliseconds(shortFinal.WordsForLine(0)[^1]) == 14500, "fallback intervals do not overlap and stop at playback duration");
var zeroLength = LyricsTrack.Parse("[00:01.000]Last", duration: TimeSpan.FromSeconds(1));
Expect(EndMilliseconds(zeroLength.WordsForLine(0)[0]) == 1000, "end at final start does not invent time past the track");
var clippedAtZero = LyricsTrack.Parse("[offset:+900]\n[00:00.500]<00:00.500>A <00:00.700>B<00:01.200>");
Expect(EndMilliseconds(clippedAtZero.WordsForLine(0)[0]) == 0 && EndMilliseconds(clippedAtZero.WordsForLine(0)[1]) == 300,
       "offset clipping preserves nonnegative ordered intervals");
var earlierLrc = LyricsTrack.Parse("[00:02.140]<00:01.900>Early <00:02.200>words<00:02.900>");
Expect(earlierLrc.HasWordTiming(0) && earlierLrc.IndexAt(TimeSpan.FromSeconds(1.9)) == 0,
       "an authored enhanced word can bring the line forward");

const string document = """
  version: '1.0'
  metadata:
    title: 'Test'
    artist: 'Artist'
    offset_ms: 250
  lines:
    - text: 'يا هلا!'
      start_ms: 1200
      end_ms: 2000
      words:
        - text: 'يا '
          start_ms: 1200
        - { text: 'هلا!', start_ms: 1590 }
    - text: 山川
      start_ms: 2400
      words:
        - text: 山
          start_ms: 2400
        - text: 川
          start_ms: 2700
    - text: 'Just an estimate'
      start_ms: 2600
      end_ms: 3300
  plain: |
    Mixed example
  """;
var yaml = LyricsTrack.ParseLyricsFile(document) ?? throw new Exception("could not parse valid Lyricsfile");
Expect(yaml.HasAuthoredWordTiming && yaml.HasWordTiming(0) && yaml.HasWordTiming(1) && !yaml.HasWordTiming(2), "LRCLIB Lyricsfile supports a mixture of real and estimated words");
Expect(yaml.WordsForLine(0).Select(Milliseconds).SequenceEqual(new[] { 1200, 1590 }) && yaml.WordsForLine(0)[1].Start == 3, "author timestamps preserved and Arabic separators are intact");
Expect(yaml.Lines[1].Text == "山川" && yaml.WordsForLine(1)[1].Start == 1 && Milliseconds(yaml.WordsForLine(1)[1]) == 2700, "space-less authored words and overlapping lyrics preserved");
Expect(yaml.WordsForLine(2)[^1].At < TimeSpan.FromMilliseconds(3300), "authored line end constrains fallback pacing");
const string exactEnds = """
    version: '1.0'
    metadata: {duration_ms: 8500}
    lines:
      - text: 'Come now home'
        start_ms: 1650
        end_ms: 3200
        words:
          - {text: 'Come ', start_ms: 1475, end_ms: 1850}
          - {text: 'now ', start_ms: 2200, end_ms: 2950}
          - {text: 'home', start_ms: 2600, end_ms: 3700}
      - {text: 'Then it ends', start_ms: 7900}
    """;
var precise = LyricsTrack.ParseLyricsFile(exactEnds) ?? throw new Exception("structured timing rejected");
Expect(precise.HasWordTiming(0) && precise.IndexAt(TimeSpan.FromMilliseconds(1475)) == 0,
    "valid authored YAML words can start ahead of their parent line");
Expect(precise.WordsForLine(0).Select(EndMilliseconds).SequenceEqual(new[] { 1850, 2600, 3200 }),
    "format preserves explicit word gaps and bounds overlapping endings");
Expect(EndMilliseconds(precise.WordsForLine(1)[^1]) == 8500, "Lyricsfile duration_ms bounds its final estimated word");
Expect(LyricsTrack.ParseLyricsFile(document.Replace("'1.0'", "'2.0'")) is null, "unknown Lyricsfile version rejected");
Expect(LyricsTrack.ParseLyricsFile("lines: [") is null, "malformed Lyricsfile gracefully rejected");
Expect(LyricsTrack.ParseLyricsFile("plain: &p hello\nlines: *p") is null, "untrusted aliases rejected");
Expect(LyricsTrack.ParseLyricsFile("plain: " + new string('x', 270_000)) is null, "oversized YAML rejected");
var malformed = LyricsTrack.Parse("[00:01.00]<00:01.60>Broken <00:01.05>order\n[00:03]Following line");
Expect(!malformed.HasWordTiming(0) && malformed.WordsForLine(0).Count == 2, "non-monotonic authored times fall back safely");

var readResponse = typeof(MediaService).GetMethod("ParseRecord", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("Provider method not found");
LyricsTrack? ReadMockResponse(bool wordSync, string body)
{
    using var response = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        hasWordSync = wordSync,
        lyricsfile = body,
        syncedLyrics = "[00:01.20]يا هلا!\n[00:02.60]Just an estimate",
        plainLyrics = "يا هلا!\nJust an estimate"
    }));
    return (LyricsTrack?)readResponse.Invoke(null, new object[] { response.RootElement, TimeSpan.FromMilliseconds(3200) });
}
Expect(ReadMockResponse(true, document) is { HasAuthoredWordTiming: true }, "LRCLIB response consumes the actual word timing document");
Expect(ReadMockResponse(true, "lines: [") is { HasAuthoredWordTiming: false, Lines.Count: 2 }, "malformed word timing response falls back to legacy lines");
Expect(ReadMockResponse(false, document) is { HasAuthoredWordTiming: false, Lines.Count: 2 }, "legacy provider records retain estimated word mode");
Expect(EndMilliseconds(ReadMockResponse(false, document)!.WordsForLine(1)[^1]) == 3200,
    "chosen Spotify playback duration bounds remote fallback timing");
