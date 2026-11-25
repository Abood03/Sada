using System;
using System.Text.RegularExpressions;

namespace Sada;

internal sealed record YouTubeSong(string Title, string Artist, string Album);

internal static class YouTubeTrack
{
    private const string AlternateName = @"live|acoustic|piano|instrumental|karaoke|cover|remaster(?:ed)?|remix(?:ed)?|reaction|nightcore|sped[ -]up|slowed|extended|edit|حفلة|مباشر|ريمكس|كاريوكي";
    private static readonly string[] BrowserNames = ["chrome", "google chrome", "msedge", "microsoft edge", "firefox", "brave", "opera", "vivaldi", "arc"];
    private static readonly Regex SiteSuffix = Pattern(@"\s+(?:[-–—|]\s+YouTube(?: Music)?|\(YouTube(?: Music)?\)|\[YouTube(?: Music)?\])\s*$");
    private static readonly Regex ArtistTitleSeparator = Pattern(@"\s+[-–—]\s+");
    private static readonly Regex OfficialSuffix = Pattern(@"\s*[\[(](?:(?:official\s+)?(?:music\s+video|lyric\s+video|visuali[sz]er|video|audio)|lyrics|official|الفيديو الرسمي|فيديو كليب|كلمات)(?:\s+(?:HD|4K))?[\])]\s*$");
    private static readonly Regex TopicSuffix = Pattern(@"\s+[-–—]\s+Topic\s*$");
    private static readonly Regex AlternateTag = Pattern(@"(?:[\[(]|\s[-–—|:]\s)\s*(?:\d{4}\s+)?(?:" + AlternateName + @")\b");
    private static readonly Regex BareAlternate = Pattern(@"^(?:" + AlternateName + @")(?:\s+version)?$");
    private static readonly Regex AlternateDescription = Pattern(
        @"\b(?:remix|cover|remaster(?:ed)?|karaoke|instrumental|nightcore|sped[ -]up|slowed(?:\s+and\s+reverb)?)\s*(?:[\])]|$)|\b(?:cover\s+by|live\s+(?:at|from|in))\s+\p{L}");

    internal static bool IsBrowserSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var separator = source.LastIndexOfAny(['\\', '/']);
        var application = source.AsSpan(separator + 1).Trim();
        foreach (var browser in BrowserNames)
        {
            if (application.StartsWith(browser, StringComparison.OrdinalIgnoreCase) &&
                (application.Length == browser.Length || application[browser.Length] is '.' or '_' or '!'))
                return true;
        }
        return false;
    }

    internal static bool HasYouTubeMarker(string title, string artist, string album) =>
        IsSiteName(title) || SiteSuffix.IsMatch(title) || IsSiteName(artist) || IsSiteName(album);

    internal static YouTubeSong? Match(string title, string artist, string album)
    {
        var song = SiteSuffix.Replace(title.Trim(), "");
        var split = ArtistTitleSeparator.Match(song);
        if (split.Success)
        {
            // Browser media metadata can report the channel as the artist. A
            // visible artist/title pair in the video title is more specific.
            artist = song[..split.Index];
            song = song[(split.Index + split.Length)..];
        }

        string previous;
        do
        {
            previous = song;
            song = OfficialSuffix.Replace(song, "").Trim();
        }
        while (!string.Equals(song, previous, StringComparison.Ordinal));

        artist = TopicSuffix.Replace(artist.Trim(), "").Trim();
        if (!IsMetadataValue(song) || !IsMetadataValue(artist) ||
            AlternateTag.IsMatch(song) || BareAlternate.IsMatch(song) || AlternateDescription.IsMatch(song))
            return null;

        album = album.Trim();
        return new YouTubeSong(song, artist, IsMetadataValue(album) ? album : "");
    }

    private static bool IsSiteName(string value) =>
        value.Trim().Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("YouTube Music", StringComparison.OrdinalIgnoreCase);

    private static bool IsMetadataValue(string value) =>
        value.Length is > 0 and <= 200 && !IsSiteName(value) &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
