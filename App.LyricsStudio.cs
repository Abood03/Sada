using System.Windows;

namespace Sada;

public partial class App
{
    LyricRecordingStore? recordingStore;
    LyricRecordingStore RecordingStore => recordingStore ??= new(Path.Combine(Preferences.Folder, "recording-timings"));
    LyricsStudio? lyricsStudio;
    LyricRecordingKey? checkedRecording;
    LyricsTrack? savedRecordingLyrics;

    bool IsEditedTiming => savedRecordingLyrics is not null && ReferenceEquals(Lyrics, savedRecordingLyrics)
        && LyricRecordingKey.From(Media.Current) == checkedRecording;

    bool TryApplyRecordingTiming(MediaSnapshot track)
    {
        var recording = LyricRecordingKey.From(track);
        if (recording != checkedRecording)
        {
            checkedRecording = recording;
            savedRecordingLyrics = recording is { } key ? RecordingStore.Load(key) : null;
        }
        if (savedRecordingLyrics is null) return false;
        CancelLyricsLookup();
        if (!ReferenceEquals(Lyrics, savedRecordingLyrics))
            SetLyrics(track.LyricsKey, savedRecordingLyrics, "saved in Lyrics Studio");
        return true;
    }

    internal void OpenLyricsStudio()
    {
        if (lyricsStudio is { } open)
        {
            open.Activate();
            return;
        }
        var song = Media.Current;
        if (LyricRecordingKey.From(song) is not { } recording)
        {
            SetStatus("Play a recording with a known duration to open Lyrics Studio.");
            return;
        }
        if (Lyrics is not { } source || song.LyricsKey != lyricIdentity)
        {
            SetStatus("Load lyrics for this recording before editing its timing.");
            return;
        }

        bool edited = IsEditedTiming;
        var offset = edited ? TimeSpan.Zero : TimeSpan.FromSeconds(LyricsOffsetFor(song.LyricsKey).Seconds);
        var draft = new LyricTimingDraft(source, song.Duration, offset);
        var window = new LyricsStudio(song, draft, edited,
            () =>
            {
                var current = Media.Current;
                return LyricRecordingKey.From(current) == recording ? current : null;
            },
            playing => Media.SetRecordingPlayingAsync(recording, playing),
            position => Media.SeekRecordingAsync(recording, position),
            editedDraft => SaveRecordingTiming(recording, editedDraft),
            () => RestoreRecordingTiming(recording),
            Scene.LyricsTextSize);
        if (dashboard.IsVisible) window.Owner = dashboard;
        lyricsStudio = window;
        window.Closed += (_, _) => lyricsStudio = null;
        window.Show();
        window.Activate();
    }

    bool SaveRecordingTiming(LyricRecordingKey recording, LyricTimingDraft draft)
    {
        try
        {
            RecordingStore.Save(recording, draft);
            RefreshEditedRecording(recording);
            SetStatus("Saved timing for the selected recording.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("Could not save lyrics timing: " + ex.Message);
            return false;
        }
    }

    bool RestoreRecordingTiming(LyricRecordingKey recording)
    {
        try
        {
            if (!RecordingStore.Remove(recording)) return false;
            RefreshEditedRecording(recording);
            SetStatus("Source timing restored.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not restore lyrics timing: " + ex.Message);
            return false;
        }
    }

    void RefreshEditedRecording(LyricRecordingKey recording)
    {
        var song = Media.Current;
        if (LyricRecordingKey.From(song) != recording) return;
        checkedRecording = null;
        savedRecordingLyrics = null;
        suppressedLyricsTrack = "";
        retryLyricsAfter = default;
        CancelLyricsLookup();
        Lyrics = null;
        lyricIdentity = "";
        SyncLyrics(song);
        dashboard.UpdateScene();
    }
}
