using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Sada;

internal static class Program
{
    const BindingFlags NonpublicStatic = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags NonpublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Type Layout = typeof(App).Assembly.GetType("Sada.SceneLayout", true)!;
    static readonly Type Presets = typeof(App).Assembly.GetType("Sada.ScenePresets", true)!;

    [STAThread]
    static void Main()
    {
        var area = new Size(1280, 720);
        var options = new WidgetOptions { Width = 220, Height = 130, Left = 2320, Top = 300 };
        var bounds = Resolve(options, area, new Point(1920, 0), 1.25, 1.25);
        Equal(320, bounds.Left, "Legacy screen X respects physical origin and DPI");
        Equal(240, bounds.Top, "Legacy screen Y respects DPI");
        Layout.GetMethod("Store", NonpublicStatic)!.Invoke(null, [options, bounds, area]);
        Equal(bounds, Resolve(options, area, new Point(-1280, 0), 1.25, 1.25), "Monitor relocation retains the same relative layout");
        var resizedArea = new Size(1000, 600);
        var relocated = Resolve(options, resizedArea, default, 1, 1);
        Equal((1000d - 220) * 320 / (1280d - 220), relocated.Left, "Smaller monitor scales travel room");
        Equal((600d - 130) * 240 / (720d - 130), relocated.Top, "Smaller monitor scales vertical travel");
        Equal(null, options.Left, "Migration clears legacy X");
        Equal(null, options.Top, "Migration clears legacy Y");

        var clamped = Resolve(new WidgetOptions { Left = 2000, Top = -1000, Width = 3000, Height = 3000 }, area, default, 1, 1);
        Equal(new Rect(0, 0, 1280, 720), clamped, "Oversized disconnected legacy geometry remains on screen");

        var move = ReadSnap("Move", [new Rect(1076, 174, 200, 130), area, new[] { new Rect(501, 170, 212, 95) }, true]);
        Equal(new Rect(1080, 170, 200, 130), move.Item1, "Moving snaps to monitor edge and peer");
        Equal((double?)1280, move.Item2, "Vertical guide matches monitor edge");
        Equal((double?)170, move.Item3, "Horizontal guide matches peer");
        var centered = ReadSnap("Move", [new Rect(535, 174, 200, 130), area, Array.Empty<Rect>(), true]);
        Equal(540, centered.Item1.Left, "Moving snaps widget center to monitor center");
        var unsnapped = ReadSnap("Move", [new Rect(1076, 174, 200, 130), area, Array.Empty<Rect>(), false]);
        Equal((null, null), (unsnapped.Item2, unsnapped.Item3), "Alt bypass removes guides");
        Equal(1076, unsnapped.Item1.Left, "Alt bypass keeps exact placement");
        var resized = ReadSnap("Resize", [new Rect(100, 100, 200, 130), new Vector(197, 13), area, new[] { new Rect(500, 245, 100, 100) }, true]);
        Equal(new Rect(100, 100, 400, 145), resized.Item1, "Resize snaps right and bottom while retaining origin");

        var original = new SceneOptions { Name = "Existing V1 setup", LyricsPath = "local.lrc", RingCover = false };
        original.Visual.Mode = VisualMode.Waterfall;
        original.Widget(WidgetKind.Clock).Enabled = true;
        var album = Apply("Album", original, area);
        Equal("Existing V1 setup", original.Name, "Preview does not alter saved name");
        Equal(VisualMode.Waterfall, original.Visual.Mode, "Preview does not mutate existing visual settings");
        Equal(true, original.Widget(WidgetKind.Clock).Enabled, "Preview leaves V1 widget enablement intact");
        Equal("local.lrc", album.LyricsPath, "Preset preserves lyric source");
        Equal("Album", album.Name, "Album preset exposes its own name");
        Equal(true, album.RingCover && album.MoodColor && album.LinkCoverAndLyrics, "Album shows cover colors and grouped lyrics");
        var albumCircle = Resolve(album.Widget(WidgetKind.Visualizer), area, default, 1, 1);
        var albumLyrics = Resolve(album.Widget(WidgetKind.Lyrics), area, default, 1, 1);
        Equal(albumCircle.Left + albumCircle.Width / 2, albumLyrics.Left + albumLyrics.Width / 2, "Cover and lyrics centers are aligned");
        var karaoke = Apply("Karaoke", original, area);
        Equal(true, karaoke.LyricsTextSize > album.LyricsTextSize && karaoke.Widget(WidgetKind.Visualizer).Width < album.Widget(WidgetKind.Visualizer).Width, "Karaoke gives lyrics the visual priority");
        var focus = Apply("Focus", original, area);
        Equal(true, focus.Widget(WidgetKind.Clock).Enabled && focus.Visual.Mode == VisualMode.Bars && !focus.Widget(WidgetKind.Lyrics).Enabled, "Focus shows clock and bottom bars");
        Equal(album.Widget(WidgetKind.Visualizer).RelativeLeft, Preferences.Clone(album).Widget(WidgetKind.Visualizer).RelativeLeft, "Saved scene clone retains normalized geometry");
        TestDraft(original);
    }

