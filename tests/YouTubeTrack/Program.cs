using Sada;

Assert(YouTubeTrack.IsBrowserSource("Chrome"), "Chrome shell ID is recognized");
Assert(YouTubeTrack.IsBrowserSource("MSEdge._crx_123"), "Edge web app ID is recognized");
Assert(YouTubeTrack.IsBrowserSource(@"C:\Program Files\Google\Chrome\Application\chrome.exe"), "Executable shell ID is recognized");
Assert(YouTubeTrack.IsBrowserSource("Firefox.E7CF176E110C211B"), "Profile-specific Firefox ID is recognized");
Assert(!YouTubeTrack.IsBrowserSource("Spotify.exe"), "Spotify is not a browser");
Assert(!YouTubeTrack.IsBrowserSource("ChromecastPlayer"), "Unrelated app with browser prefix is not a browser");

Assert(YouTubeTrack.HasYouTubeMarker("Artist - My Song | YouTube", "", ""), "Explicit title site suffix is recognized");
Assert(YouTubeTrack.HasYouTubeMarker("My Song", "Artist", "YouTube Music"), "Structured service field is recognized");
Assert(!YouTubeTrack.HasYouTubeMarker("Artist - My Song (Official Music Video)", "", ""), "Music-video label alone does not identify the site");
Assert(!YouTubeTrack.HasYouTubeMarker("Introduction to YouTube", "Channel", ""), "Mention of service in title is not a service marker");

Assert(YouTubeTrack.Match("Artist - My Song (Official Music Video)", "ArtistVEVO", "YouTube") ==
    new YouTubeSong("My Song", "Artist", ""), "Visible artist/title pair and official suffix are handled");
Assert(YouTubeTrack.Match("My Song", "Artist - Topic", "Album") ==
    new YouTubeSong("My Song", "Artist", "Album"), "Structured music metadata preserves album and cleans Topic artist tag");
Assert(YouTubeTrack.Match("فنان – أغنية (الفيديو الرسمي) | YouTube", "YouTube", "") ==
    new YouTubeSong("أغنية", "فنان", ""), "Arabic title parses through an en dash, official suffix and site marker");
Assert(YouTubeTrack.Match("Artist - Live Forever (Official Video)", "", "") ==
    new YouTubeSong("Live Forever", "Artist", ""), "Legitimate title beginning with Live is preserved");
Assert(YouTubeTrack.Match("Artist - Song (Part II) [Lyrics] (Official Video 4K)", "", "") ==
    new YouTubeSong("Song (Part II)", "Artist", ""), "Only recognized trailing publisher tags are stripped");
Assert(YouTubeTrack.Match("My Song (Official Audio)", "YouTube", "") is null, "Generic site metadata does not become an artist");
Assert(YouTubeTrack.Match("https://www.youtube.com/watch?v=abc", "Artist", "") is null, "URLs are not used as track names");

foreach (var variant in new[] { "Song (Live at Wembley)", "Song - 2019 Remaster", "Song (DJ Remix)",
    "Song Cover by Another Artist", "Song [Sped-Up] (Lyric Video)", "أغنية (ريمكس)", "Song - Acoustic" })
{
    Assert(YouTubeTrack.Match($"Artist - {variant}", "", "") is null, $"Alternate recording requires manual review: {variant}");
}

static void Assert(bool success, string message)
{
    if (!success) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}
