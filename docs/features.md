# Feature explorer

[Documentation home](../README.md) · [Quick start](quick-start.md) · [Architecture map](architecture.md)

Pick a feature and expand its guide. The paths below use the labels in Sada Studio.

- [Desktop scenes](#desktop-scenes) · [Visualizer](#visualizer) · [Playback sources](#playback-sources)
- [Synchronized lyrics](#synchronized-lyrics) · [Lyrics Studio](#lyrics-studio) · [Neon Sign](#neon-sign)
- [Widget catalog](#widget-catalog) · [Full screen](#full-screen) · [Audio and system](#audio-and-system)

## Desktop scenes

A scene saves your widget arrangement and appearance so you can switch between different desktop layouts.

<details>
<summary>Arrange widgets and save a scene</summary>

1. Open **Scenes → Edit on desktop** or choose **Edit desktop scene** from the tray icon.
2. Pick a display and select a widget. Drag it to move it or pull its corner to resize it. Hold <kbd>Alt</kbd> to bypass snap guides.
3. Adjust its opacity, visibility, or lock. **Move cover and lyrics together** links the cover visualizer and lyrics for dragging. Wallpaper-size effects fill the display.
4. Choose **Save** to use the draft, **Save as** to keep a named copy, or **Cancel** to return to the prior layout.

| Template | Layout |
| --- | --- |
| **Album** | A large cover in the Halo visualizer, with lyrics underneath. |
| **Karaoke** | A smaller cover and more room for lyrics. |
| **Focus** | A minimal clock and a subdued bar spectrum. |

Apply a saved scene from **Scenes**, **Profiles**, or the tray menu. In **Profiles**, you can also save the current arrangement with an optional foreground app name or daily local time in 24-hour `HH:mm` format. A foreground profile applies while the matching app is active. Sada restores the previous arrangement when you leave it. Scheduled switching works while Sada is running.

Use **Desktop widgets → Placement** for desktop-layer, normal-window, or always-on-top behavior. Locking ordinary widgets prevents edits outside the scene editor. If a saved display is disconnected, Sada falls back to an available display and keeps widgets within its bounds.

</details>

## Visualizer

The visualizer reacts to audio captured from the chosen Windows playback device or microphone.

<details>
<summary>Choose a pattern, palette, and movement</summary>

Open **Visualizer** to see the live preview.

| Control | Options |
| --- | --- |
| **Pattern** | Bars, Mirror, Blocks, Ring, Halo, Waveform, Aurora, Orb, Vinyl, Waterfall, Scope, Particles, Terrain, and Meters. |
| **Palette** | Aurora, Ember, Orchid, Frost, Citrus, Monochrome, Rainbow, or custom starting and ending colors. |
| **Direction** | Up, Down, Left, Right, or Center. The effect depends on the selected pattern. |
| **Atmosphere** | None, Mist, Dust, or Ripples, with an independent strength control. |
| **Appearance** | Density, line weight, glow, speed, opacity, and optional reflection. |

Enable **Use colors from album art** to derive the palette from the current cover. Under **Desktop widgets → Visualizer**, you can also place the cover inside Ring and Halo, add a track card, or use blurred album art as an ambient layer. Changes to the visualizer pattern, direction, palette, and atmosphere transition smoothly when Windows animations are enabled.

</details>

## Playback sources

Sada follows the selected player's track metadata, album art, playback state, and position. Playback controls depend on what the player exposes to Windows.

<details>
<summary>Switch between Spotify and YouTube</summary>

Open **Media & lyrics → Playback source → Follow**.

| Source | Behavior |
| --- | --- |
| **Automatic** | Prioritizes Spotify when it is playing. It can fall back to a browser or another Windows media session. |
| **Spotify** | Follows the Spotify Windows media session. |
| **YouTube** | Follows a supported browser media session or the optional Chrome or Edge bridge. |

The dashboard transports can play, pause, and change tracks when the selected session supports those commands. The optional YouTube bridge sends the video ID, current time, and playback state directly to Sada. Use the YouTube player itself for playback and seeking when following the bridge.

If a video's title includes a nickname, translation, or promotional text, use **Match YouTube song…** to enter the recording title and artist. [YouTube setup and matching](youtube.md)

</details>

## Synchronized lyrics

Sada can look up lyrics from LRCLIB or load a local file. Timed lyrics follow the playback position and highlight text progressively.

<details>
<summary>Choose a layout and correct a timing offset</summary>

1. Open **Media & lyrics** and enable **Show lyrics widget**. Under **Lyrics source**, turn on **Find lyrics automatically**, choose **Open lyrics file…**, or use **Search again**.
2. Pick a **Lyrics layout** and set **Maximum text size**. Local files can use LRC, Enhanced LRC, plain text, or the Lyricsfile YAML format.
3. Use **Timing for this song** to move the display **Earlier by 0.10 s** or **Later by 0.10 s**. **Clear saved timing** resets that offset. **Adjust automatically when the audio matches** allows a correction when playback signals line up reliably.

| Layout | Display |
| --- | --- |
| **Flow** | Previous, current, and next lines slide as the song progresses. The current line highlights letter by letter and preserves Arabic shaping. |
| **Single** | One current line. |
| **Karaoke** | Current line with its neighboring lines. |
| **Subtitles** | Current and next lines in a compact layout. |
| **Panel** | Up to five timed lines, or a scrollable view for plain lyrics. |
| **Marquee** | A single line that scrolls horizontally when it overflows. |

When a source supplies word timestamps, Sada uses them. With line timestamps alone, Sada estimates the word timing within each line. Plain text can be read as supplied or timed manually in [Lyrics Studio](#lyrics-studio).

</details>

## Lyrics Studio

Use the recording editor when one offset is insufficient and specific lines or words need correction.

<details>
<summary>Edit and save timing for one recording</summary>

1. Load lyrics for a recording with a known playback duration and choose **Media & lyrics → Edit this recording**.
2. Select a line in the editor. Use the preview to compare the position with playback. **Mark line at playhead** assigns the current playback position to the line and advances the selection.
3. Select a word and enter its start and end times, or capture them with **Start at playhead** and **End at playhead**. Use **Earlier 0.10 s** and **Later 0.10 s** to shift the whole recording.
4. For players that expose seeking, use **Seek to preview time** or **Replay selection**. **Follow playback** returns the preview to the current playhead after manual scrubbing.
5. Select **Save timing** to keep the edits locally. **Restore source timing** removes an existing saved edit and loads the available source again.

The preview labels source, estimated, and edited timing. The draft stays attached to the recording you opened. You can keep editing it if the player changes tracks. Live transport controls are disabled while a different recording is selected in the player.

</details>

## Neon Sign

Neon renders a track title and optional artist as animated glass tubing. Desktop and full-screen visibility can be enabled independently.

<details>
<summary>Set cover colors, music response, and pause behavior</summary>

Open **Desktop widgets → Neon sign** and enable either **Show neon on the desktop** or **Show neon in full screen**.

| Control | What it changes |
| --- | --- |
| **Lettering and Colors** | Script, Block, or Tall Capitals lettering. Use Sada, Warm White, Visualizer, Album cover, or custom title and artist colors. |
| **Music response** | Chase, Equalizer, Power surge, Flare, or Steady, with adjustable response strength. |
| **Tube flicker** | Steady, Occasional, or Broken Letter. |
| **After playback stops** | Show unlit tubes, Fade unlit tubes, or Hide sign. Fade and Hide take effect after six seconds. |
| **Display options** | Acrylic backing, artist line, music note, and optional click-to-play on the desktop. |

**Album cover** uses the dominant cover color for the title and an accent for the artist. When playback pauses, the glow flickers out and leaves the glass visible. Playback resuming lights the text progressively. Drag the sign in full screen to save its position independently from the desktop. [Neon Sign and motion guide](neon-and-motion.md)

</details>

## Widget catalog

Open **Desktop widgets → Choose a widget** for a preview and individual settings. Enable a set of widgets quickly under **All widgets**.

| Music and playback | What it displays |
| --- | --- |
| [Visualizer](#visualizer) | The active sound as a live pattern, with optional cover art and track card. |
| [Lyrics](#synchronized-lyrics) | Synchronized or plain lyrics in the chosen layout. |
| **Now playing** | Title, artist, and playback controls in Compact, Centered, Cover, or Text Only layout. |
| **Cassette** and **Disc player** | Alternative track cards with cassette styling or a rotating disc design. |
| [Neon sign](#neon-sign) | An animated title and artist overlay with album-derived colors. |
| **Album colors** | Three swatches sampled from the current cover. |
| **Listening stats** | Today's listening time and track count, plus total time, unique tracks, and top artist from local history. |

<details>
<summary>See visual effects and utility widgets</summary>

| Visual effect | What it displays |
| --- | --- |
| **Environment** | A full-display animated Dusk, Tides, or Skyline scene. |
| **Wallpaper** | A local image or video as a full-display layer. |
| **Album backdrop** | A blurred, tinted view of the current cover art. |
| **Screen glow** | Audio-reactive edge lighting or spectrum effects. The widget has ten glow styles and a strength control. |
| **Spanning spectrum** | The scene's visualizer pattern in a separate widget sized for a wider layout. |
| **Fluid motion**, **Waves**, and **Snow** | Fluid shapes, animated waves, or drifting particles. |
| **Cursor trail** | A fading trace that follows the pointer. |
| **Murmuration** | Animated particles that move as a flock. |

| Utility | What it displays |
| --- | --- |
| **Clock** | Digital, Stacked, Minimal, Analog, or Words face. Customize type, spacing, color, glow, time format, seconds, and date. |
| **Calendar** | The current month with today highlighted. |
| **Weather** | Current conditions for a city selected in **Desktop widgets → Weather**, provided by Open-Meteo. |
| **System monitor** | CPU, GPU, memory, and network activity when Windows provides the readings. |
| **Disk space** | Used and available space on the system drive. |

</details>

## Full screen

Full screen combines the visualizer with a configurable backdrop and optional media overlays.

<details>
<summary>Set up full screen and use keyboard controls</summary>

Open **Media & lyrics → Full screen**. Choose **Studio**, **Album**, **Image**, or **Transparent** as the backdrop. **Choose image or video…** loads a local file for the Image backdrop. Toggle playback controls, clock, and lyrics independently. Enable the full-screen Neon Sign from its own widget settings.

Use **Enter full screen**, the dashboard's **Full screen** button, or the tray menu.

| Key | Action |
| --- | --- |
| <kbd>Space</kbd> | Play or pause. |
| <kbd>←</kbd> / <kbd>→</kbd> | Previous or next track. |
| <kbd>V</kbd> | Cycle the visualizer pattern. |
| <kbd>C</kbd> | Cycle the color palette. |
| <kbd>Esc</kbd> | Exit full screen. |

</details>

## Audio and system

Open **Audio & system** to choose what Sada listens to and how it behaves in Windows.

<details>
<summary>Change your input, display, and performance settings</summary>

- **Capture device** selects the default Windows playback output, another output, or a microphone. Use **Refresh devices** after connecting new hardware. **Sensitivity** changes how strongly visuals react, and **Smoothing** controls how quickly they follow signal changes.
- **Frame cap** limits rendering to 24, 30, 60, or 120 fps. **Reduce refresh while silent** and **Pause covered widgets** can reduce drawing when widgets are inactive or covered.
- **Widget monitor** chooses the active display. **Repeat full-size effects on every monitor** duplicates Environment, Wallpaper, Album backdrop, and Screen glow across displays.
- **Launch at Windows login** starts Sada in the tray. **Exit Sada** stops it.
- If **Global shortcuts** are enabled, <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>M</kbd> shows or hides desktop widgets, and <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> opens or closes full screen.

</details>
