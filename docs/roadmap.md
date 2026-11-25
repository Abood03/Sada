# Roadmap

[Home](../README.md) · [Features](features.md) · [System map](architecture.md) · [Changelog](../CHANGELOG.md)

## Release history

| Version | What shipped |
| --- | --- |
| **1.0.0** | Desktop audio visualizer, album artwork, synchronized lyrics, transparent widgets, and full-screen visualization. |
| **1.1.0** | Live desktop scene editing, Album, Karaoke, and Focus templates, named profiles, and saved display-relative layout. |
| **1.2.0** | Lyrics Studio with per-recording line and word edits, 0.10-second timing steps, and more natural pacing for slow songs with long gaps. |
| **1.3.0** | YouTube playback detection, manual song matching, and an optional browser bridge. |
| **1.4.0** | Neon Sign for desktop and full screen, smoother lyric transitions, and crossfades between visualizer effects. |
| [**1.4.1**](https://github.com/Abood03/Sada/releases/tag/v1.4.1) | Neon colors derived from the cover, dim glass visible after playback stops, and gradual relighting on resume. |

The GitHub release history starts at **1.2.0**. See the [changelog](../CHANGELOG.md) for the complete history of Windows versions.

## Validation priorities

<details>
<summary>Timing on slow songs and alternate recordings</summary>

Compare the source and edited timing against real playback with long vocal pauses, live versions, and videos with an intro. Cover Arabic and English, pause and seek, and check that the saved edit reopens for the same recording. [Current test coverage](testing.md)

</details>

<details>
<summary>Desktop and full-screen behavior across displays</summary>

Check transparent widgets, independent Neon placement in full screen, Windows reduced motion, and scene restoration after disconnecting a monitor. Repeat at common display scaling settings.

</details>

## What would make it V2

The V2 milestone is **two independent scenes running at the same time**. One display could show lyrics and album art while another runs a focus scene. Each scene would have its own layout, audio source, and activation rules.

<details>
<summary>What would count as complete</summary>

- Change the layout or audio source on either display while the other keeps running.
- Save both scenes and restore them after a restart.
- Apply the requested screen fallback when a display is unavailable.
- Preserve existing single-scene profiles during migration.

This is a design direction with no announced release date.

</details>
