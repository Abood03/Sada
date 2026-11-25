# YouTube lyrics

[Sada](../README.md) / [Feature explorer](features.md)

Sada can follow a YouTube video through the browser's Windows media session. The optional Chrome or Edge extension supplies the video ID and playback clock directly.

## Follow YouTube through Windows

1. Play a music video in your browser.
2. In Sada, open **Media & lyrics**, then set **Playback source** to **YouTube**. Sada uses the available title, artist, and duration to find lyrics on LRCLIB.
3. To correct a match, choose **Match YouTube song…**, enter the song title and artist, and select **Find lyrics**. The video needs a reported duration. If you've already saved custom timing for it, restore source timing in Lyrics Studio before changing its match.
4. To supply your own timed lyrics, choose **Open lyrics file…** and select an LRC file.

**Automatic** gives priority to Spotify while it's playing. Timeline accuracy through Windows depends on the playback data the browser reports.

## Add browser timing

1. Extract the portable ZIP, including the `browser` folder, and run `Sada.exe` from the location where you keep it.
2. In **Media & lyrics**, choose **Register browser bridge**. Sada registers its current executable path as a native messaging host for your Windows user.
3. Open `chrome://extensions` or `edge://extensions`, turn on **Developer mode**, and choose **Load unpacked**. Select the extracted `browser` folder containing `manifest.json`.
4. Reload your YouTube video on a standard `www.youtube.com/watch` page. Keep Sada running and select **YouTube** as the playback source.
5. Play, pause, or seek in the browser. Sada follows the position reported by the video.

If you move `Sada.exe` later, run **Register browser bridge** again from its new location. [Chrome extension setup](https://developer.chrome.com/docs/extensions/get-started/tutorial/hello-world#load-an-unpacked-extension) · [Edge extension setup](https://learn.microsoft.com/en-us/microsoft-edge/extensions/getting-started/extension-sideloading)

## Correct timing for a video

Open **Edit this recording** in **Media & lyrics** when timed lyrics are available. Use a recording offset for a fixed delay, such as a spoken intro. Adjust individual lines and words when the mismatch varies across the video. Alternate and live recordings may have a different arrangement and need a better match or a local lyrics file.

With the extension, Sada uses the video ID to keep corrections for different uploads separate. [Lyrics Studio controls](features.md#lyrics-studio)

<details>
<summary>What the extension sends</summary>

The browser sends the video ID, metadata, position, duration, playback rate, and playback state to Sada through local native messaging. Lyrics lookups send the song title and artist to LRCLIB and may include the album and duration. The extension focuses on standard YouTube watch pages and suspends its song report during ads it detects.

[Browser extension reference](../browser/README.md) · [Chrome native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)

</details>
