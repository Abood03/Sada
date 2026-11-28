using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Sada;

public sealed record AudioDevice(string? Id, string Name, bool IsMicrophone)
{
    public override string ToString() => Name;
}

public sealed record AudioFrame(float[] Left, float[] Right, float[] Spectrum, float Level, float Beat)
{
    internal long SignalTimestamp { get; init; }
}

public sealed class AudioEngine : IDisposable
{
    private const int FftOrder = 12;
    private const int FftSize = 1 << FftOrder;
    private const int WaveSize = 512;
    private const int BandCount = 80;
    private const float LowestFrequency = 25;
    private const float HighestFrequency = 18000;
    private const double FrameInterval = 1d / 45;
    private const double SilenceTimeout = 0.16;

    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly AudioDevice DefaultOutput = new(null, "Default Windows output", false);
    private static readonly AudioDevice DefaultMicrophone = new(null, "Default microphone", true);
    private static readonly AudioFrame EmptyFrame = new(new float[WaveSize], new float[WaveSize], new float[BandCount], 0, 0);

    private readonly object _controlLock = new();
    private readonly object _sampleLock = new();
    private readonly object _frameLock = new();
    private readonly float[] _leftRing = new float[FftSize];
    private readonly float[] _rightRing = new float[FftSize];
    private readonly float[] _leftSamples = new float[FftSize];
    private readonly float[] _rightSamples = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _transform = new Complex[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2 + 1];
    private readonly Timer _followDefaultTimer;
    private CaptureSession? _session;
    private AudioDevice? _selected;
    private int _ringPosition;
    // At FftSize the ring is entirely zero, including any trailing silence after playback.
    private int _framesSinceSignal = FftSize;
    private long _lastSamples;
    private long _lastSignalTimestamp;
    private long _sampleRevision;
    private int _sampleRate = 48000;
    private bool _disposed;
    private volatile string? _error;
    private volatile bool _isRunning;
    private volatile float _sensitivity = 1;

    private AudioFrame _frame = EmptyFrame;
    private long _frameTimestamp;
    private long _beatTimestamp;
    private long _frameSampleRevision = -1;
    private float _bassAverage;
    private float _previousBass;

