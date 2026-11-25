# Changelog

[Home](README.md) · [Roadmap](docs/roadmap.md) · [GitHub releases](https://github.com/Abood03/Sada/releases)

Sada first appeared on GitHub as **1.2.0**. This history also covers the earlier Windows builds and the changes that shipped together in **1.4.1**.

## 1.4.1 — Album-colored neon and unlit glass

- **Neon sign** can use the cover's dominant color for the title and a complementary color from the cover for the artist. The previous title retains its own palette during the transition.
- Pausing switches off the light and leaves the glass tubing visible. The title relights progressively on resume. The fade and hide options take effect after a six-second idle period.

## 1.4.0 — Neon Sign and smoother transitions

- Added **Neon sign** on the desktop and in full screen, with independent full-screen placement, adjustable lettering and color, beat response, and idle behavior.
- **Flow** lyrics transition between lines and highlight words progressively by character. Highlights hold on pause and follow the new position after a seek.
- Visualizer modes, palette and direction changes, and atmosphere changes blend between frames. Motion follows applicable Windows animation settings.

See [Neon Sign and motion](docs/neon-and-motion.md).

## 1.3.0 — YouTube lyrics

- Added **Automatic**, **Spotify**, and **YouTube** playback sources. Automatic mode prioritizes active Spotify playback.
- Sada can read a browser's Windows media session and look up time-synced lyrics. You can enter the title and artist manually if a video needs a better match.
- The optional Chrome and Edge extension sends video identity and playback time over a local bridge. That lets Lyrics Studio keep corrections associated with a particular YouTube video.

See [YouTube lyrics setup](docs/youtube.md).

## 1.2.0 — Lyrics Studio and timing improvements

- Added a timeline and a three-line playback preview with progressive highlighting for Arabic and English. Labels identify source, estimated, and edited timing.
- Added tap-to-mark line timing, editable word start and end times, and whole-recording adjustments in 0.10-second steps. Players that support seeking allow you to replay a section.
- Timing edits are stored locally per recording. **Restore source timing** removes the local correction. An open draft stays attached to its original recording when the player changes tracks.
- Estimated highlighting handles slow songs and long instrumental gaps more consistently when only line timestamps are available. Explicit word times remain intact.
- Recording identity includes available metadata and duration. Playback commands from Lyrics Studio check the recording before controlling the player.

Arabic and English rendering, persistence, and restoration were tested. Playback seeking used a simulated player. See [Testing](docs/testing.md) for the verification scope.

## 1.1.0 — Desktop Scene Studio

- Added a scene-first dashboard and an editor that opens from the dashboard or the system tray. Drag, resize, align, lock, and preview desktop widgets before saving.
- Added **Album**, **Karaoke**, and **Focus** presets, plus named scenes and switching from the tray.
- Saved layouts use display-relative positions and can fall back to the primary display when the original monitor becomes unavailable. Existing 1.0.0 settings can be opened.

## 1.0.0 — First Windows build

- Transparent desktop widgets, a visualizer, a dashboard, a tray icon, and full-screen playback.
- Windows audio capture and media-session detection, including Spotify. The circular visualizer can display the cover and pick up its palette.
- Automatic lyrics lookup from LRCLIB, local lyric files, estimated word timing where only line timing is available, and automatic or manual timing adjustments.
- Fourteen visualizer modes and 23 widget types, including clock, calendar, weather, and system information.
