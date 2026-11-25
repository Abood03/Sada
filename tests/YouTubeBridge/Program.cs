using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Sada;

var origin = $"chrome-extension://{YouTubeBridge.ExtensionId}/";
Check(YouTubeBridge.IsHostLaunch([origin, "--parent-window=0"]), "registered extension can launch native mode");
Check(!YouTubeBridge.IsHostLaunch(["chrome-extension://unknown/"]) && !YouTubeBridge.IsHostLaunch(["--tray"]),
    "other extensions and desktop launches cannot select native mode");

string pipeName = $"Sada.YouTube.Test.{Guid.NewGuid():N}";
using var bridge = new YouTubeBridge(pipeName);
bridge.Start();
Check(!bridge.IsConnected && bridge.Current is null, "no YouTube session before a connection");

using var first = await Connect();
using var second = await Connect();
Check(await Send(first, Playback(1, "aaaaaaaaaaa", 10)) && bridge.Current is { VideoId: "aaaaaaaaaaa", Title: "أغنية تجريبية", Position: 10 },
    "valid framed metadata is read without losing Arabic text");
Check(await Send(second, Playback(2, "bbbbbbbbbbb", 2)) && bridge.Current?.VideoId == "bbbbbbbbbbb",
    "latest playback start takes priority even with coinciding browser tab IDs");
Check(await Send(first, Playback(3, "aaaaaaaaaaa", 11)) && bridge.Current?.VideoId == "bbbbbbbbbbb",
    "a heartbeat from the older playing tab does not steal focus");
Check(await Send(second, Playback(4, "bbbbbbbbbbb", 3, false)) && bridge.Current is { VideoId: "aaaaaaaaaaa", Playing: true },
    "playing tab takes priority when another tab pauses");
Check(await Send(first, Playback(5, "aaaaaaaaaaa", 12, false)) && bridge.Current is { VideoId: "aaaaaaaaaaa", Playing: false, Position: 12 },
    "last paused tab keeps its exact position");
Check(!await Send(first, Playback(4, "aaaaaaaaaaa", 1)) && bridge.Current?.Position == 12,
    "old sequence cannot overwrite a newer pause");
Check(!await Send(first, Playback(6, "aaaaaaaaaaa", 500)) && bridge.Current?.Position == 12 &&
      !await Send(first, "{"u8.ToArray()), "invalid clock and malformed JSON cannot overwrite a valid update");

Check(await Send(first, JsonSerializer.SerializeToUtf8Bytes(new { type = "closed", tabId = 7, sequence = 7 })) &&
      bridge.Current?.VideoId == "bbbbbbbbbbb", "closing one tab leaves an identically numbered tab in another browser intact");
Check(!await Send(first, Playback(6, "aaaaaaaaaaa", 1)) && bridge.Current?.VideoId == "bbbbbbbbbbb",
    "closed tab rejects an older queued playback state");

second.Dispose();
await WaitFor(() => bridge.Current is null);
Check(bridge.Current is null, "disconnected browser removes its playback state");

await using var malformed = await Connect();
await malformed.WriteAsync(new byte[] { 0, 0, 1, 0 });
using (var sizeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    Check(await malformed.ReadAsync(new byte[1], sizeTimeout.Token) == 0, "oversized messages close their pipe without affecting other clients");
}
Check(await Send(first, Playback(8, "ccccccccccc", 6)) && bridge.Current?.VideoId == "ccccccccccc",
    "other clients still work after oversize rejection");

await using var browserPackets = new MemoryStream();
await WriteFrame(browserPackets, Playback(1, "ddddddddddd", 8));
await WriteFrame(browserPackets, Playback(2, "bad", 8));
browserPackets.Position = 0;
await using var hostReplies = new MemoryStream();
await YouTubeBridge.RelayAsync(browserPackets, hostReplies, pipeName);
hostReplies.Position = 0;
Check(await ReadAccepted(hostReplies) && !await ReadAccepted(hostReplies), "native stdio relay returns app acknowledgements");

browserPackets.Position = 0;
hostReplies.SetLength(0);
await YouTubeBridge.RelayAsync(browserPackets, hostReplies, pipeName + ".missing");
hostReplies.Position = 0;
Check(!await ReadAccepted(hostReplies), "native stdio reports unavailable desktop without launching it");

async Task<NamedPipeClientStream> Connect()
{
    var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await client.ConnectAsync(3000);
    return client;
}

static byte[] Playback(long sequence, string videoId, double position, bool playing = true) =>
    JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "playback", tabId = 7, sequence, videoId, title = "أغنية تجريبية", artist = "", album = "",
        position, duration = 120, rate = 1, playing
    });

static async Task<bool> Send(Stream pipe, byte[] packet)
{
    await WriteFrame(pipe, packet);
    return await ReadAccepted(pipe);
}

static async Task WriteFrame(Stream stream, byte[] packet)
{
    var size = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(size, packet.Length);
    await stream.WriteAsync(size);
    await stream.WriteAsync(packet);
}

static async Task<bool> ReadAccepted(Stream stream)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var size = new byte[4];
    await stream.ReadExactlyAsync(size, timeout.Token);
    var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(size)];
    await stream.ReadExactlyAsync(bytes, timeout.Token);
    using var reply = JsonDocument.Parse(bytes);
    return reply.RootElement.GetProperty("ok").GetBoolean();
}

static async Task WaitFor(Func<bool> condition)
{
    for (int i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
}

static void Check(bool succeeded, string description)
{
    if (!succeeded) throw new InvalidOperationException(description);
    Console.WriteLine($"PASS {description}");
}