    static void TestDraft(SceneOptions original)
    {
        var app = new App();
        var preferences = new Preferences { Scene = original };
        typeof(App).GetProperty("Prefs", NonpublicInstance)!.SetValue(app, preferences);
        var draftField = typeof(App).GetField("sceneDraft", NonpublicInstance)!;
        var editorType = typeof(App).Assembly.GetType("Sada.SceneEditor", true)!;
        var draft = Preferences.Clone(original);
        draftField.SetValue(app, draft);
        int previewChanges = 0;
        SceneOptions? proposed = null;
        string? name = null;
        Action<SceneOptions> preview = value => { previewChanges++; draftField.SetValue(app, value); };
        Func<SceneOptions, string?, bool> save = (options, label) => { proposed = options; name = label; return false; };
        var editor = (Window)Activator.CreateInstance(editorType, NonpublicInstance, null, [app, draft, preview, save, null], null)!;
        var stage = (Grid)editor.Content;
        stage.Measure(new Size(1280, 720));
        stage.Arrange(new Rect(0, 0, 1280, 720));
        editorType.GetMethod("PreviewPreset", NonpublicInstance)!.Invoke(editor, null);
        Equal(1, previewChanges, "Desktop editor publishes an editable draft");
        Equal("Existing V1 setup", preferences.Scene.Name, "Preview leaves committed settings untouched");
        Equal("Album", ((SceneOptions)draftField.GetValue(app)!).Name, "Renderer sees the preview scene");
        var nameBox = (TextBox)editorType.GetField("name", NonpublicInstance)!.GetValue(editor)!;
        nameBox.Text = " Evening ";
        editorType.GetMethod("Save", NonpublicInstance)!.Invoke(editor, [true]);
        Equal("Evening", name, "Save as hands the new profile name to the app");
        Equal("Album", proposed?.Name, "Save as submits the current draft");
        Equal("Existing V1 setup", preferences.Scene.Name, "Editor leaves production settings alone on a failed commit");
        editorType.GetMethod("ReleasePreviews", NonpublicInstance)!.Invoke(editor, null);
    }

    static Rect Resolve(WidgetOptions options, Size area, Point origin, double scaleX, double scaleY) =>
        (Rect)Layout.GetMethod("Resolve", NonpublicStatic)!.Invoke(null, [WidgetKind.Visualizer, options, area, origin, scaleX, scaleY])!;

    static (Rect, double?, double?) ReadSnap(string method, object[] args) =>
        ((Rect, double?, double?))Layout.GetMethod(method, NonpublicStatic)!.Invoke(null, args)!;

    static SceneOptions Apply(string preset, SceneOptions original, Size area) =>
        (SceneOptions)Presets.GetMethod("Apply", NonpublicStatic)!.Invoke(null, [preset, original, area])!;

    static void Equal<T>(T expected, T actual, string label)
    {
        if (expected is double a && actual is double b && Math.Abs(a - b) <= .001 ||
            expected is Rect first && actual is Rect second &&
            Math.Abs(first.X - second.X) <= .001 && Math.Abs(first.Y - second.Y) <= .001 &&
            Math.Abs(first.Width - second.Width) <= .001 && Math.Abs(first.Height - second.Height) <= .001)
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}. Expected {expected}, got {actual}.");
        Console.WriteLine("PASS " + label);
    }
}
