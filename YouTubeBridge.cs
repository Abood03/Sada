using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Sada;

internal sealed record YouTubePlayback(string VideoId, string Title, string Artist, string Album,
    double Position, double Duration, double Rate, bool Playing, DateTimeOffset ObservedAt);

internal sealed class YouTubeBridge : IDisposable
{
    internal const string HostName = "com.sada.youtube";
    internal const string ExtensionId = "jafenianfjmlnaiepgbjeabccfdflpfd";
    const string ExtensionOrigin = "chrome-extension://" + ExtensionId + "/";
    const string DefaultPipe = "Sada.YouTube.Playback.v1";
    const int MaxFrameSize = 16 * 1024;
    const int MaxConnections = 8;
    const int MaxTabsPerConnection = 128;
    const double StaleSeconds = 5;
    const double RetainedTabSeconds = 20;

    static readonly byte[] Accepted = "{\"ok\":true}"u8.ToArray();
    static readonly byte[] Rejected = "{\"ok\":false}"u8.ToArray();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 12 };

    sealed record Packet(string? Type, int? TabId, long? Sequence, string? VideoId, string? Title,
        string? Artist, string? Album, double? Position, double? Duration, double? Rate, bool? Playing);

    sealed record Tab(long Sequence, long Order, long ReceivedAt, YouTubePlayback? Playback);

    readonly object gate = new();
    readonly string pipeName;
    readonly CancellationTokenSource stop = new();
    readonly HashSet<Dictionary<int, Tab>> connections = [];
    Task? workers;
    bool disposed;
    long activityOrder;

    internal YouTubeBridge(string pipeName = DefaultPipe) => this.pipeName = pipeName;

    internal bool IsConnected
    {
        get { lock (gate) return !disposed && connections.Count != 0; }
    }

    internal YouTubePlayback? Current
    {
        get
        {
            lock (gate)
            {
                if (disposed) return null;
                Tab? best = null;
                var now = Stopwatch.GetTimestamp();
                foreach (var connection in connections)
                foreach (var tab in connection.Values)
                {
                    if (tab.Playback is not { } playback || Stopwatch.GetElapsedTime(tab.ReceivedAt, now).TotalSeconds > StaleSeconds)
                        continue;
                    if (best?.Playback is not { } selected ||
                        (playback.Playing != selected.Playing ? playback.Playing : tab.Order > best.Order))
                        best = tab;
                }
                return best?.Playback;
            }
        }
    }

    internal void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            workers ??= Task.WhenAll(Enumerable.Range(0, MaxConnections).Select(_ => ListenAsync(stop.Token)));
        }
    }

    async Task ListenAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                await ReceiveAsync(pipe, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Allow other established clients to continue and avoid spinning if the name is unavailable.
                try { await Task.Delay(120, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            }
        }
    }

    async Task ReceiveAsync(Stream pipe, CancellationToken cancellation)
    {
        var tabs = new Dictionary<int, Tab>();
        lock (gate) connections.Add(tabs);
        try
        {
            while (await ReadFrameAsync(pipe, cancellation).ConfigureAwait(false) is { } frame)
                await WriteFrameAsync(pipe, Apply(tabs, frame) ? Accepted : Rejected, cancellation).ConfigureAwait(false);
        }
        finally { lock (gate) connections.Remove(tabs); }
    }

    bool Apply(Dictionary<int, Tab> tabs, byte[] frame)
    {
        Packet? packet;
        try { packet = JsonSerializer.Deserialize<Packet>(frame, Json); }
        catch (JsonException) { return false; }

        if (packet?.TabId is not int tabId || tabId < 0 || packet.Sequence is not long sequence ||
            sequence is <= 0 or > 9_007_199_254_740_991L || packet.Type is not ("closed" or "playback"))
            return false;

        var playback = packet.Type == "playback" ? ToPlayback(packet) : null;
        if (packet.Type == "playback" && playback is null) return false;

        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (tabs.Count >= MaxTabsPerConnection)
            {
                var expired = tabs.Where(pair => Stopwatch.GetElapsedTime(pair.Value.ReceivedAt, now).TotalSeconds > RetainedTabSeconds)
                    .Select(pair => pair.Key).ToArray();
                foreach (var key in expired) tabs.Remove(key);
            }

            tabs.TryGetValue(tabId, out var old);
            if (old is not null && old.Sequence >= sequence) return false;
            if (old is null && tabs.Count >= MaxTabsPerConnection) return false;
            bool continued = playback is not null && old?.Playback is { } previous &&
                previous.VideoId == playback.VideoId && previous.Playing == playback.Playing &&
                Stopwatch.GetElapsedTime(old.ReceivedAt, now).TotalSeconds <= StaleSeconds;
            tabs[tabId] = new Tab(sequence, continued ? old!.Order : ++activityOrder, now, playback);
        }
        return true;
    }

    static YouTubePlayback? ToPlayback(Packet packet)
    {
        if (packet.VideoId is not { Length: 11 } videoId ||
            !videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
            packet.Title is not { Length: > 0 and <= 400 } title || string.IsNullOrWhiteSpace(title) ||
            !ValidMetadata(title, 400) || !ValidMetadata(packet.Artist, 200) || !ValidMetadata(packet.Album, 200) ||
            packet.Position is not double position || !double.IsFinite(position) || position < 0 ||
            packet.Duration is not double duration || !double.IsFinite(duration) || duration is <= 0 or > 86_400 ||
            position > duration + .25 || packet.Rate is not double rate || !double.IsFinite(rate) || rate is <= 0 or > 16 ||
            packet.Playing is not bool playing)
            return null;

        return new YouTubePlayback(videoId, title.Trim(), packet.Artist?.Trim() ?? "", packet.Album?.Trim() ?? "",
            Math.Min(position, duration), duration, rate, playing, DateTimeOffset.UtcNow);
    }

    static bool ValidMetadata(string? value, int maximum) =>
        value is null || value.Length <= maximum && !value.Any(char.IsControl);

    internal static bool IsHostLaunch(string[] args) => args.Length is 1 or 2 &&
        string.Equals(args[0], ExtensionOrigin, StringComparison.Ordinal) &&
        (args.Length == 1 || args[1].StartsWith("--parent-window=", StringComparison.Ordinal));

    internal static async Task RunHostAsync()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await RelayAsync(input, output, DefaultPipe).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    internal static async Task RelayAsync(Stream input, Stream output, string pipeName)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            while (await ReadFrameAsync(input, CancellationToken.None).ConfigureAwait(false) is { } frame)
            {
                byte[] reply = Rejected;
                try
                {
                    if (pipe is null)
                    {
                        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.ConnectAsync(350).ConfigureAwait(false);
                    }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await WriteFrameAsync(pipe, frame, timeout.Token).ConfigureAwait(false);
                    reply = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
                {
                    pipe?.Dispose();
                    pipe = null;
                }
                await WriteFrameAsync(output, reply, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { pipe?.Dispose(); }
    }

    static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        int read = await stream.ReadAsync(prefix, cancellation).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < prefix.Length)
            await stream.ReadExactlyAsync(prefix.AsMemory(read), cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxFrameSize) throw new InvalidDataException("Invalid browser message length.");

        byte[] message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellation).ConfigureAwait(false);
        return message;
    }

    static async Task WriteFrameAsync(Stream stream, byte[] message, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, message.Length);
        await stream.WriteAsync(prefix, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task? running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            running = workers;
        }
        stop.Cancel();
        if (running is null) stop.Dispose();
        else _ = running.ContinueWith(_ => stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
