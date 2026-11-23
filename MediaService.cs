using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using MediaSessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Sada;

public sealed record MediaSnapshot(
    string Title, string Artist, string Album, byte[]? Artwork, TimeSpan Position,
    TimeSpan Duration, bool IsPlaying, string Source)
{
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
    internal string LyricsKey => HasTrack ? string.Join("\n", Source, Artist, Title, Album) : "";

    internal DateTimeOffset LastUpdated { get; init; }
    internal double PlaybackRate { get; init; } = 1;
#if SADA_LYRICS_STUDIO
    internal bool CanSeek { get; init; }
#endif

    internal static MediaSnapshot Empty { get; } = new("", "", "", null, TimeSpan.Zero,
        TimeSpan.Zero, false, "");
}

public sealed class MediaService : IDisposable
{
    private const int MaxArtworkBytes = 12 * 1024 * 1024;
    private const int MaxLyricsFileBytes = 2 * 1024 * 1024;

    private static readonly HttpClient LyricsClient = CreateLyricsClient();
    private static readonly SemaphoreSlim LyricsNetwork = new(1);
    private static DateTimeOffset lyricsRequestAfter;

    private readonly SemaphoreSlim sessionAccess = new(1);
    private MediaSessionManager? manager;
    private MediaSession? session;
    private MediaSnapshot current = MediaSnapshot.Empty;
    private int metadataVersion;
    private int loadedMetadataVersion = -1;
    private DateTimeOffset nextMetadataCheck;
    private volatile bool disposed;

    public MediaSnapshot Current
    {
        get
        {
            MediaSnapshot snapshot = Volatile.Read(ref current);
            if (!snapshot.IsPlaying || snapshot.LastUpdated == default)
                return snapshot;

            double elapsed = Math.Clamp((DateTimeOffset.UtcNow - snapshot.LastUpdated).TotalSeconds, 0, 86_400);
            TimeSpan position = snapshot.Position + TimeSpan.FromSeconds(elapsed * snapshot.PlaybackRate);
            if (snapshot.Duration > TimeSpan.Zero && position > snapshot.Duration)
                position = snapshot.Duration;

            return snapshot with { Position = position };
        }
    }

    public async Task RefreshAsync()
    {
        if (disposed || !await sessionAccess.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            if (disposed)
                return;

            manager ??= await MediaSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            SetSession(ChooseSession(manager));
            MediaSession? active = session;
            if (active is null || disposed)
            {
                Volatile.Write(ref current, MediaSnapshot.Empty);
                return;
            }

            MediaSnapshot snapshot = Volatile.Read(ref current);
            int version = Volatile.Read(ref metadataVersion);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (version != loadedMetadataVersion || now >= nextMetadataCheck)
            {
                var properties = await active.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                string title = properties?.Title ?? "";
                string artist = properties?.Artist ?? "";
                string album = properties?.AlbumTitle ?? "";
                bool metadataChanged = title != snapshot.Title || artist != snapshot.Artist || album != snapshot.Album;
                byte[]? artwork = snapshot.Artwork;
                if (metadataChanged || version != loadedMetadataVersion || artwork is null)
                {
                    byte[]? latest = await ReadArtworkAsync(properties?.Thumbnail).ConfigureAwait(false);
                    if (latest is not null && artwork is not null && artwork.AsSpan().SequenceEqual(latest))
                        latest = artwork;
                    artwork = latest;
                }

                snapshot = snapshot with { Title = title, Artist = artist, Album = album, Artwork = artwork };
                loadedMetadataVersion = version;
                nextMetadataCheck = now.AddSeconds(12);
            }

            var playback = active.GetPlaybackInfo();
            var timeline = active.GetTimelineProperties();
            TimeSpan duration = timeline.EndTime - timeline.StartTime;
            TimeSpan position = timeline.Position - timeline.StartTime;
            double rate = playback?.PlaybackRate ?? 1;
            bool hasTimeline = timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch &&
                (duration > TimeSpan.Zero || position > TimeSpan.Zero);

            if (!disposed)
            {
                Volatile.Write(ref current, snapshot with
                {
                    Position = position > TimeSpan.Zero ? position : TimeSpan.Zero,
                    Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                    IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    Source = active.SourceAppUserModelId,
                    LastUpdated = hasTimeline ? timeline.LastUpdatedTime : default,
                    PlaybackRate = double.IsFinite(rate) && rate > 0 && rate <= 16 ? rate : 1,
#if SADA_LYRICS_STUDIO
                    CanSeek = playback?.Controls?.IsPlaybackPositionEnabled == true
#endif
                });
            }
        }
        catch (Exception exception) when (IsMediaFailure(exception))
        {
            SetSession(null);
            manager = null;
            Volatile.Write(ref current, MediaSnapshot.Empty);
        }
        finally
        {
            sessionAccess.Release();
        }
    }

