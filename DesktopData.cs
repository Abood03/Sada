using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sada;

public sealed record SystemReading(
    double? CpuPercent, double? GpuPercent, double? MemoryPercent, long MemoryUsed, long MemoryTotal,
    long DiskFree, long DiskTotal, string DiskName, double DownloadBytesPerSecond, double UploadBytesPerSecond);

public sealed record ListeningReading(TimeSpan Today, TimeSpan Total, int TracksToday, int UniqueTracks, string TopArtist);

public sealed record CalendarReading(int Year, int Month, int Today, int StartingWeekday, int DaysInMonth, string MonthName);

public sealed record WeatherReading(string Place, string Summary, double Temperature, double FeelsLike,
    double WindSpeed, int WeatherCode, DateTimeOffset UpdatedAt);

public sealed class DesktopData : IDisposable
{
    private static readonly HttpClient WeatherClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 1024 * 1024
    };

    private readonly string historyPath;
    private readonly ListeningHistory history;
    private readonly string diskRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
    private readonly GpuMeter gpuMeter = new();
    private Dictionary<string, (long Read, long Written)> networkTotals = new(StringComparer.Ordinal);
    private (ulong Total, ulong Idle)? lastCpuTimes;
    private (long Free, long Total) disk;
    private long lastSample;
    private long lastDiskRefresh;
    private long lastHistorySave = Stopwatch.GetTimestamp();
    private string lastPlayingTrack = "";
    private bool historyDirty;
    private bool disposed;
    private int weatherRequest;
    private WeatherReading? weather;

    public DesktopData(string dataFolder)
    {
        historyPath = Path.Combine(dataFolder, "listening-history.json");
        history = ReadHistory(historyPath);
        UpdateListening(DateTime.Today);
    }

    public SystemReading System { get; private set; } = new(null, null, null, 0, 0, 0, 0, "", 0, 0);
    public ListeningReading Listening { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, "");
    public WeatherReading? Weather => Volatile.Read(ref weather);

    public CalendarReading Calendar
    {
        get
        {
            DateTime today = DateTime.Today;
            DateTime first = new(today.Year, today.Month, 1);
            int weekday = ((int)first.DayOfWeek + 6) % 7;
            return new CalendarReading(today.Year, today.Month, today.Day, weekday,
                DateTime.DaysInMonth(today.Year, today.Month), today.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        }
    }

    public void Sample(MediaSnapshot snapshot)
    {
        if (disposed)
            return;

        long timestamp = Stopwatch.GetTimestamp();
        double elapsed = lastSample == 0 ? 0 : Stopwatch.GetElapsedTime(lastSample, timestamp).TotalSeconds;
        lastSample = timestamp;

        SampleListening(snapshot, Math.Clamp(elapsed, 0, 3));
        if (lastDiskRefresh == 0 || Stopwatch.GetElapsedTime(lastDiskRefresh, timestamp).TotalSeconds >= 30)
        {
            disk = ReadDisk(diskRoot);
            lastDiskRefresh = timestamp;
        }

        (double? percent, long used, long total) = ReadMemory();
        (double received, double sent) = ReadNetwork(elapsed);
        System = new SystemReading(ReadCpu(), gpuMeter.Read(), percent, used, total, disk.Free, disk.Total,
            diskRoot.TrimEnd(Path.DirectorySeparatorChar), received, sent);

        if (historyDirty && Stopwatch.GetElapsedTime(lastHistorySave, timestamp).TotalSeconds >= 30)
        {
            SaveHistory();
            lastHistorySave = timestamp;
        }
    }

    public async Task<WeatherReading?> SearchWeatherAsync(string city, CancellationToken cancellationToken = default)
    {
        int request = Interlocked.Increment(ref weatherRequest);
        string[] parts = city.Trim().Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length < 2)
            return null;

        string geocodeUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" +
            Uri.EscapeDataString(parts[0]);
        if (parts.Length == 2 && parts[1].Length == 2 && parts[1].All(char.IsAsciiLetter))
            geocodeUrl += "&countryCode=" + parts[1].ToUpperInvariant();

        using JsonDocument locations = await RequestJsonAsync(geocodeUrl, cancellationToken).ConfigureAwait(false);
        if (!locations.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
            return null;

        JsonElement place = results[0];
        string name = place.GetProperty("name").GetString() ?? parts[0];
        if (place.TryGetProperty("country", out JsonElement country) && country.GetString() is { Length: > 0 } countryName)
            name += ", " + countryName;

        double latitude = place.GetProperty("latitude").GetDouble();
        double longitude = place.GetProperty("longitude").GetDouble();
        string forecastUrl = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m&timeformat=unixtime&forecast_days=1");
        using JsonDocument forecast = await RequestJsonAsync(forecastUrl, cancellationToken).ConfigureAwait(false);
        JsonElement current = forecast.RootElement.GetProperty("current");
        int code = current.GetProperty("weather_code").GetInt32();
        var reading = new WeatherReading(name, Condition(code), current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(), current.GetProperty("wind_speed_10m").GetDouble(),
            code, DateTimeOffset.FromUnixTimeSeconds(current.GetProperty("time").GetInt64()));

        if (request == Volatile.Read(ref weatherRequest))
            Volatile.Write(ref weather, reading);
        return reading;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        SaveHistory();
        gpuMeter.Dispose();
    }

    private void SampleListening(MediaSnapshot snapshot, double elapsed)
    {
        DateTime today = DateTime.Today;
        if (snapshot.HasTrack && snapshot.IsPlaying)
        {
            string artist = snapshot.Artist.Trim();
            string key = (artist + "\0" + snapshot.Title.Trim()).ToUpperInvariant();
            string date = DateKey(today);
            if (!history.Days.TryGetValue(date, out ListeningDay? day))
            {
                day = new ListeningDay();
                history.Days[date] = day;
                foreach (string past in history.Days.Keys.Where(value =>
                    string.CompareOrdinal(value, DateKey(today.AddDays(-90))) < 0).ToArray())
                {
                    history.Days.Remove(past);
                }
            }

            bool newLifetimeTrack = history.Tracks.Add(key);
            bool newDailyTrack = day.Tracks.Add(key);
            if (newLifetimeTrack || newDailyTrack)
                historyDirty = true;

            if (key == lastPlayingTrack && elapsed > 0)
            {
                history.TotalSeconds += elapsed;
                day.Seconds += elapsed;
                if (artist.Length > 0)
                    history.ArtistSeconds[artist] = history.ArtistSeconds.GetValueOrDefault(artist) + elapsed;
                historyDirty = true;
            }
            lastPlayingTrack = key;
        }
        else
        {
            lastPlayingTrack = "";
        }

        UpdateListening(today);
    }

    private void UpdateListening(DateTime today)
    {
        history.Days.TryGetValue(DateKey(today), out ListeningDay? day);
        string topArtist = history.ArtistSeconds.Count == 0 ? "" : history.ArtistSeconds.MaxBy(item => item.Value).Key;
        Listening = new ListeningReading(TimeSpan.FromSeconds(day?.Seconds ?? 0),
            TimeSpan.FromSeconds(history.TotalSeconds), day?.Tracks.Count ?? 0, history.Tracks.Count, topArtist);
    }

    private static string DateKey(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ListeningHistory ReadHistory(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ListeningHistory();

            ListeningHistory? loaded = JsonSerializer.Deserialize<ListeningHistory>(File.ReadAllText(path));
            if (loaded is null || loaded.Tracks is null || loaded.Days is null || loaded.ArtistSeconds is null ||
                loaded.TotalSeconds is < 0 or > 100_000_000_000 ||
                loaded.Days.Values.Any(day => day?.Tracks is null || day.Seconds is < 0 or > 100_000_000_000))
            {
                return new ListeningHistory();
            }

            loaded.ArtistSeconds = new Dictionary<string, double>(loaded.ArtistSeconds, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new ListeningHistory();
        }
    }

    private void SaveHistory()
    {
        if (!historyDirty)
            return;

        string temporaryPath = historyPath + ".tmp";
        try
        {
            if (Path.GetDirectoryName(historyPath) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(history));
            File.Move(temporaryPath, historyPath, overwrite: true);
            historyDirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory data so a later save can retry.
        }
    }

    private (double Download, double Upload) ReadNetwork(double elapsed)
    {
        var latest = new Dictionary<string, (long Read, long Written)>(StringComparer.Ordinal);
        long received = 0;
        long sent = 0;

        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            networkTotals.Clear();
            return (0, 0);
        }

        foreach (NetworkInterface adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                IPInterfaceStatistics counters = adapter.GetIPStatistics();
                latest[adapter.Id] = (counters.BytesReceived, counters.BytesSent);
                if (networkTotals.TryGetValue(adapter.Id, out var previous))
                {
                    received += Math.Max(0, counters.BytesReceived - previous.Read);
                    sent += Math.Max(0, counters.BytesSent - previous.Written);
                }
            }
            catch (NetworkInformationException)
            {
                // Ignore an interface that has disappeared between enumeration and reading.
            }
        }

        networkTotals = latest;
        return elapsed > 0 ? (received / elapsed, sent / elapsed) : (0, 0);
    }

    private double? ReadCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            return null;

        var measured = (Total: kernel + user, Idle: idle);
        var previous = lastCpuTimes;
        lastCpuTimes = measured;
        if (previous is null || measured.Total <= previous.Value.Total || measured.Idle < previous.Value.Idle)
            return null;

        double total = measured.Total - previous.Value.Total;
        double idleDelta = measured.Idle - previous.Value.Idle;
        return Math.Clamp(100 * (total - idleDelta) / total, 0, 100);
    }

    private static (double? Percent, long Used, long Total) ReadMemory()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref memory)
            ? (memory.LoadPercent, (long)(memory.TotalPhysical - memory.AvailablePhysical), (long)memory.TotalPhysical)
            : (null, 0, 0);
    }

    private static (long Free, long Total) ReadDisk(string drive)
    {
        if (drive.Length == 0)
            return (0, 0);

        try
        {
            var volume = new DriveInfo(drive);
            return volume.IsReady ? (volume.AvailableFreeSpace, volume.TotalSize) : (0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static async Task<JsonDocument> RequestJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await WeatherClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string Condition(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Conditions unavailable"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint LoadPercent;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    private sealed class ListeningDay
    {
        public double Seconds { get; set; }
        public HashSet<string> Tracks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ListeningHistory
    {
        public double TotalSeconds { get; set; }
        public HashSet<string> Tracks { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ListeningDay> Days { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ArtistSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GpuMeter : IDisposable
    {
        private const uint MoreData = 0x800007D2;
        private const uint DoubleFormat = 0x00000200;
        private IntPtr query;
        private IntPtr counter;

        public GpuMeter()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0)
                return;
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
                Dispose();
        }

        public double? Read()
        {
            if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0)
                return null;

            uint bytes = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, DoubleFormat, ref bytes, out _, IntPtr.Zero);
            if (status != MoreData || bytes is 0 or > 4 * 1024 * 1024)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, DoubleFormat, ref bytes, out uint count, buffer) != 0)
                    return null;

                var engineTotals = new Dictionary<string, double>(StringComparer.Ordinal);
                int itemSize = Marshal.SizeOf<CounterItem>();
                for (int index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<CounterItem>(buffer + itemSize * index);
                    if (entry.Status > 1 || !double.IsFinite(entry.Value) || entry.Value < 0)
                        continue;
                    string name = Marshal.PtrToStringUni(entry.Name) ?? "";
                    int hardwareId = name.IndexOf("_luid_", StringComparison.Ordinal);
                    string engine = hardwareId >= 0 ? name[hardwareId..] : name;
                    engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + entry.Value;
                }

                return engineTotals.Count == 0 ? null : Math.Clamp(engineTotals.Values.Max(), 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            if (query == IntPtr.Zero)
                return;
            PdhCloseQuery(query);
            query = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterItem
        {
            public IntPtr Name;
            public uint Status;
            public double Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes,
            out uint count, IntPtr buffer);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
