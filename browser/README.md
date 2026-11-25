# Sada browser extension

The optional extension sends YouTube playback timing from Chrome or Edge to the Sada desktop app. It follows standard `www.youtube.com/watch` pages and suspends the song report while it detects an ad.

[Sada on GitHub](https://github.com/Abood03/Sada) · [YouTube setup guide](https://github.com/Abood03/Sada/blob/main/docs/youtube.md)

## Load the extension

1. Run `Sada.exe` from its final location and choose **Media & lyrics → Register browser bridge**.
2. In Chrome or Edge, open the extensions page and enable **Developer mode**.
3. Choose **Load unpacked** and select this `browser` folder.
4. Reload the YouTube tab. In Sada, select **YouTube** as the playback source.

## Local messaging

The native messaging host is `com.sada.youtube`. The extension uses the fixed ID `jafenianfjmlnaiepgbjeabccfdflpfd`. Chrome or Edge passes playback metadata and timing to Sada through the local host. [Chrome native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)

## Test from source

Use Node.js to run the dependency-free browser tests from the repository root. They exercise the extension's message handling and video state tracking with simulated tabs and players.

```powershell
node --test .\browser\tests\extension.test.cjs
```