    public Task<bool> ToggleAsync() => SendAsync(active => active.TryTogglePlayPauseAsync());
    public Task<bool> PreviousAsync() => SendAsync(active => active.TrySkipPreviousAsync());
    public Task<bool> NextAsync() => SendAsync(active => active.TrySkipNextAsync());

    public Task<bool> SeekAsync(TimeSpan position) => SendAsync(active =>
    {
        var timeline = active.GetTimelineProperties();
        TimeSpan target = position < TimeSpan.Zero ? timeline.StartTime : timeline.StartTime + position;
        if (timeline.EndTime > timeline.StartTime && target > timeline.EndTime)
            target = timeline.EndTime;
        return active.TryChangePlaybackPositionAsync(target.Ticks);
    });

#if SADA_LYRICS_STUDIO
    internal Task<bool> SeekRecordingAsync(LyricRecordingKey recording, TimeSpan position) =>
        SendRecordingCommandAsync(recording, active =>
        {
            if (active.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled != true) return Task.FromResult(false);
            var timeline = active.GetTimelineProperties();
            long max = (timeline.EndTime - timeline.StartTime).Ticks;
            if (max <= 0) return Task.FromResult(false);
            return active.TryChangePlaybackPositionAsync(timeline.StartTime.Ticks + Math.Clamp(position.Ticks, 0, max)).AsTask();
        });

    internal Task<bool> SetRecordingPlayingAsync(LyricRecordingKey recording, bool playing) =>
        SendRecordingCommandAsync(recording, async active =>
        {
            var state = active.GetPlaybackInfo()?.PlaybackStatus;
            if (state is null) return false;
            if (playing && state == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true;
            if (!playing && state == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) return true;
            return await (playing ? active.TryPlayAsync() : active.TryPauseAsync()).AsTask().ConfigureAwait(false);
        });

    private async Task<bool> SendRecordingCommandAsync(LyricRecordingKey recording, Func<MediaSession, Task<bool>> command)
    {
        if (disposed) return false;
        await sessionAccess.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || LyricRecordingKey.From(Current) != recording) return false;
            manager ??= await MediaSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            SetSession(ChooseSession(manager));
            if (session is not { } active) return false;
            int version = Volatile.Read(ref metadataVersion);
            var metadata = await active.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            if (disposed || metadata is null || Volatile.Read(ref metadataVersion) != version) return false;
            var timeline = active.GetTimelineProperties();
            var actual = new MediaSnapshot(metadata.Title, metadata.Artist, metadata.AlbumTitle, null, TimeSpan.Zero,
                timeline.EndTime - timeline.StartTime, false, active.SourceAppUserModelId);
            return LyricRecordingKey.From(actual) == recording && await command(active).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsMediaFailure(exception)) { return false; }
        finally { sessionAccess.Release(); }
    }
