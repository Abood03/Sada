# Neon Sign and motion

[Sada](../README.md) / [Feature explorer](features.md)

## Set up Neon Sign

1. Open **Desktop widgets** and choose **Neon sign**.
2. Enable **Show neon on the desktop**, **Show neon in full screen**, or both.
3. Position the desktop sign in the scene editor. Drag the sign in full-screen mode to save a separate position.

Neon Sign displays the song title and artist in luminous letters. **Album cover** uses the dominant artwork color for the title and an accent color for the artist. The colors refresh as artwork becomes available or the track changes.

### Appearance controls

| Control | What it changes |
| --- | --- |
| **Lettering** | Choose Script, Block, or Tall Capitals. |
| **Colors** | Use the album cover, Sada palette, Warm White, visualizer palette, or custom title and artist colors. |
| **Music response** and **Response strength** | Choose Chase, Equalizer, Power surge, Flare, or Steady, then adjust the intensity. |
| **Tube flicker** | Choose whether lit letters flicker. |
| **Acrylic backing** | Add a panel behind the tubes. |
| **Show artist** and **Show music note** | Show or hide the secondary elements. |
| **Click desktop sign to play or pause** | Let a click on the desktop sign toggle playback when its window permits input. |

<details>
<summary>What happens when playback stops?</summary>

The glow disappears and the unlit glass tubes remain visible. Under **After playback stops**, choose **Show unlit tubes** to leave them visible. **Fade unlit tubes** and **Hide sign** take effect after six seconds. When playback resumes after the tubes have gone dark, the title lights up progressively with animations enabled.

</details>

## Lyrics and visualizer transitions

**Flow** shows the previous, current, and next lyric lines. On a natural line change, the stack slides into place as the outgoing edge fades. The active word fills as playback advances through its timing range, while preserving Arabic shaping. A seek updates the highlight to the new playback position.

The visualizer briefly blends the outgoing and incoming drawing when supported appearance settings change. The visualizer, Neon Sign, and natural lyric line transitions honor the Windows animation setting.

[Automated coverage and manual checks](testing.md)