    public AudioEngine()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);

        _followDefaultTimer = new Timer(_ => RefreshDefaultDevice());
    }

    public string? Error => _error;
    public bool IsRunning => _isRunning;

    public float Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = float.IsFinite(value) ? Math.Clamp(value, 0.05f, 5) : 1;
    }

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var devices = new List<AudioDevice> { DefaultOutput };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            AddDevices(devices, enumerator, DataFlow.Render);
            devices.Add(DefaultMicrophone);
            AddDevices(devices, enumerator, DataFlow.Capture);
        }
        catch (Exception exception)
        {
            _error = $"Couldn't list Windows audio devices. {exception.Message}";
        }

        return devices;
    }

    private static void AddDevices(List<AudioDevice> devices, MMDeviceEnumerator enumerator, DataFlow flow)
    {
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (endpoint)
                devices.Add(new AudioDevice(endpoint.ID, endpoint.FriendlyName, flow == DataFlow.Capture));
        }
    }

    public bool Start(AudioDevice? device = null)
    {
        lock (_controlLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _selected = device ?? DefaultOutput;
            bool started = StartCapture(_selected);
            _followDefaultTimer.Change(
                _selected.Id is null ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan,
                _selected.Id is null ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan);
            return started;
        }
    }

    public void Stop()
    {
        lock (_controlLock)
        {
            if (_disposed)
                return;

            _selected = null;
            _followDefaultTimer.Change(Timeout.Infinite, Timeout.Infinite);
            EndCapture();
        }
    }

    public AudioFrame GetFrame()
    {
        lock (_frameLock)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = _frameTimestamp == 0 ? FrameInterval : Stopwatch.GetElapsedTime(_frameTimestamp, now).TotalSeconds;
            if (elapsed < FrameInterval)
                return _frame;

            _frameTimestamp = now;
            int sampleRate;
            long revision;
            long sampleTimestamp;
            bool hasSamples;
            bool hasSignal;
            lock (_sampleLock)
            {
                sampleRate = _sampleRate;
                revision = _sampleRevision;
                sampleTimestamp = _lastSignalTimestamp;
                hasSamples = _lastSamples != 0 && Stopwatch.GetElapsedTime(_lastSamples, now).TotalSeconds <= SilenceTimeout;
                hasSignal = hasSamples && _framesSinceSignal < FftSize;
                if (hasSignal && revision != _frameSampleRevision)
                {
                    CopyRing(_leftRing, _leftSamples);
                    CopyRing(_rightRing, _rightSamples);
                }
            }

            float beat = _frame.Beat * (float)Math.Exp(-elapsed / 0.14);
            if (hasSamples && revision == _frameSampleRevision)
            {
                if (_frame.Beat != beat)
                    _frame = _frame with { Beat = beat };
                return _frame;
            }

            _frameSampleRevision = revision;
            // The waveform represents the last 1/36 second of samples.
            long waveformCenter = hasSamples ? sampleTimestamp - Stopwatch.Frequency / 72 : 0;
            if (!hasSignal)
            {
                _previousBass = 0;
                _bassAverage *= (float)Math.Exp(-elapsed / (hasSamples ? 0.68 : 0.6));
                _frame = waveformCenter == 0 && beat == 0 ? EmptyFrame
                    : EmptyFrame with { Beat = beat, SignalTimestamp = waveformCenter };
                return _frame;
            }

            _frame = Analyze(sampleRate, now, elapsed, beat, waveformCenter);
            return _frame;
        }
    }

    private void CopyRing(float[] source, float[] target)
    {
        int tailLength = FftSize - _ringPosition;
        Array.Copy(source, _ringPosition, target, 0, tailLength);
        Array.Copy(source, 0, target, tailLength, _ringPosition);
    }

    private AudioFrame Analyze(int sampleRate, long now, double elapsed, float beat, long waveformCenter)
    {
        for (int i = 0; i < FftSize; i++)
        {
            // Pack left and right into one transform. Its conjugate bin pair retains
            // both channels' energy even when their phases oppose each other.
            _transform[i].X = _leftSamples[i] * _window[i];
            _transform[i].Y = _rightSamples[i] * _window[i];
        }

        FastFourierTransform.FFT(true, FftOrder, _transform);
        for (int bin = 1; bin < _magnitudes.Length; bin++)
        {
            Complex positive = _transform[bin];
            Complex negative = _transform[FftSize - bin];
            float power = positive.X * positive.X + positive.Y * positive.Y
                        + negative.X * negative.X + negative.Y * negative.Y;
            _magnitudes[bin] = 2 * MathF.Sqrt(power);
        }

        float hzPerBin = (float)sampleRate / FftSize;
        float bass = FindBandPeak(33 / hzPerBin, 140 / hzPerBin);
        float gain = _sensitivity;
        float trigger = Math.Max(0.008f / gain, _bassAverage * 1.42f);
        bool pastHold = _beatTimestamp == 0 || Stopwatch.GetElapsedTime(_beatTimestamp, now).TotalSeconds > 0.19;
        if (pastHold && bass > trigger && bass - _previousBass > Math.Max(0.002f / gain, _bassAverage * 0.18f))
        {
            beat = 1;
            _beatTimestamp = now;
        }

        _previousBass = bass;
        _bassAverage += (bass - _bassAverage) * (float)(1 - Math.Exp(-elapsed / 0.68));

        var spectrum = new float[BandCount];
        float highFrequency = Math.Min(HighestFrequency, sampleRate * 0.48f);
        float ratio = MathF.Pow(highFrequency / LowestFrequency, 1f / BandCount);
        float lowerBin = LowestFrequency / hzPerBin;
        for (int band = 0; band < BandCount; band++)
        {
            float upperBin = lowerBin * ratio;
            float magnitude = FindBandPeak(lowerBin, upperBin) * gain;
            float decibels = 20 * MathF.Log10(magnitude + 0.000001f);
            spectrum[band] = MathF.Pow(Math.Clamp((decibels + 64) / 60, 0, 1), 1.35f);
            lowerBin = upperBin;
        }

        var left = new float[WaveSize];
        var right = new float[WaveSize];
        int waveSamples = Math.Clamp(sampleRate / 36, WaveSize, FftSize);
        float powerSum = 0;
        for (int i = FftSize - waveSamples; i < FftSize; i++)
            powerSum += _leftSamples[i] * _leftSamples[i] + _rightSamples[i] * _rightSamples[i];

        for (int i = 0; i < WaveSize; i++)
        {
            float position = FftSize - waveSamples + i * (waveSamples - 1f) / (WaveSize - 1);
            left[i] = Math.Clamp(Interpolate(_leftSamples, position) * gain, -1, 1);
            right[i] = Math.Clamp(Interpolate(_rightSamples, position) * gain, -1, 1);
        }

        float rms = MathF.Sqrt(powerSum / (2 * waveSamples));
        float level = MathF.Sqrt(Math.Clamp(rms * gain * 2.2f, 0, 1));
        return new AudioFrame(left, right, spectrum, level, beat) { SignalTimestamp = waveformCenter };
    }

    private float FindBandPeak(float lowerBin, float upperBin)
    {
        float peak = Interpolate(_magnitudes, MathF.Sqrt(lowerBin * upperBin));
        int maxBin = Math.Min((int)upperBin, _magnitudes.Length - 1);
        for (int bin = Math.Max(1, (int)MathF.Ceiling(lowerBin)); bin <= maxBin; bin++)
            peak = Math.Max(peak, _magnitudes[bin]);

        return peak;
    }

    private static float Interpolate(float[] values, float index)
    {
        index = Math.Clamp(index, 0, values.Length - 1);
        int left = (int)index;
        int right = Math.Min(left + 1, values.Length - 1);
        return values[left] + (values[right] - values[left]) * (index - left);
    }

    private void RefreshDefaultDevice()
    {
        lock (_controlLock)
        {
            if (_disposed || _selected is not { Id: null } device)
                return;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var endpoint = GetDefaultEndpoint(enumerator, device);
                if (!_isRunning || _session?.EndpointId != endpoint.ID)
                    StartCapture(device);
            }
            catch (Exception exception)
            {
                if (!_isRunning)
                    _error = $"Waiting for a default Windows audio device. {exception.Message}";
            }
        }
    }

    private static MMDevice GetDefaultEndpoint(MMDeviceEnumerator enumerator, AudioDevice device)
    {
        return enumerator.GetDefaultAudioEndpoint(device.IsMicrophone ? DataFlow.Capture : DataFlow.Render, Role.Multimedia);
    }

    private bool StartCapture(AudioDevice device)
    {
        EndCapture();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = device.Id is null ? GetDefaultEndpoint(enumerator, device) : enumerator.GetDevice(device.Id);
            CaptureSession session = CaptureSession.Create(endpoint, device.IsMicrophone);
            lock (_sampleLock)
            {
                _session = session;
                _sampleRate = session.SampleRate;
                _error = null;
                _isRunning = true;
            }

            session.Start(OnDataAvailable, OnRecordingStopped);
            return true;
        }
        catch (Exception exception)
        {
            EndCapture();
            _error = $"Couldn't start {device.Name}. {exception.Message}";
            return false;
        }
    }

    private void EndCapture()
    {
        CaptureSession? session;
        lock (_sampleLock)
        {
            session = _session;
            _session = null;
            _isRunning = false;
            ResetSamples();
        }

        // Release the sample lock before waiting for the final capture callback.
        session?.Dispose();
    }

    private void ResetSamples()
    {
        _ringPosition = 0;
        _framesSinceSignal = FftSize;
        _lastSamples = 0;
        _lastSignalTimestamp = 0;
        _sampleRevision++;
        Array.Clear(_leftRing);
        Array.Clear(_rightRing);
    }

    private void OnRecordingStopped(CaptureSession session, Exception? error)
    {
        lock (_sampleLock)
        {
            if (_session != session)
                return;

            _isRunning = false;
            _error = error?.Message ?? "Windows stopped the audio input.";
            ResetSamples();
        }
    }

    private void OnDataAvailable(CaptureSession session, byte[] bytes, int samples, bool silent, long packetTimestamp)
    {
        if (samples < 1)
            return;

        lock (_sampleLock)
        {
            if (_session != session)
                return;

            if (silent)
            {
                if (_framesSinceSignal < FftSize)
                {
                    int tail = Math.Min(samples, FftSize - _ringPosition);
                    int head = Math.Min(samples - tail, _ringPosition);
                    Array.Clear(_leftRing, _ringPosition, tail);
                    Array.Clear(_rightRing, _ringPosition, tail);
                    Array.Clear(_leftRing, 0, head);
                    Array.Clear(_rightRing, 0, head);
                }

                _ringPosition = (_ringPosition + samples) & (FftSize - 1);
                _framesSinceSignal += Math.Min(samples, FftSize - _framesSinceSignal);
            }
            else
            {
                int lastSignalIndex = -1;
                for (int i = 0; i < samples; i++)
                {
                    int offset = i * session.BytesPerFrame;
                    float left = session.ReadSample(bytes.AsSpan(offset));
                    float right = session.Channels == 1 ? left : session.ReadSample(bytes.AsSpan(offset + session.BytesPerSample));
                    _leftRing[_ringPosition] = left;
                    _rightRing[_ringPosition] = right;
                    _ringPosition = (_ringPosition + 1) & (FftSize - 1);
                    if (left != 0 || right != 0)
                        lastSignalIndex = i;
                }

                _framesSinceSignal = lastSignalIndex >= 0 ? Math.Min(samples - 1 - lastSignalIndex, FftSize)
                    : _framesSinceSignal + Math.Min(samples, FftSize - _framesSinceSignal);
            }

            _lastSamples = Stopwatch.GetTimestamp();
            _lastSignalTimestamp = packetTimestamp;
            _sampleRevision++;
        }
    }

    public void Dispose()
    {
        lock (_controlLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _followDefaultTimer.Dispose();
            _selected = null;
            EndCapture();
        }
    }

    private sealed class CaptureSession(
        MMDevice endpoint, AudioClient client, WaveFormat format, bool isFloat) : IDisposable
    {
        private readonly ManualResetEventSlim stop = new();
        private Thread? worker;
        private byte[] buffer = new byte[client.BufferSize * format.BlockAlign];

        public string EndpointId { get; } = endpoint.ID;
        public int SampleRate { get; } = format.SampleRate;
        public int Channels { get; } = format.Channels;
        public int BytesPerFrame { get; } = format.BlockAlign;
        public int BytesPerSample { get; } = format.BitsPerSample / 8;

        public static CaptureSession Create(MMDevice endpoint, bool microphone)
        {
            AudioClient? client = null;
            try
            {
                client = endpoint.AudioClient;
                WaveFormat format = client.MixFormat;
                WaveFormatExtensible? extensible = format as WaveFormatExtensible;
                bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || extensible?.SubFormat == FloatSubtype;
                bool isPcm = format.Encoding == WaveFormatEncoding.Pcm || extensible?.SubFormat == PcmSubtype;
                if (!(isFloat && format.BitsPerSample is 32 or 64 || isPcm && format.BitsPerSample is 8 or 16 or 24 or 32)
                    || format.Channels < 1 || format.BlockAlign < format.Channels * format.BitsPerSample / 8)
                {
                    throw new NotSupportedException($"Unsupported Windows audio format ({format.Encoding}, {format.BitsPerSample} bits).");
                }

                var flags = microphone ? AudioClientStreamFlags.None : AudioClientStreamFlags.Loopback;
                client.Initialize(AudioClientShareMode.Shared, flags, 400_000, 0, format, Guid.Empty);
                return new CaptureSession(endpoint, client, format, isFloat);
            }
            catch
            {
                client?.Dispose();
                endpoint.Dispose();
                throw;
            }
        }

        public void Start(Action<CaptureSession, byte[], int, bool, long> onData, Action<CaptureSession, Exception?> onStopped)
        {
            client.Start();
            var thread = new Thread(() => ReadPackets(onData, onStopped))
            {
                IsBackground = true,
                Name = "Sada audio capture"
            };
            thread.Start();
            worker = thread;
        }

        private void ReadPackets(Action<CaptureSession, byte[], int, bool, long> onData, Action<CaptureSession, Exception?> onStopped)
        {
            Exception? error = null;
            try
            {
                var reader = client.AudioCaptureClient;
                // Poll at the shared device period to handle both silence and endpoints
                // that do not signal loopback capture events.
                while (!stop.Wait(10))
                {
                    while (!stop.IsSet && reader.GetNextPacketSize() > 0)
                    {
                        var address = reader.GetBuffer(out int frames, out var flags, out _, out long qpcPosition);
                        long receivedAt = Stopwatch.GetTimestamp();
                        try
                        {
                            bool silent = (flags & AudioClientBufferFlags.Silent) != 0 || address == IntPtr.Zero;
                            if (!silent)
                            {
                                int size = checked(frames * BytesPerFrame);
                                if (size > buffer.Length)
                                    Array.Resize(ref buffer, size);

                                Marshal.Copy(address, buffer, 0, size);
                            }

                            // WASAPI publishes the first frame's counter in 100 ns units.
                            long packetEnd = receivedAt;
                            if (qpcPosition > 0 && !flags.HasFlag(AudioClientBufferFlags.TimestampError))
                            {
                                double elapsed = qpcPosition / (double)TimeSpan.TicksPerSecond + frames / (double)SampleRate;
                                long lastFrame = (long)(elapsed * Stopwatch.Frequency);
                                if (Math.Abs((double)receivedAt - lastFrame) < Stopwatch.Frequency / 2d)
                                    packetEnd = Math.Min(receivedAt, lastFrame);
                            }
                            onData(this, buffer, frames, silent, packetEnd);
                        }
                        finally
                        {
                            reader.ReleaseBuffer(frames);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                try
                {
                    client.Stop();
                }
                catch (Exception exception)
                {
                    // Unplugged audio devices may also reject Stop.
                    error ??= exception;
                }
                onStopped(this, error);
            }
        }

        public float ReadSample(ReadOnlySpan<byte> bytes)
        {
            float sample = isFloat
                ? BytesPerSample == 4 ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes)
                : ReadPcm(bytes, BytesPerSample);
            return float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
        }

        private static float ReadPcm(ReadOnlySpan<byte> bytes, int bytesPerSample)
        {
            return bytesPerSample switch
            {
                1 => (bytes[0] - 128) / 128f,
                2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                3 => (bytes[0] << 8 | bytes[1] << 16 | bytes[2] << 24) / 2147483648f,
                4 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
                _ => 0
            };
        }

        public void Dispose()
        {
            stop.Set();
            worker?.Join();
            try
            {
                client.Dispose();
            }
            finally
            {
                stop.Dispose();
                endpoint.Dispose();
            }
        }
    }
}