#endif

    public async Task<LyricsTrack> LoadLyricsAsync(string path)
    {
        if (new FileInfo(path).Length > MaxLyricsFileBytes)
            throw new InvalidDataException("The lyrics file must be under 2 MB.");

        string text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        if (Path.GetExtension(path).ToLowerInvariant() is ".lyricsfile" or ".yaml" or ".yml")
            return LyricsTrack.ParseLyricsFile(text) ?? throw new FormatException("This file does not contain supported Lyricsfile lyrics.");
        return LyricsTrack.Parse(text);
    }

    public Task<LyricsTrack?> FetchLyricsAsync(CancellationToken cancellationToken = default) =>
        FetchLyricsAsync(Current, cancellationToken);

    public async Task<LyricsTrack?> FetchLyricsAsync(MediaSnapshot track, CancellationToken cancellationToken = default)
    {
        if (!track.HasTrack || string.IsNullOrWhiteSpace(track.Artist))
            return null;

        var signature = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
        var query = signature;
        if (!string.IsNullOrWhiteSpace(track.Album))
            query += $"&album_name={Uri.EscapeDataString(track.Album)}";
        int duration = (int)Math.Round(track.Duration.TotalSeconds);
        if (duration is >= 1 and <= 3600)
            query += $"&duration={duration.ToString(CultureInfo.InvariantCulture)}";

        using var direct = await RequestLyricsAsync("api/get?" + query, cancellationToken).ConfigureAwait(false);
        var exact = direct?.RootElement is { } record ? ParseRecord(record, track.Duration) : null;
        if (exact is { Lines.Count: > 0 } || direct is not null && IsInstrumental(direct.RootElement))
            return exact;

        using var search = await RequestLyricsAsync("api/search?" + signature, cancellationToken).ConfigureAwait(false);
        if (search?.RootElement.ValueKind != JsonValueKind.Array) return exact;

        var title = Normalize(track.Title);
        var artist = Normalize(track.Artist);
        var album = Normalize(track.Album);
        LyricsTrack? best = exact;
        double bestScore = exact is null ? double.NegativeInfinity : 0;
        foreach (var candidate in search.RootElement.EnumerateArray())
        {
            if (Normalize(ReadString(candidate, "trackName")) != title || Normalize(ReadString(candidate, "artistName")) != artist)
                continue;
            bool sameAlbum = album.Length > 0 && Normalize(ReadString(candidate, "albumName")) == album;
            double difference = candidate.TryGetProperty("duration", out var seconds) && seconds.TryGetDouble(out var length)
                ? Math.Abs(length - duration) : double.PositiveInfinity;
            if (duration > 0 && difference > (sameAlbum ? 7 : 4)) continue;

            var lyrics = ParseRecord(candidate, track.Duration);
            if (lyrics is null) continue;
            var score = (lyrics.Lines.Count > 0 ? 30 : 0) + (lyrics.HasAuthoredWordTiming ? 9 : 0) + (sameAlbum ? 8 : 0) +
                (duration > 0 ? 7 - difference : 0);
            if (score > bestScore) { best = lyrics; bestScore = score; }
        }
        return best;
    }

    private static bool IsInstrumental(JsonElement record) =>
        record.TryGetProperty("instrumental", out var flag) && flag.ValueKind == JsonValueKind.True;

    private static LyricsTrack? ParseRecord(JsonElement record, TimeSpan playbackDuration)
    {
        if (IsInstrumental(record)) return null;
        TimeSpan? duration = playbackDuration > TimeSpan.Zero ? playbackDuration : null;
        if (duration is null && record.TryGetProperty("duration", out var stored) &&
            stored.TryGetDouble(out var seconds) && seconds is > 0 and <= 86_400)
            duration = TimeSpan.FromSeconds(seconds);
        var timed = ReadString(record, "syncedLyrics");
        var plain = ReadString(record, "plainLyrics");
        if (record.TryGetProperty("hasWordSync", out var wordSync) && wordSync.ValueKind == JsonValueKind.True &&
            ReadString(record, "lyricsfile") is { } rich)
        {
            var lyrics = LyricsTrack.ParseLyricsFile(rich, plain, duration);
            if (lyrics is { HasAuthoredWordTiming: true }) return lyrics;
        }
        if (string.IsNullOrWhiteSpace(timed)) return string.IsNullOrWhiteSpace(plain) ? null : LyricsTrack.Parse(plain);
        return LyricsTrack.Parse(timed, plain, duration);
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return new string(text.Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static async Task<JsonDocument?> RequestLyricsAsync(string uri, CancellationToken cancellationToken)
    {
        await LyricsNetwork.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = lyricsRequestAfter - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.FromSeconds(1)) throw new LyricsRetryException(wait);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            using var response = await LyricsClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(20);
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                lyricsRequestAfter = DateTimeOffset.UtcNow + delay + TimeSpan.FromMilliseconds(250);
                throw new LyricsRetryException(delay);
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var next = DateTimeOffset.UtcNow.AddMilliseconds(250);
            if (lyricsRequestAfter < next) lyricsRequestAfter = next;
            LyricsNetwork.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Volatile.Write(ref current, MediaSnapshot.Empty);
        _ = DetachAsync();
    }

    private async Task DetachAsync()
    {
        await sessionAccess.WaitAsync().ConfigureAwait(false);
        try
        {
            SetSession(null);
            manager = null;
        }
        finally
        {
            sessionAccess.Release();
        }
    }

    private async Task<bool> SendAsync(Func<MediaSession, IAsyncOperation<bool>> command)
    {
        if (disposed)
            return false;

        await sessionAccess.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return false;

            manager ??= await MediaSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            SetSession(ChooseSession(manager));
            return session is { } active && await command(active).AsTask().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsMediaFailure(exception))
        {
            return false;
        }
        finally
        {
            sessionAccess.Release();
        }
    }

    private static MediaSession? ChooseSession(MediaSessionManager manager)
    {
        MediaSession? selected = manager.GetCurrentSession();
        if (selected is not null && IsSpotify(selected) && IsPlaying(selected))
            return selected;
        var available = manager.GetSessions();
        var spotify = available.FirstOrDefault(IsSpotify);
        if (spotify is not null && IsPlaying(spotify)) return spotify;
        if (selected is not null && IsPlaying(selected)) return selected;
        return available.FirstOrDefault(IsPlaying) ?? spotify ?? selected;
    }

    private static bool IsSpotify(MediaSession session) =>
        session.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlaying(MediaSession candidate)
    {
        try
        {
            return candidate.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception exception) when (IsMediaFailure(exception))
        {
            return false;
        }
    }

    private void SetSession(MediaSession? selected)
    {
        if (ReferenceEquals(selected, session))
            return;

        if (session is { } previous)
        {
            try
            {
                previous.MediaPropertiesChanged -= MediaPropertiesChanged;
            }
            catch (Exception exception) when (IsMediaFailure(exception))
            {
                // A player may disappear before Windows delivers the session change.
            }
        }

        session = selected;
        Interlocked.Increment(ref metadataVersion);
        if (session is not null)
            session.MediaPropertiesChanged += MediaPropertiesChanged;
    }

    private void MediaPropertiesChanged(MediaSession sender, MediaPropertiesChangedEventArgs args) =>
        Interlocked.Increment(ref metadataVersion);

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
            return null;

        try
        {
            using var artwork = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
            if (artwork.Size is 0 or > MaxArtworkBytes)
                return null;

            using var reader = new DataReader(artwork);
            uint size = (uint)artwork.Size;
            if (await reader.LoadAsync(size).AsTask().ConfigureAwait(false) != size)
                return null;

            byte[] bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception exception) when (IsMediaFailure(exception) || exception is IOException)
        {
            return null;
        }
    }

    private static bool IsMediaFailure(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or ObjectDisposedException;

    private static HttpClient CreateLyricsClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxLyricsFileBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sada/1.0 (personal Windows music visualizer)");
        return client;
    }

    private static string? ReadString(JsonElement json, string property) =>
        json.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

