using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using static Sada.StudioUi;

namespace Sada;

public partial class App
{
    YouTubeBridge? youtubeBridge;
    YouTubePlayback? lastBrowserPlayback;
    bool lastBridgeConnected;
    sealed record YouTubeLookup(string Title, string Artist, string Album, bool ManuallyMatched);

    void SetUpYouTubePlayback()
    {
        Media.SourcePreference = Prefs.PlaybackSource;
        Media.IsBrowserSource = YouTubeTrack.IsBrowserSource;
        youtubeBridge = new YouTubeBridge();
        youtubeBridge.Start();
    }

    async Task RunYouTubeHostAsync()
    {
        try { await YouTubeBridge.RunHostAsync(); }
        catch (IOException) { }
        finally { Shutdown(); }
    }

    void RefreshYouTubePlayback()
    {
        if (youtubeBridge is null) return;
        var video = youtubeBridge.Current;
        bool connected = youtubeBridge.IsConnected;
        if (ReferenceEquals(lastBrowserPlayback, video) && lastBridgeConnected == connected) return;
        lastBrowserPlayback = video;
        lastBridgeConnected = connected;
        MediaSnapshot? snapshot = video is null ? null : new MediaSnapshot(video.Title, video.Artist, video.Album, null,
            TimeSpan.FromSeconds(video.Position), TimeSpan.FromSeconds(video.Duration), video.Playing, "YouTube")
        {
            YouTubeVideoId = video.VideoId,
            LastUpdated = video.ObservedAt,
            PlaybackRate = video.Rate
        };
        Media.SetYouTubePlayback(snapshot, connected);
    }

    internal void ChangePlaybackSource(MediaSourcePreference value)
    {
        Prefs.PlaybackSource = value;
        Media.SourcePreference = value;
        QueueSave();
        _ = RefreshMediaAsync();
    }

    bool IsYouTubePlayback(MediaSnapshot track) => track.YouTubeVideoId is not null ||
        YouTubeTrack.IsBrowserSource(track.Source) && (Media.SourcePreference == MediaSourcePreference.YouTube ||
            YouTubeTrack.HasYouTubeMarker(track.Title, track.Artist, track.Album));

    YouTubeLookup? ResolveYouTubeSong(MediaSnapshot track)
    {
        if (Prefs.YouTubeMatches.TryGetValue(LyricsRequestKey(track), out var chosen))
            return new(chosen.Title, chosen.Artist, "", true);
        var match = YouTubeTrack.Match(track.Title, track.Artist, track.Album);
        return match is null ? null : new(match.Title, match.Artist, match.Album, false);
    }

    internal void OpenYouTubeMatch()
    {
        var video = Media.Current;
        if (!video.HasTrack || !IsYouTubePlayback(video) || video.Duration <= TimeSpan.Zero)
        {
            SetStatus("Choose YouTube as Playback source and play a video with a reported duration.");
            return;
        }
#if SADA_LYRICS_STUDIO
        if (IsEditedTiming)
        {
            SetStatus("Restore source timing in Lyrics Studio before changing this video's match.");
            return;
        }
#endif
        var suggested = ResolveYouTubeSong(video);
        var title = new TextBox { Text = suggested?.Title ?? "", MaxLength = 200, Margin = new Thickness(0, 4, 0, 12) };
        var artist = new TextBox { Text = suggested?.Artist ?? "", MaxLength = 200, Margin = new Thickness(0, 4, 0, 12) };
        var dialog = new Window
        {
            Owner = dashboard, Title = "Match YouTube song", Width = 490,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Night
        };
        var apply = new Button { Content = "Find lyrics", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        void CheckFields(object? sender, TextChangedEventArgs args) =>
            apply.IsEnabled = !string.IsNullOrWhiteSpace(title.Text) && !string.IsNullOrWhiteSpace(artist.Text);
        title.TextChanged += CheckFields;
        artist.TextChanged += CheckFields;
        apply.IsEnabled = title.Text.Length > 0 && artist.Text.Length > 0;
        apply.Click += (_, _) => dialog.DialogResult = true;
        var body = Stack(Text(video.Title, 15, Paper, true), Text("Song title", 12, Soft), title,
            Text("Artist", 12, Soft), artist, Wrap(apply, cancel));
        body.Margin = new Thickness(22);
        dialog.Content = body;
        dialog.Loaded += (_, _) => title.Focus();
        if (dialog.ShowDialog() != true) return;

        var key = LyricsRequestKey(video);
        if (Prefs.YouTubeMatches.Count >= 250 && !Prefs.YouTubeMatches.ContainsKey(key))
            Prefs.YouTubeMatches.Remove(Prefs.YouTubeMatches.Keys.First());
        Prefs.YouTubeMatches[key] = new(title.Text.Trim(), artist.Text.Trim());
        lyricsCache.Remove(key);
        QueueSave();
        if (LyricsRequestKey(Media.Current) != key)
        {
            SetStatus("Song match saved for the original video.");
            return;
        }
        CancelLyricsLookup();
        Lyrics = null;
        _ = FetchLyricsAsync();
    }

    internal void RegisterYouTubeBridge()
    {
        try
        {
            if (Environment.ProcessPath is not { } path || !string.Equals(Path.GetFileName(path), "Sada.exe", StringComparison.OrdinalIgnoreCase))
            { SetStatus("Run Sada.exe before registering the YouTube browser add-on."); return; }
            Directory.CreateDirectory(Preferences.Folder);
            var manifest = Path.Combine(Preferences.Folder, YouTubeBridge.HostName + ".json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                name = YouTubeBridge.HostName, description = "Sada YouTube playback", path, type = "stdio",
                allowed_origins = new[] { "chrome-extension://" + YouTubeBridge.ExtensionId + "/" }
            }));
            foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge" })
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\{browser}\NativeMessagingHosts\{YouTubeBridge.HostName}");
                key.SetValue("", manifest, RegistryValueKind.String);
            }
            SetStatus("Browser bridge registered. Load the Sada add-on in Chrome or Edge, then reload YouTube.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { SetStatus("Could not register the YouTube browser bridge: " + error.Message); }
    }
}
