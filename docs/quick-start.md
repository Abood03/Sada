# Quick start

[Home](../README.md) · [Feature explorer](features.md) · [System map](architecture.md)

## Run Sada

1. Open the [latest GitHub release](https://github.com/Abood03/Sada/releases/latest) and download the Windows x64 ZIP under **Assets**.
2. Extract the ZIP to a folder you want to keep, then open `Sada.exe`. The release is self-contained and has been tested on Windows 11.
3. Start playing a track. Use the Sada icon near the Windows clock to reopen the dashboard or enter full screen.

## Set up your desktop

1. Select **Edit desktop scene** in Sada or from the system tray menu.
2. Try **Album** for artwork and lyrics, **Karaoke** for more room for lyrics, or **Focus** for a quieter desktop.
3. Drag and resize the widgets. Use **Save** to apply the layout, **Save as** to keep it as a named scene, or **Cancel** to restore the previous layout.
4. For individual controls, open **Desktop widgets** and select the widget. You can set its placement, opacity, size, and lock state from there.

## Show lyrics

| Source | What to do |
| --- | --- |
| Spotify | Play a song and keep **Playback source** on **Automatic**, or select **Spotify** to use it exclusively. |
| YouTube | Play a music video and select **YouTube** under **Media & lyrics → Playback source**. Follow the [YouTube guide](youtube.md) if you want the optional extension or need to match the title manually. |
| Local lyrics | Use **Open lyrics file** under **Media & lyrics** to load a supported lyrics file. |

Enable the lyrics widget in **Desktop widgets** if it is hidden. If the words are early or late, open **Edit this recording** and use [Lyrics Studio](features.md#lyrics-studio) to adjust the timing.

## Set up Neon Sign

Open **Desktop widgets**, choose **Neon sign**, then enable it on the desktop or in full screen. The **Album cover** color preset follows the current artwork. See the [Neon Sign guide](neon-and-motion.md) for lettering, music response, and what remains visible when playback pauses.

## If something is missing

<details>
<summary><strong>I started Sada and can't see the widgets</strong></summary>

Open the dashboard from the Sada icon in the system tray. If the button reads **Show widgets**, select it. Then open **Desktop widgets**, choose the widget, and check that it is enabled. Desktop placement can put a widget behind open windows. Choose **Always on top** when you want it visible over other apps.

</details>

<details>
<summary><strong>The current track appears, but the lyrics are missing</strong></summary>

Check **Media & lyrics** for automatic lyrics lookup and the selected playback source. A match depends on the title, artist, and timing data reported for that recording. For YouTube, try **Match YouTube song**. You can also open a local lyrics file.

</details>

<details>
<summary><strong>The visualizer is quiet while a song plays</strong></summary>

Check the selected audio device and input type in **Audio & system**. For ordinary music playback, use the Windows output that is actually playing the song.

</details>