internal sealed class LyricsRetryException(TimeSpan retryAfter) : HttpRequestException("Lyrics service is temporarily busy.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public readonly record struct LyricLine(TimeSpan At, string Text);
public readonly record struct LyricWord(string Text, int Start, int Length, TimeSpan At)
{
    public TimeSpan? End { get; init; }
}

public sealed partial class LyricsTrack
{
    private const int MaximumLyricsFileCharacters = 256 * 1024;
    private static readonly Regex Timestamp = new(@"\G\[([0-9]{1,4}):([0-5]?[0-9])(?:[.:]([0-9]{1,3}))?\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex Offset = new(@"^\[offset:\s*([+-]?\d+)\s*\]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Metadata = new(@"^\[[A-Za-z]+:.*\]$", RegexOptions.CultureInvariant);
    private static readonly Regex WordTimestamp = new(@"<([0-9]{1,4}):([0-5]?[0-9])(?:[.:]([0-9]{1,3}))?>",
        RegexOptions.CultureInvariant);
    private static readonly Regex WordToken = new(@"\S+", RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<LyricWord>[] wordTimings;
    private readonly bool[] authoredTimings;

    private sealed record ParsedLine(LyricLine Line, IReadOnlyList<LyricWord> Words, TimeSpan? End = null);

    private LyricsTrack(IEnumerable<ParsedLine> content, string? plainText, TimeSpan? duration)
    {
        ParsedLine[] ordered = content.Select(UseEarlierWordStart).OrderBy(item => item.Line.At).ToArray();
        Lines = Array.AsReadOnly(ordered.Select(item => item.Line).ToArray());
        PlainText = plainText ?? string.Join(Environment.NewLine, Lines.Select(line => line.Text));
        wordTimings = new IReadOnlyList<LyricWord>[ordered.Length];
        authoredTimings = new bool[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            TimeSpan? next = index + 1 < ordered.Length ? ordered[index + 1].Line.At : null;
            var authored = ValidateWordTimes(item);
            authoredTimings[index] = authored;
            wordTimings[index] = authored
                ? CompleteWordEnds(item.Words, item.End, next, duration)
                : EstimateWords(item.Line, item.End, EarliestEnd(next, duration));
        }
        HasAuthoredWordTiming = Array.Exists(authoredTimings, result => result);
    }

    public IReadOnlyList<LyricLine> Lines { get; }
    public string PlainText { get; }
    public bool HasAuthoredWordTiming { get; }

    public IReadOnlyList<LyricWord> WordsForLine(int index) =>
        (uint)index < (uint)wordTimings.Length ? wordTimings[index] : Array.Empty<LyricWord>();

    public bool HasWordTiming(int index) => (uint)index < (uint)authoredTimings.Length && authoredTimings[index];

    public int IndexAt(TimeSpan position)
    {
        int lower = 0;
        int upper = Lines.Count;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (Lines[middle].At <= position)
                lower = middle + 1;
            else
                upper = middle;
        }

        return lower - 1;
    }

    public static LyricsTrack Parse(string text, string? plainText = null, TimeSpan? duration = null)
    {
        var timed = new List<ParsedLine>();
        var untimed = new List<string>();
        int offset = 0;
        using var reader = new StringReader(text.TrimStart('\uFEFF'));

        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            Match adjustment = Offset.Match(line);
            if (adjustment.Success && int.TryParse(adjustment.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsedOffset))
            {
                offset = parsedOffset;
                continue;
            }
            if (Metadata.IsMatch(line))
                continue;

            MatchCollection timestamps = Timestamp.Matches(line);
            if (timestamps.Count == 0)
            {
                untimed.Add(line);
                continue;
            }

            Match finalTimestamp = timestamps[^1];
            var first = ParseTime(timestamps[0]);
            var (words, timings, end) = ParseEnhancedText(line[(finalTimestamp.Index + finalTimestamp.Length)..], first);
            foreach (Match timestamp in timestamps)
            {
                var at = ParseTime(timestamp);
                var delta = at - first;
                var shifted = timings.Count == 0 || at == first
                    ? timings
                    : Array.AsReadOnly(timings.Select(word => word with { At = word.At + delta, End = word.End + delta }).ToArray());
                timed.Add(new ParsedLine(new LyricLine(at, words), shifted, end + delta));
            }
        }

        var corrected = timed.Select(item => item with
        {
            Line = item.Line with { At = ApplyOffset(item.Line.At, offset) },
            Words = CorrectWords(item.Words, offset),
            End = item.End is { } end ? ApplyOffset(end, offset) : null
        });
        return new LyricsTrack(corrected, plainText ?? (timed.Count == 0 ? string.Join(Environment.NewLine, untimed).Trim() : null), duration);
    }

    public static LyricsTrack? ParseLyricsFile(string text, string? plainText = null, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumLyricsFileCharacters) return null;
        var document = new YamlStream();
        try
        {
            using var reader = new StringReader(text);
            document.Load(new BoundedYamlParser(reader));
        }
        catch (YamlException) { return null; }
        if (document.Documents.Count != 1 || document.Documents[0].RootNode is not YamlMappingNode root) return null;
        if (ReadScalar(root, "version") is { } version && version != "1.0") return null;

        var metadata = ReadNode(root, "metadata") as YamlMappingNode;
        if (metadata is not null && bool.TryParse(ReadScalar(metadata, "instrumental"), out var instrumental) && instrumental)
            return null;
        if (duration is null && metadata is not null && ReadTime(metadata, "duration_ms", out var declared) && declared > TimeSpan.Zero)
            duration = declared;
        // Lyricsfile 1.0 leaves offset_ms undefined. Its recorded starts are absolute.
        var lines = new List<ParsedLine>();
        if (ReadNode(root, "lines") is YamlSequenceNode sequence)
        {
            foreach (var node in sequence.Children)
            {
                if (node is not YamlMappingNode entry || !ReadTime(entry, "start_ms", out var at)) continue;
                var words = ReadFileWords(ReadNode(entry, "words") as YamlSequenceNode);
                var lineText = ReadScalar(entry, "text") ?? "";
                if (words.Count > 0 && ValidateWordRanges(words, lineText, out var authoredText, out var ranges))
                {
                    lineText = authoredText;
                    words = ranges;
                }
                else words = Array.Empty<LyricWord>();
                var end = ReadTime(entry, "end_ms", out var last) && last > at ? last : (TimeSpan?)null;
                lines.Add(new ParsedLine(new LyricLine(at, lineText), words, end));
            }
        }
        var plain = plainText ?? ReadScalar(root, "plain");
        return lines.Count == 0 && string.IsNullOrWhiteSpace(plain) ? null : new LyricsTrack(lines, plain, duration);
    }

    private static ParsedLine UseEarlierWordStart(ParsedLine item)
    {
        if (item.Words.Count == 0) return item;
        var start = item.Words[0].At;
        if (start >= item.Line.At || item.Line.At - start > TimeSpan.FromSeconds(2)) return item;
        var aligned = item with { Line = item.Line with { At = start } };
        return ValidateWordTimes(aligned) ? aligned : item;
    }

    private static bool ValidateWordTimes(ParsedLine item)
    {
        if (item.Words.Count == 0) return false;
        var previous = item.Line.At;
        foreach (var word in item.Words)
        {
            if (word.At < previous || item.End is { } maximum && word.At >= maximum) return false;
            previous = word.At;
        }
        return true;
    }

    private static (string Text, IReadOnlyList<LyricWord> Words, TimeSpan? End) ParseEnhancedText(string text, TimeSpan lineTime)
    {
        MatchCollection markers = WordTimestamp.Matches(text);
        if (markers.Count == 0) return (text.Trim(), Array.Empty<LyricWord>(), null);
        var chunks = new List<(string Text, TimeSpan At, TimeSpan? End)>
        {
            (text[..markers[0].Index], lineTime, ParseTime(markers[0]))
        };
        for (int i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            int start = marker.Index + marker.Length;
            int end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            chunks.Add((text[start..end], ParseTime(marker), i + 1 < markers.Count ? ParseTime(markers[i + 1]) : null));
        }
        var display = string.Concat(chunks.Select(chunk => chunk.Text));
        var last = chunks[^1];
        TimeSpan? lineEnd = string.IsNullOrWhiteSpace(last.Text) && last.At > lineTime ? last.At : null;
        if (!chunks.Skip(1).Any(chunk => !string.IsNullOrWhiteSpace(chunk.Text)))
            return (display.Trim(), Array.Empty<LyricWord>(), lineEnd);
        var trimmed = TrimAuthoredWords(chunks);
        return (trimmed.Text, trimmed.Words, lineEnd);
    }

    private static IReadOnlyList<LyricWord> ReadFileWords(YamlSequenceNode? sequence)
    {
        if (sequence is null) return Array.Empty<LyricWord>();
        var words = new List<LyricWord>();
        foreach (var node in sequence.Children)
        {
            if (node is not YamlMappingNode entry || ReadScalar(entry, "text") is not { } text ||
                !ReadTime(entry, "start_ms", out var at)) return Array.Empty<LyricWord>();
            words.Add(new LyricWord(text, 0, text.Length, at)
            {
                End = ReadTime(entry, "end_ms", out var end) && end > at ? end : null
            });
        }
        return words;
    }

    private static bool ValidateWordRanges(IReadOnlyList<LyricWord> input, string line,
        out string text, out IReadOnlyList<LyricWord> words)
    {
        (text, words) = TrimAuthoredWords(input.Select(word => (word.Text, word.At, word.End)));
        if (words.Count == 0) return false;
        var display = line.Trim();
        if (display.Length == 0 || display == text) return true;

        // Some providers omit spaces from individual word entries. Keep the
        // original line text and map the timed ranges back into that text.
        var positions = new int[text.Length];
        int target = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index])) continue;
            while (target < display.Length && char.IsWhiteSpace(display[target])) target++;
            if (target == display.Length || display[target] != text[index]) return false;
            positions[index] = target++;
        }
        if (target != display.Length) return false;

        var mapped = new LyricWord[words.Count];
        for (int index = 0; index < words.Count; index++)
        {
            var word = words[index];
            int start = positions[word.Start];
            int end = positions[word.Start + word.Length - 1] + 1;
            mapped[index] = word with { Text = display[start..end], Start = start, Length = end - start };
        }
        text = display;
        words = Array.AsReadOnly(mapped);
        return true;
    }

    private static (string Text, IReadOnlyList<LyricWord> Words) TrimAuthoredWords(IEnumerable<(string Text, TimeSpan At, TimeSpan? End)> chunks)
    {
        var builder = new StringBuilder();
        var pieces = new List<LyricWord>();
        foreach (var (text, at, end) in chunks)
        {
            pieces.Add(new LyricWord(text, builder.Length, text.Length, at) { End = end });
            builder.Append(text);
        }
        var raw = builder.ToString();
        var trimmed = raw.Trim();
        int left = raw.Length - raw.TrimStart().Length;
        int right = left + trimmed.Length;
        var result = new List<LyricWord>();
        foreach (var piece in pieces)
        {
            int start = Math.Max(piece.Start, left);
            int end = Math.Min(piece.Start + piece.Length, right);
            while (start < end && char.IsWhiteSpace(raw[start])) start++;
            while (start < end && char.IsWhiteSpace(raw[end - 1])) end--;
            if (start < end) result.Add(new LyricWord(raw[start..end], start - left, end - start, piece.At) { End = piece.End });
        }
        return (trimmed, result.AsReadOnly());
    }

    private static IReadOnlyList<LyricWord> CompleteWordEnds(IReadOnlyList<LyricWord> source, TimeSpan? lineEnd,
        TimeSpan? nextLine, TimeSpan? duration)
    {
        var words = new LyricWord[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            var word = source[index];
            var limit = EarliestEnd(EndAtOrAfter(lineEnd, word.At), EndAtOrAfter(nextLine, word.At));
            limit = EarliestEnd(limit, EndAtOrAfter(duration, word.At));
            if (index + 1 < source.Count) limit = EarliestEnd(limit, source[index + 1].At);
            var guessed = word.At + TimeSpan.FromMilliseconds(Math.Clamp(180 + word.Length * 55, 220, 950));
            var end = EndAtOrAfter(word.End, word.At);
            if (end is null)
                end = limit is { } nearby && nearby - word.At <= TimeSpan.FromSeconds(1.6) ? nearby : guessed;
            words[index] = word with { End = EarliestEnd(end, limit) };
        }
        return Array.AsReadOnly(words);
    }

    private static TimeSpan? EndAtOrAfter(TimeSpan? end, TimeSpan start) =>
        end is { } value && value >= start ? value : null;

    private static TimeSpan? EarliestEnd(TimeSpan? first, TimeSpan? second) =>
        first is { } left && (second is null || left < second.Value) ? first : second;

    private static IReadOnlyList<LyricWord> EstimateWords(LyricLine line, TimeSpan? explicitEnd, TimeSpan? next)
    {
        var groups = new List<(int Start, int End)>();
        foreach (Match token in WordToken.Matches(line.Text))
        {
            int end = token.Index + token.Length;
            bool punctuation = !token.Value.EnumerateRunes().Any(Rune.IsLetterOrDigit);
            if (punctuation && groups.Count > 0)
                groups[^1] = (groups[^1].Start, end);
            else if (!punctuation)
                groups.Add((groups.Count == 0 ? 0 : token.Index, end));
        }
        if (groups.Count == 0) return Array.Empty<LyricWord>();

        var weights = groups.Select(group => Math.Clamp(Math.Sqrt(StringInfo.ParseCombiningCharacters(
            line.Text[group.Start..group.End]).Length), 1, 4)).ToArray();
        var natural = Math.Max(850, groups.Count * 405.0);
        var endAt = EarliestEnd(EndAtOrAfter(explicitEnd, line.At), EndAtOrAfter(next, line.At));
        double duration = endAt is { } nextTime ? (nextTime - line.At).TotalMilliseconds : natural;
        if (EndAtOrAfter(explicitEnd, line.At) is null)
        {
            // With line-only lyrics the gap to the next line includes rests. Let
            // slower phrases breathe without spreading a short phrase across a rest.
            double slowPhrase = Math.Max(1200, groups.Count * 650.0);
            double extra = Math.Max(0, duration - slowPhrase);
            duration = Math.Min(duration, slowPhrase + slowPhrase * extra / (slowPhrase + extra));
        }
        double weight = 0;
        var total = weights.Sum();
        var words = new LyricWord[groups.Count];
        for (int index = 0; index < groups.Count; index++)
        {
            var (start, end) = groups[index];
            var at = line.At + TimeSpan.FromMilliseconds(duration * weight / total);
            weight += weights[index];
            words[index] = new LyricWord(line.Text[start..end], start, end - start, at)
            {
                End = line.At + TimeSpan.FromMilliseconds(duration * weight / total)
            };
        }
        return Array.AsReadOnly(words);
    }

    private static TimeSpan ApplyOffset(TimeSpan time, int offset) =>
        TimeSpan.FromMilliseconds(Math.Max(0, time.TotalMilliseconds - offset));

    private static IReadOnlyList<LyricWord> CorrectWords(IReadOnlyList<LyricWord> words, int offset) =>
        offset == 0 || words.Count == 0
            ? words
            : Array.AsReadOnly(words.Select(word => word with
            {
                At = ApplyOffset(word.At, offset),
                End = word.End is { } end ? ApplyOffset(end, offset) : null
            }).ToArray());

    private static YamlNode? ReadNode(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string? ReadScalar(YamlMappingNode map, string key) => (ReadNode(map, key) as YamlScalarNode)?.Value;

    private static bool ReadTime(YamlMappingNode map, string key, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (!int.TryParse(ReadScalar(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ||
            milliseconds is < 0 or > 86_400_000) return false;
        time = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private sealed class BoundedYamlParser(TextReader reader) : IParser
    {
        private readonly Parser parser = new(reader);
        private int depth;
        private int events;
        public ParsingEvent? Current => parser.Current;

        public bool MoveNext()
        {
            if (!parser.MoveNext() || parser.Current is not { } item) return false;
            depth += item.NestingIncrease;
            if (depth > 16 || ++events > 40_000 || item is AnchorAlias)
                throw new YamlException("The Lyricsfile document exceeds supported structure limits.");
            return true;
        }
    }

    private static TimeSpan ParseTime(Match timestamp)
    {
        int minutes = int.Parse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(timestamp.Groups[2].Value, CultureInfo.InvariantCulture);
        string fraction = timestamp.Groups[3].Value;
        int milliseconds = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return TimeSpan.FromSeconds(minutes * 60 + seconds) + TimeSpan.FromMilliseconds(milliseconds);
    }
}
