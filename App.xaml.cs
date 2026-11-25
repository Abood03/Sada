using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Sada;

public partial class App : Application
{
    internal static App Studio => (App)Current;
    internal Preferences Prefs { get; private set; } = null!;
    internal SceneOptions Scene => sceneDraft ?? Prefs.Scene;
    internal AudioEngine Audio { get; } = new();
    internal MediaService Media { get; } = new();
    internal DesktopData Data { get; private set; } = null!;
    internal LyricsTrack? Lyrics { get; private set; }
    internal string LyricsMessage { get; private set; } = "Waiting for playback";
    internal string Status { get; private set; } = "";
    internal bool WidgetsVisible => !Prefs.HiddenWidgets;
    Dashboard dashboard = null!;
    Forms.NotifyIcon? tray;
    Forms.ContextMenuStrip? trayMenu;
    readonly Dictionary<(WidgetKind, string), DesktopWidget> widgets = new();
    readonly DispatcherTimer renderTimer = new(DispatcherPriority.Render);
    readonly DispatcherTimer updateTimer = new(DispatcherPriority.Background);
    readonly DispatcherTimer mediaTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly Stopwatch timer = Stopwatch.StartNew();
    readonly SongSync lyricSync = new();
    readonly Dictionary<string, (LyricsTrack Source, double Seconds)> automaticLyricOffsets = new();
    string analyzingSong = "";
    long lastTimingSignal;
    long lastFrame;
    long lastLyricsFrame;
    long lastDesktopCheck;
    long lastProfileCheck;
    int lastScheduledMinute = -1;
    SceneProfile? activeProcessProfile;
    SceneOptions? sceneBeforeProcess;
    SceneOptions? sceneDraft;
    SceneEditor? sceneEditor;
    DesktopWidget? full;
    HwndSource? hotkeySource;
    Mutex? singleInstance;
    EventWaitHandle? showRequested;
    RegisteredWaitHandle? showRegistration;
    bool refreshingMedia;
    bool quitting;
    bool ownsMutex;
    bool hotkeysRegistered;
    string lyricIdentity = "";
    string observedLyricsTrack = "";
    string suppressedLyricsTrack = "";
    DateTimeOffset retryLyricsAfter;
    CancellationTokenSource? lyricsRequest;
    readonly Dictionary<string, LyricsTrack> lyricsCache = new(StringComparer.OrdinalIgnoreCase);
    LyricsTrack? manualLyrics;
    string automaticWeatherRequest = "";
    DateTimeOffset nextWeatherRefresh;
    bool lookingUpWeather;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (YouTubeBridge.IsHostLaunch(e.Args))
        {
            _ = RunYouTubeHostAsync();
            return;
        }
        singleInstance = new Mutex(true, @"Local\Sada.Personal.Desktop", out ownsMutex);
        showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sada.Personal.Show");
        if (!ownsMutex)
        {
            showRequested.Set();
            Shutdown();
            return;
        }
        Prefs = Preferences.Load();
        SetUpYouTubePlayback();
        Data = new DesktopData(Preferences.Folder);
        if (!File.Exists(Path.Combine(Preferences.Folder, "settings.json")))
            Scene.Widget(WidgetKind.Visualizer).Enabled = true;
        if (Prefs.LyricsSetupVersion == 0)
        {
            Scene.AutoLyrics = true;
            Scene.Widget(WidgetKind.Lyrics).Enabled = true;
            foreach (var profile in Prefs.Profiles) profile.Scene.AutoLyrics = true;
            Prefs.LyricsSetupVersion = 1;
            SaveNow();
        }
        dashboard = new Dashboard(this);
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showRequested, (state, timedOut) => Dispatcher.BeginInvoke(ShowDashboard), null, Timeout.Infinite, false);
        MainWindow = dashboard;
        var hwnd = DesktopInterop.WindowHandle(dashboard);
        DesktopInterop.UseDarkCaption(hwnd);
        hotkeySource = HwndSource.FromHwnd(hwnd);
        hotkeySource?.AddHook(HotkeyHook);
        dashboard.Closing += (_, args) =>
        {
            if (quitting) return;
            args.Cancel = true;
            dashboard.Hide();
        };
        SetupTray();
        RestartAudio();
        ConfigureHotkeys();
        if (!e.Args.Contains("--tray")) ShowDashboard();
        RefreshWidgets();
        _ = RestoreLyricsAsync();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
        renderTimer.Interval = TimeSpan.FromMilliseconds(8);
        renderTimer.Tick += (_, _) => Render();
        renderTimer.Start();
        mediaTimer.Tick += async (_, _) => await RefreshMediaAsync();
        mediaTimer.Start();
        updateTimer.Interval = TimeSpan.FromSeconds(1);
        updateTimer.Tick += (_, _) => Sample();
        updateTimer.Start();
        _ = RefreshMediaAsync();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    void SetupTray()
    {
        trayMenu = new Forms.ContextMenuStrip();
        tray = new Forms.NotifyIcon { Text = "Sada · Desktop audio", Icon = CreateIcon(), ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => ShowDashboard();
        trayMenu.Opening += (_, _) =>
        {
            foreach (var item in trayMenu.Items.Cast<Forms.ToolStripItem>().ToArray()) { trayMenu.Items.Remove(item); item.Dispose(); }
            trayMenu.Items.Add("Open Sada Studio", null, (_, _) => ShowDashboard());
            trayMenu.Items.Add("Edit desktop scene", null, (_, _) => OpenSceneEditor());
            trayMenu.Items.Add(WidgetsVisible ? "Hide desktop widgets" : "Show desktop widgets", null, (_, _) => ToggleWidgets());
            trayMenu.Items.Add("Full screen", null, (_, _) => ToggleFull());
            if (Prefs.Profiles.Count != 0)
            {
                var profiles = new Forms.ToolStripMenuItem("Profiles");
                foreach (var profile in Prefs.Profiles) profiles.DropDownItems.Add(profile.Name, null, (_, _) => ApplyProfile(profile));
                trayMenu.Items.Add(profiles);
            }
            trayMenu.Items.Add("Exit Sada", null, (_, _) => ExitApp());
        };
    }

    static System.Drawing.Icon CreateIcon()
    {
        if (Environment.ProcessPath is { } path)
        {
            try { return System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application; }
            catch (IOException) { }
        }
        return System.Drawing.SystemIcons.Application;
    }

    void DisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        sceneEditor?.DisplaysChanged();
        RefreshWidgets(rebuild: true);
    });

    internal void ShowDashboard()
    {
        if (sceneEditor is { IsVisible: true })
        {
            sceneEditor.Activate();
            return;
        }
        dashboard.Show();
        if (dashboard.WindowState == WindowState.Minimized) dashboard.WindowState = WindowState.Normal;
        dashboard.Activate();
    }

    internal void ShowNeonSettings()
    {
        ShowDashboard();
        if (dashboard.IsVisible) dashboard.ShowNeonSettings();
    }

    internal void RestartAudio()
    {
        lyricSync.Reset();
        analyzingSong = "";
        lastTimingSignal = 0;
        automaticLyricOffsets.Clear();
        var source = Audio.GetDevices().FirstOrDefault(d => (d.Id ?? "") == Prefs.AudioId && d.IsMicrophone == Prefs.Microphone);
        if (!Audio.Start(source)) SetStatus(Audio.Error ?? "Audio capture could not start.");
        else SetStatus(source?.Name ?? "Listening to Windows playback");
    }

    internal void SetStatus(string text)
    {
        Status = text;
        dashboard?.UpdateStatus(text);
    }

    internal void ToggleWidgets()
    {
        if (sceneDraft is not null) return;
        Prefs.HiddenWidgets = !Prefs.HiddenWidgets;
        ApplyChanges();
    }

    internal void ConfigureHotkeys()
    {
        if (hotkeySource is null) return;
        DesktopInterop.UnregisterHotKey(hotkeySource.Handle, 1);
        DesktopInterop.UnregisterHotKey(hotkeySource.Handle, 2);
        hotkeysRegistered = false;
        if (!Prefs.Hotkeys) return;
        const uint ctrlAltShift = 0x4007;
        var widgetsKey = DesktopInterop.RegisterHotKey(hotkeySource.Handle, 1, ctrlAltShift, 0x4d);
        var fullscreenKey = DesktopInterop.RegisterHotKey(hotkeySource.Handle, 2, ctrlAltShift, 0x46);
        hotkeysRegistered = widgetsKey || fullscreenKey;
        if (!widgetsKey || !fullscreenKey) SetStatus("A global shortcut is already in use. Tray controls are available.");
    }

    nint HotkeyHook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message != DesktopInterop.HotkeyMessage) return 0;
        if (wparam == 1) ToggleWidgets();
        if (wparam == 2) ToggleFull();
        handled = true;
        return 0;
    }

    internal bool SetStartup(bool enabled)
    {
        try
        {
            using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (startup is null) return false;
            if (enabled) startup.SetValue("Sada", $"\"{Environment.ProcessPath}\" --tray");
            else startup.DeleteValue("Sada", false);
            Prefs.StartAtLogin = enabled;
            QueueSave();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { SetStatus(ex.Message); return false; }
    }

    internal void QueueSave() { saveTimer.Stop(); saveTimer.Start(); }
    void SaveNow()
    {
        try { Prefs.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Settings could not be saved: " + ex.Message); }
    }

    internal void ApplyChanges(bool rebuild = false)
    {
        RefreshWidgets(rebuild);
        dashboard?.UpdateScene();
        QueueSave();
    }

    internal void ApplyProfile(SceneProfile profile, bool fromProcess = false)
    {
        if (sceneDraft is not null) return;
        if (!fromProcess) { activeProcessProfile = null; sceneBeforeProcess = null; }
        Prefs.Scene = Preferences.Clone(profile.Scene);
        Prefs.Scene.Name = profile.Name;
        dashboard.ReloadPage();
        ApplyChanges(true);
        _ = RestoreLyricsAsync();
        SetStatus("Profile loaded · " + profile.Name);
    }

    internal void OpenSceneEditor(string? preset = null)
    {
        if (sceneEditor is not null)
        {
            sceneEditor.Activate();
            return;
        }

        full?.Close();
        sceneDraft = Preferences.Clone(Prefs.Scene);
        try
        {
            var editor = new SceneEditor(this, sceneDraft, draft => sceneDraft = draft, CommitScene, preset);
            sceneEditor = editor;
            editor.Closed += (_, _) =>
            {
                sceneEditor = null;
                sceneDraft = null;
                if (quitting) return;
                RefreshWidgets(rebuild: true);
                dashboard.ReloadPage();
            };
            dashboard.Hide();
            RefreshWidgets();
            editor.Show();
            editor.Activate();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            sceneEditor?.Close();
            sceneEditor = null;
            sceneDraft = null;
            RefreshWidgets();
            ShowDashboard();
            SetStatus("Could not open the desktop editor: " + exception.Message);
        }
    }

    bool CommitScene(SceneOptions draft, string? profileName)
    {
        var previous = Prefs.Scene;
        var profiles = Prefs.Profiles;
        var next = Preferences.Clone(draft);
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            next.Name = profileName.Trim();
            Prefs.Profiles = profiles.ToList();
            var index = Prefs.Profiles.FindIndex(item => string.Equals(item.Name, next.Name, StringComparison.OrdinalIgnoreCase));
            var entry = new SceneProfile(next.Name, Preferences.Clone(next),
                index < 0 ? null : Prefs.Profiles[index].Process, index < 0 ? null : Prefs.Profiles[index].StartTime);
            if (index < 0) Prefs.Profiles.Add(entry);
            else Prefs.Profiles[index] = entry;
        }

        Prefs.Scene = next;
        bool wasHidden = Prefs.HiddenWidgets;
        Prefs.HiddenWidgets = false;
        try
        {
            Prefs.Save();
            activeProcessProfile = null;
            sceneBeforeProcess = null;
            SetStatus("Scene saved · " + next.Name);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Prefs.Scene = previous;
            Prefs.Profiles = profiles;
            Prefs.HiddenWidgets = wasHidden;
            MessageBox.Show(sceneEditor!, "Your layout is still open. " + exception.Message, "Could not save the scene",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    async Task RestoreLyricsAsync()
    {
        CancelLyricsLookup();
        Lyrics = null;
        manualLyrics = null;
        lyricIdentity = "";
        observedLyricsTrack = "";
        suppressedLyricsTrack = "";
        retryLyricsAfter = default;
        var scene = Scene;
        var path = scene.LyricsPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var parsed = await Media.LoadLyricsAsync(path);
            if (ReferenceEquals(scene, Scene) && scene.LyricsPath == path)
            {
                manualLyrics = parsed;
                SyncLyrics(Media.Current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        { SetLyricsMessage("Lyrics file could not be opened: " + ex.Message); }
    }

    internal async Task LoadLyricsAsync(string path)
    {
        var scene = Scene;
        var trackId = Media.Current.LyricsKey;
        try
        {
            var parsed = await Media.LoadLyricsAsync(path);
            if (!ReferenceEquals(scene, Scene)) return;
            manualLyrics = parsed;
            scene.LyricsPath = path;
            scene.LyricsFileTrack = trackId;
            QueueSave();
            if (ManualLyricsApply(Media.Current.LyricsKey))
            {
                CancelLyricsLookup();
#if SADA_LYRICS_STUDIO
                if (TryApplyRecordingTiming(Media.Current)) return;
#endif
                SetLyrics(trackId, parsed, "Local file");
            }
            else SetLyricsMessage("Saved the local file for its original track");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        { SetLyricsMessage("Lyrics file could not be opened: " + ex.Message); }
    }

    internal Task FetchLyricsAsync()
    {
        var track = Media.Current;
        if (!track.HasTrack)
        { SetLyricsMessage("Play a song first"); return Task.CompletedTask; }
        if (IsYouTubePlayback(track) && ResolveYouTubeSong(track) is null)
        { SetLyricsMessage("Choose Match YouTube song to enter its title and artist"); return Task.CompletedTask; }
        if (!IsYouTubePlayback(track) && string.IsNullOrWhiteSpace(track.Artist))
        { SetLyricsMessage("Waiting for the artist name"); return Task.CompletedTask; }
        var key = LyricsRequestKey(track);
        if (lyricsRequest is not null && key == observedLyricsTrack) return Task.CompletedTask;
        CancelLyricsLookup();
        observedLyricsTrack = key;
        suppressedLyricsTrack = "";
#if SADA_LYRICS_STUDIO
        if (TryApplyRecordingTiming(track)) return Task.CompletedTask;
#endif
        return LookupLyricsAsync(track, manualSearch: true);
    }

    internal void SetAutomaticLyrics(bool value)
    {
        Scene.AutoLyrics = value;
        suppressedLyricsTrack = "";
        retryLyricsAfter = default;
        if (!value) CancelLyricsLookup();
        SyncLyrics(Media.Current);
        QueueSave();
    }

    internal void ClearLyrics()
    {
        CancelLyricsLookup();
        Lyrics = null;
        manualLyrics = null;
        Scene.LyricsPath = "";
        Scene.LyricsFileTrack = "";
        lyricIdentity = "";
        suppressedLyricsTrack = Media.Current.LyricsKey;
        QueueSave();
        SetLyricsMessage(suppressedLyricsTrack.Length == 0 ? "Waiting for playback" : "Lyrics cleared for this track");
    }

    internal async Task WeatherAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) { SetStatus("Enter a city to search Open-Meteo."); return; }
        if (lookingUpWeather) return;
        SetStatus("Looking up " + city.Trim() + " …");
        lookingUpWeather = true;
        try
        {
            var weather = await Data.SearchWeatherAsync(city.Trim());
            if (weather is null) { SetStatus("No weather results for " + city.Trim()); return; }
            Scene.WeatherCity = city.Trim();
            automaticWeatherRequest = Scene.WeatherCity;
            nextWeatherRefresh = DateTimeOffset.Now.AddMinutes(20);
            QueueSave();
            SetStatus("Weather updated · " + weather.Place);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { SetStatus("Weather lookup failed: " + ex.Message); }
        finally { lookingUpWeather = false; }
    }

    async Task UpdateWeatherAsync()
    {
        var city = Scene.WeatherCity;
        if (lookingUpWeather || string.IsNullOrWhiteSpace(city) || !Scene.Widget(WidgetKind.Weather).Enabled) return;
        if (city == automaticWeatherRequest && DateTimeOffset.Now < nextWeatherRefresh) return;
        lookingUpWeather = true;
        automaticWeatherRequest = city;
        nextWeatherRefresh = DateTimeOffset.Now.AddMinutes(20);
        try { await Data.SearchWeatherAsync(city); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { SetStatus("Weather refresh failed: " + ex.Message); }
        finally { lookingUpWeather = false; }
    }

    void SyncLyrics(MediaSnapshot track)
    {
        var id = track.LyricsKey;
        var lookupKey = LyricsRequestKey(track);
        if (lookupKey != observedLyricsTrack)
        {
            CancelLyricsLookup();
            observedLyricsTrack = lookupKey;
            suppressedLyricsTrack = "";
            retryLyricsAfter = default;
            Lyrics = null;
            lyricIdentity = "";
        }
        if (id.Length != 0 && id == suppressedLyricsTrack) return;
#if SADA_LYRICS_STUDIO
        if (TryApplyRecordingTiming(track)) return;
#endif
        if (ManualLyricsApply(id) && !ReferenceEquals(Lyrics, manualLyrics) && lyricsRequest is null)
        { SetLyrics(id, manualLyrics!, "Local file"); return; }
        if (lyricsRequest is not null || (Lyrics is not null && id == lyricIdentity)) return;
        if (!track.HasTrack)
        {
            SetLyricsMessage(Media.SourcePreference == MediaSourcePreference.YouTube ? "Play a YouTube video in the browser" : "Waiting for playback");
            return;
        }
        if (!Scene.AutoLyrics) { SetLyricsMessage("Automatic lyrics are off"); return; }
        if (IsYouTubePlayback(track))
        {
            if (track.Duration <= TimeSpan.Zero) { SetLyricsMessage("YouTube timeline unavailable · try the browser add-on"); return; }
            if (ResolveYouTubeSong(track) is null) { SetLyricsMessage("YouTube song unknown · choose Match YouTube song"); return; }
        }
        else if (string.IsNullOrWhiteSpace(track.Artist)) { SetLyricsMessage("Waiting for track information"); return; }
        if (lyricsCache.TryGetValue(lookupKey, out var cached)) { SetLyrics(id, cached, "LRCLIB"); return; }
        if (DateTimeOffset.UtcNow >= retryLyricsAfter) _ = LookupLyricsAsync(track, manualSearch: false);
    }

    static string LyricsRequestKey(MediaSnapshot track)
    {
#if SADA_LYRICS_STUDIO
        if (LyricRecordingKey.From(track) is { } recording) return recording.Value;
#endif
        return track.LyricsKey;
    }

    bool ManualLyricsApply(string trackId) => manualLyrics is not null &&
        (Scene.LyricsFileTrack.Length == 0 || Scene.LyricsFileTrack == trackId);

    void SetLyrics(string trackId, LyricsTrack lyrics, string source)
    {
        Lyrics = lyrics;
        lyricIdentity = trackId;
        SetLyricsMessage(lyrics.Lines.Count > 0 ? $"Synced lyrics · {source}" : $"Untimed lyrics · {source}");
    }

    internal TimeSpan LyricsPosition
    {
        get
        {
            var song = Media.Current;
#if SADA_LYRICS_STUDIO
            if (IsEditedTiming) return song.Position;
#endif
            var adjusted = song.Position + TimeSpan.FromSeconds(LyricsOffsetFor(song.LyricsKey).Seconds);
            return adjusted > TimeSpan.Zero ? adjusted : TimeSpan.Zero;
        }
    }

    (double Seconds, bool Manual, bool Measured) LyricsOffsetFor(string key)
    {
        if (Prefs.SongLyricOffsets.TryGetValue(key, out var manual) && double.IsFinite(manual))
            return (Math.Clamp(manual, -5, 5), true, false);
        if (Prefs.AdaptiveLyricsTiming && !Prefs.Microphone && automaticLyricOffsets.TryGetValue(key, out var learned) &&
            ReferenceEquals(learned.Source, Lyrics)) return (learned.Seconds, false, true);
        return default;
    }

    internal bool HasSongTimingTarget => Media.Current.HasTrack
#if SADA_LYRICS_STUDIO
        && !IsEditedTiming
#endif
        ;

    internal string CurrentLyricTiming()
    {
        var song = Media.Current;
        if (!song.HasTrack) return "Play a song to set its timing.";
#if SADA_LYRICS_STUDIO
        if (IsEditedTiming) return $"{song.Title} · saved in Lyrics Studio";
#endif
        var (seconds, manual, measured) = LyricsOffsetFor(song.LyricsKey);
        var offset = Math.Abs(seconds) < .005 ? "source timing" : $"{Math.Abs(seconds):0.00} s {(seconds < 0 ? "later" : "earlier")}";
        var method = manual ? "Saved" : measured ? "Automatic match" : "Original";
        return $"{song.Title} · {method} · {offset}";
    }

    internal void AdjustCurrentLyricTiming(double change)
    {
        var key = Media.Current.LyricsKey;
        if (key.Length == 0) return;
        var current = LyricsOffsetFor(key).Seconds;
        if (Prefs.SongLyricOffsets.Count >= 250 && !Prefs.SongLyricOffsets.ContainsKey(key))
            Prefs.SongLyricOffsets.Remove(Prefs.SongLyricOffsets.Keys.First());
        Prefs.SongLyricOffsets[key] = Math.Clamp(Math.Round(current + change, 2), -5, 5);
        QueueSave();
    }

    internal void ClearCurrentLyricTiming()
    {
        if (Prefs.SongLyricOffsets.Remove(Media.Current.LyricsKey)) QueueSave();
    }

    void ObserveLyricTiming(AudioFrame frame, MediaSnapshot song)
    {
        var key = song.LyricsKey;
#if SADA_LYRICS_STUDIO
        if (IsEditedTiming) return;
#endif
        if (!Prefs.AdaptiveLyricsTiming || Prefs.Microphone || IsYouTubePlayback(song) || Lyrics is not { Lines.Count: > 0 } lyrics ||
            key.Length == 0 || key != lyricIdentity || Prefs.SongLyricOffsets.ContainsKey(key))
        {
            if (analyzingSong.Length > 0)
            {
                lyricSync.Reset();
                analyzingSong = "";
                lastTimingSignal = 0;
            }
            return;
        }
        if (analyzingSong != key)
        {
            lyricSync.Reset();
            analyzingSong = key;
            lastTimingSignal = 0;
        }
        if (!song.IsPlaying)
        {
            lyricSync.Observe(frame, lyrics, song.Position, false);
            return;
        }
        if (frame.SignalTimestamp <= 0 || frame.SignalTimestamp == lastTimingSignal) return;
        lastTimingSignal = frame.SignalTimestamp;
        double age = Stopwatch.GetElapsedTime(frame.SignalTimestamp).TotalSeconds;
        if (age < 0 || age > .28) return;
        var samplePosition = song.Position - TimeSpan.FromSeconds(age * song.PlaybackRate);
        if (samplePosition < TimeSpan.Zero) return;
        lyricSync.Observe(frame, lyrics, samplePosition, true);
        if (!lyricSync.HasEstimate) return;
        if (automaticLyricOffsets.Count >= 40 && !automaticLyricOffsets.ContainsKey(key))
            automaticLyricOffsets.Remove(automaticLyricOffsets.Keys.First());
        automaticLyricOffsets[key] = (lyrics, lyricSync.Correction.TotalSeconds);
    }

    void SetLyricsMessage(string text)
    {
        if (LyricsMessage == text) return;
        LyricsMessage = text;
        dashboard?.UpdateLyricsStatus(text);
    }

    void CancelLyricsLookup()
    {
        var pending = lyricsRequest;
        lyricsRequest = null;
        pending?.Cancel();
    }

    async Task LookupLyricsAsync(MediaSnapshot track, bool manualSearch)
    {
        using var request = new CancellationTokenSource();
        lyricsRequest = request;
        SetLyricsMessage("Finding lyrics…");
        try
        {
            var youtubeSong = IsYouTubePlayback(track) ? ResolveYouTubeSong(track) : null;
            var query = youtubeSong is { } song
                ? track with { Title = song.Title, Artist = song.Artist, Album = song.Album } : track;
            var lyrics = await Media.FetchLyricsAsync(query, request.Token, strictDuration: youtubeSong is not null,
                allowDurationMismatch: youtubeSong?.ManuallyMatched == true);
            var key = LyricsRequestKey(track);
            if (request.IsCancellationRequested || quitting || key != LyricsRequestKey(Media.Current)) return;
            if (lyrics is null)
            {
                retryLyricsAfter = DateTimeOffset.UtcNow.AddMinutes(15);
                SetLyricsMessage(youtubeSong is not null ? "No matching lyrics · check the video version or choose Match YouTube song" : "No lyrics found for this track");
                return;
            }
            if (manualSearch && ManualLyricsApply(track.LyricsKey))
            {
                manualLyrics = null;
                Scene.LyricsPath = "";
                Scene.LyricsFileTrack = "";
                QueueSave();
            }
            if (lyricsCache.Count >= 40) lyricsCache.Remove(lyricsCache.Keys.First());
            lyricsCache[key] = lyrics;
            SetLyrics(track.LyricsKey, lyrics, youtubeSong is not null ? "YouTube · LRCLIB" : "LRCLIB");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (LyricsRetryException ex) when (!request.IsCancellationRequested)
        {
            retryLyricsAfter = DateTimeOffset.UtcNow + ex.RetryAfter + TimeSpan.FromSeconds(1);
            SetLyricsMessage("Lyrics service is busy · retrying");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (request.IsCancellationRequested) return;
            retryLyricsAfter = DateTimeOffset.UtcNow.AddSeconds(20);
            SetLyricsMessage(Scene.AutoLyrics ? "Lyrics connection interrupted · retrying" : "Lyrics connection interrupted");
        }
        finally
        {
            if (ReferenceEquals(lyricsRequest, request)) lyricsRequest = null;
        }
    }

    async Task RefreshMediaAsync()
    {
        if (refreshingMedia) return;
        refreshingMedia = true;
        try
        {
            RefreshYouTubePlayback();
            await Media.RefreshAsync();
            if (!quitting) SyncLyrics(Media.Current);
        }
        finally { refreshingMedia = false; }
    }

    void Sample()
    {
        Data.Sample(Media.Current);
        _ = UpdateWeatherAsync();
        if (Audio.Error is { } error && !Audio.IsRunning && Status != error) SetStatus(error);
        if (timer.ElapsedMilliseconds - lastProfileCheck > 3100)
        {
            lastProfileCheck = timer.ElapsedMilliseconds;
            EvaluateProfiles();
        }
    }

    void EvaluateProfiles()
    {
        if (sceneDraft is not null) return;
        var now = DateTime.Now;
        var minute = now.Hour * 60 + now.Minute;
        if (minute != lastScheduledMinute)
        {
            lastScheduledMinute = minute;
            var profile = Prefs.Profiles.FirstOrDefault(p => p.StartTime == now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            if (profile != null) { ApplyProfile(profile); return; }
        }
        var process = DesktopInterop.ActiveProcess();
        if (string.Equals(process, "Sada", StringComparison.OrdinalIgnoreCase)) return;
        var candidate = Prefs.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Process) &&
            string.Equals(Path.GetFileNameWithoutExtension(p.Process), process, StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(activeProcessProfile, candidate)) return;
        if (candidate != null)
        {
            sceneBeforeProcess ??= Preferences.Clone(Scene);
            activeProcessProfile = candidate;
            ApplyProfile(candidate, true);
        }
        else if (sceneBeforeProcess != null)
        {
            Prefs.Scene = sceneBeforeProcess;
            sceneBeforeProcess = null;
            activeProcessProfile = null;
            dashboard.ReloadPage();
            ApplyChanges(true);
            _ = RestoreLyricsAsync();
        }
    }

    internal void RefreshWidgets(bool rebuild = false)
    {
        var screens = Forms.Screen.AllScreens;
        var primary = screens.FirstOrDefault(s => s.DeviceName == Scene.MonitorName) ?? Forms.Screen.PrimaryScreen ?? screens.FirstOrDefault();
        if (primary is null) return;
        var wanted = new Dictionary<(WidgetKind, string), System.Drawing.Rectangle>();
        if (WidgetsVisible && sceneDraft is null)
        {
            foreach (var (kind, options) in Scene.Widgets)
            {
                if (!options.Enabled) continue;
                if (kind == WidgetKind.Span)
                {
                    var combined = screens.Select(screen => screen.Bounds).Aggregate(System.Drawing.Rectangle.Union);
                    wanted[(kind, "span")] = combined;
                }
                else
                {
                    var targets = Scene.AllMonitors && DesktopWidget.IsWallpaperSize(kind) ? screens : new[] { primary };
                    foreach (var screen in targets) wanted[(kind, screen.DeviceName)] = DesktopWidget.IsWallpaperSize(kind) ? screen.Bounds : screen.WorkingArea;
                }
            }
        }
        foreach (var key in widgets.Keys.Where(k => rebuild || !wanted.ContainsKey(k)).ToArray())
        {
            var old = widgets[key];
            widgets.Remove(key);
            old.Close();
        }
        foreach (var (key, bounds) in wanted)
        {
            if (!widgets.TryGetValue(key, out var w))
            {
                w = new DesktopWidget(this, key.Item1, bounds);
                w.Closed += (_, _) =>
                {
                    if (widgets.TryGetValue(key, out var current) && ReferenceEquals(current, w)) widgets.Remove(key);
                };
                widgets.Add(key, w);
                w.Show();
            }
            w.Apply();
        }
        DesktopInterop.OrderDesktopWindows(widgets.Where(item => item.Value.ScenePlacement == Placement.Desktop)
            .OrderByDescending(item => item.Key.Item1 is WidgetKind.Wallpaper or WidgetKind.AlbumBackdrop ? 0 : item.Key.Item1 is WidgetKind.Environment ? 1 : 2)
            .Select(item => item.Value));
        full?.Apply();
    }

    internal void ToggleFull()
    {
        if (sceneDraft is not null) return;
        if (full is not null) { full.Close(); return; }
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var window = new DesktopWidget(this, WidgetKind.Visualizer, screen.Bounds, true);
        full = window;
        window.Closed += (_, _) => { if (full == window) full = null; };
        window.Show();
        window.Apply();
        window.Activate();
    }

    void Render()
    {
        var ms = timer.ElapsedMilliseconds;
        if (ms - lastLyricsFrame >= 16)
        {
            lastLyricsFrame = ms;
            sceneEditor?.RenderLyrics();
            if (dashboard.IsVisible && dashboard.WindowState != WindowState.Minimized) dashboard.RenderLyrics();
            foreach (var pair in widgets)
                if (pair.Key.Item1 == WidgetKind.Lyrics) pair.Value.RenderLyrics();
            full?.RenderLyrics();
        }
        var fps = Math.Clamp(Prefs.Fps == 0 ? 120 : Prefs.Fps, 15, 120);
        if (ms - lastFrame < 1000d / fps - 1) return;
        var frame = Audio.GetFrame();
        if (Prefs.ReduceOnSilence && frame.Level < .005 && ms - lastFrame < 50) return;
        lastFrame = ms;
        sceneEditor?.Render(frame);
        if (Lyrics is not null) ObserveLyricTiming(frame, Media.Current);
        if (dashboard.IsVisible && dashboard.WindowState != WindowState.Minimized) dashboard.Render(frame);
        if (ms - lastDesktopCheck > 4000)
        {
            lastDesktopCheck = ms;
            RefreshWidgets();
        }
        foreach (var w in widgets.Values)
            if (!Prefs.PauseWhenCovered || w.ScenePlacement == Placement.OnTop || !DesktopInterop.Covered(w)) w.Render(frame);
        full?.Render(frame);
    }

    internal void ExitApp()
    {
        quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        renderTimer.Stop();
        updateTimer.Stop();
        mediaTimer.Stop();
        saveTimer.Stop();
        CancelLyricsLookup();
        if (ownsMutex)
        {
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (hotkeysRegistered && hotkeySource is { IsDisposed: false })
            {
                DesktopInterop.UnregisterHotKey(hotkeySource.Handle, 1);
                DesktopInterop.UnregisterHotKey(hotkeySource.Handle, 2);
            }
            SaveNow();
            Data?.Dispose();
            singleInstance?.ReleaseMutex();
        }
        singleInstance?.Dispose();
        showRegistration?.Unregister(null);
        showRequested?.Dispose();
        tray?.Dispose();
        trayMenu?.Dispose();
        Audio.Dispose();
        Media.Dispose();
        youtubeBridge?.Dispose();
        base.OnExit(e);
    }
}
