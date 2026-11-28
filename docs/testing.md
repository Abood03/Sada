# Testing Sada

[Sada](../README.md) / [Feature explorer](features.md)

## Run the automated checks

Open PowerShell at the repository root on a Windows desktop with the .NET 10 SDK. The rendering tests require WPF. Build restore requires NuGet access or cached packages.

```powershell
.\tests\Run-Checks.ps1
node --test .\browser\tests\extension.test.cjs
```

Node.js is required for the second command. To run one .NET suite, specify its project directly.

```powershell
dotnet run --project .\tests\LyricsStudio\LyricsStudio.csproj -c Release
```

The PowerShell runner prints `PASS` results and stops on the first failure. Rendering tests use isolated WPF windows, and timing tests use simulated playback or synthetic audio cues.

| Suite | Coverage |
| --- | --- |
| `Recording` | Recording identity, word-timing bounds, and saving or restoring corrections in an isolated directory. |
| `WordTiming` | Enhanced LRC, Lyricsfile/YAML, estimated timing, long breaks, and held notes. |
| `SongSync` | Adaptive timing against synthetic audio cues and preserving source timing when the signal is ambiguous. |
| `SceneEditor` | Alignment, resizing, templates, V1 positions, and keeping preview changes out of saved settings. |
| `LyricsStudio` | Editing lines and words, preview and transport controls with simulated playback, and saving the original recording's draft. |
| `GraphemeRendering` | Highlight progress on Arabic, Latin, and combining characters, plus repositioning after a backward seek. |
| `YouTubeTrack` | Arabic and English titles, browser-source detection, and ambiguous alternate recordings. |
| `YouTubeBridge` | Local message validation and tracking playback across independent tabs and connections. |
| `VisualizerMotion` | Blending changes to modes, appearance, and direction, including preserving the outgoing Waterfall drawing. |
| `NeonRendering` | Cover color changes, unlit glass after playback stops, gradual relighting, reactive glow, and Arabic and English text. |
| `GlitchRendering` | Album art and title fallback, rendering during a glitch burst, and reduced motion. |
| `FullNeon` | Independent full-screen visibility and bounded placement for Neon Sign and Glitch Box. |
| Browser extension | Sender validation, timing, pause, seeking, buffering, rate, ad detection, and tab lifecycle with simulated browser state. |

## Check the portable build on Windows

<details open>
<summary>Desktop scenes, Neon Sign, and Glitch Box</summary>

1. Open **Edit desktop scene** from Sada's tray menu. Preview a template and move the cover and lyrics. Select **Cancel** and confirm that your arrangement returns.
2. With a second display available, save a scene there and restart Sada to check the position. Then try the same scene with the display disconnected and check the primary-screen fallback.
3. Enable **Neon sign** on the desktop and in full screen. Play tracks with distinct cover colors, pause, and resume. Look for a palette change, visible unlit glass, and relighting. Move the sign in full screen and verify that the desktop position is independent.
4. Enable **Glitch Box** and switch tracks with and without cover art. Try the distortion presets and pause to check the selected idle behavior. Enable it in full screen and move it without changing the desktop position.

</details>

<details>
<summary>Synchronized lyrics and playback</summary>

1. While a familiar recording plays, open **Media & lyrics → Edit this recording**. Adjust a line or word, select **Save timing**, and check it after restarting Sada. Then try **Restore source timing**.
2. Try a slow recording with audible gaps. Compare the line onset and word highlight with the vocals, and note the recording and timestamp if they diverge.
3. Try a YouTube watch page through the Windows media session and, if installed, the extension. Check changes after a pause, a seek, or a switch to a different upload.

Seek and replay support depends on what the player exposes to Windows.

</details>

## Release verification notes

- **1.4.1:** Windows rendering checks covered the album palette, unlit glass, and relighting. The desktop Neon Sign was visible after restarting the portable build. The rendering checks exercised the transition in isolation.
- **1.3.0:** An Edge session supplied synchronized YouTube lyrics and reported a seek while paused. Manual matching was checked with a bilingual video title. The separate extension path was exercised with automated tab and message tests and a direct test of the portable executable as the native host.
- **1.2.0:** A saved scene was restored on two monitors. Primary-screen fallback was checked with a missing display ID. Slow-track timing and seeking were tested with synthetic samples, so real recordings still benefit from a listening check.
