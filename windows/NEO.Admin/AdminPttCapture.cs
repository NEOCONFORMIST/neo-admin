using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace NeoAdmin;

// NEO ADMIN PTT v1 - 48 kHz mono microphone capture and 20 ms Opus encoder.
internal sealed class AdminPttCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameSamples = 960; // 20 ms at 48 kHz.

    private const int FrameBytes = FrameSamples * sizeof(short);
    private const int MaxOpusPacketBytes = 1275;

    private readonly object _sync = new();
    private byte[] _pending = new byte[FrameBytes * 4];
    private int _pendingCount;
    private WaveInEvent? _waveIn;
    private IOpusEncoder? _encoder;
    private int _sequenceBytes;
    private uint _sectionNumber;
    private uint _uncompressedSampleOffset;
    private bool _running;
    private bool _disposed;
    private int _deviceNumber = GetPreferredMicrophoneDevice();

    public event Action<byte[], int, uint, uint, float>? OpusFrameReady;
    public event Action<string>? CaptureError;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _running;
        }
    }

    private static int GetPreferredMicrophoneDevice()
    {
        const string preferredName = "Seiren V3";

        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            WaveInCapabilities caps = WaveIn.GetCapabilities(i);

            if (caps.ProductName.Contains(
                preferredName,
                StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // Fallback only if the preferred microphone cannot be found.
        return 0;
    }
    public static string[] GetMicrophoneNames()
    {
        string[] names = new string[WaveIn.DeviceCount];

        for (int i = 0; i < names.Length; i++)
        {
            names[i] =
                WaveIn.GetCapabilities(i).ProductName;
        }

        return names;
    }

    public int DeviceNumber
    {
        get
        {
            lock (_sync)
                return _deviceNumber;
        }

        set
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);

                if (_running)
                {
                    throw new InvalidOperationException(
                        "The microphone cannot be changed while push-to-talk is active.");
                }

                if (value < 0 ||
                    value >= WaveIn.DeviceCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        "The selected microphone is not available.");
                }

                _deviceNumber = value;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
                return;

            if (WaveIn.DeviceCount <= 0)
                throw new InvalidOperationException("Windows did not report an available microphone.");

            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            _encoder = OpusCodecFactory.CreateEncoder(
                SampleRate,
                Channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 32000;
            _encoder.Complexity = 7;
            _encoder.UseVBR = true;

            _pendingCount = 0;
            _sequenceBytes = 0;
            _sectionNumber = unchecked((uint)Environment.TickCount);
            if (_sectionNumber == 0)
                _sectionNumber = 1;
            _uncompressedSampleOffset = 0;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = _deviceNumber,
                WaveFormat = new WaveFormat(SampleRate, 16, Channels),
                BufferMilliseconds = 20,
                NumberOfBuffers = 3,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _running = true;

            try
            {
                _waveIn.StartRecording();
            }
            catch
            {
                _running = false;
                DisposeCaptureLocked();
                throw;
            }
        }
    }

    public void Stop()
    {
        WaveInEvent? waveIn;

        lock (_sync)
        {
            if (!_running)
                return;

            _running = false;
            waveIn = _waveIn;
            if (waveIn is not null)
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.RecordingStopped -= OnRecordingStopped;
            }
        }

        if (waveIn is not null)
        {
            try
            {
                waveIn.StopRecording();
            }
            catch
            {
                // Device teardown should not keep the app open.
            }
        }

        lock (_sync)
            DisposeCaptureLocked();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        List<(byte[] Encoded, int SequenceBytes, uint SectionNumber, uint SampleOffset, float Level)> frames =
            new();

        lock (_sync)
        {
            if (!_running || _encoder is null || e.BytesRecorded <= 0)
                return;

            EnsurePendingCapacity(_pendingCount + e.BytesRecorded);
            Buffer.BlockCopy(
                e.Buffer,
                0,
                _pending,
                _pendingCount,
                e.BytesRecorded);
            _pendingCount += e.BytesRecorded;

            while (_pendingCount >= FrameBytes)
            {
                short[] pcm = new short[FrameSamples];
                Buffer.BlockCopy(_pending, 0, pcm, 0, FrameBytes);

                int remaining = _pendingCount - FrameBytes;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(
                        _pending,
                        FrameBytes,
                        _pending,
                        0,
                        remaining);
                }
                _pendingCount = remaining;

                byte[] encoded = new byte[MaxOpusPacketBytes];
                int encodedLength = _encoder.Encode(
                    pcm,
                    FrameSamples,
                    encoded,
                    encoded.Length);

                if (encodedLength <= 0)
                    continue;

                Array.Resize(ref encoded, encodedLength);

                int frameSequenceBytes = _sequenceBytes;
                uint frameSampleOffset = _uncompressedSampleOffset;
                _sequenceBytes = unchecked(_sequenceBytes + encodedLength);
                _uncompressedSampleOffset =
                    unchecked(_uncompressedSampleOffset + FrameSamples);

                frames.Add((
                    encoded,
                    frameSequenceBytes,
                    _sectionNumber,
                    frameSampleOffset,
                    CalculateVoiceLevel(pcm)));
            }
        }

        foreach (var frame in frames)
        {
            OpusFrameReady?.Invoke(
                frame.Encoded,
                frame.SequenceBytes,
                frame.SectionNumber,
                frame.SampleOffset,
                frame.Level);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool shouldReport = false;
        string? message = null;

        lock (_sync)
        {
            if (_running)
            {
                _running = false;
                shouldReport = e.Exception is not null;
                message = e.Exception?.Message;
            }
            DisposeCaptureLocked();
        }

        if (shouldReport)
        {
            CaptureError?.Invoke(
                $"Microphone capture stopped: {message ?? "unknown audio-device error"}");
        }
    }

    private void EnsurePendingCapacity(int needed)
    {
        if (_pending.Length >= needed)
            return;

        int next = _pending.Length;
        while (next < needed)
            next *= 2;

        Array.Resize(ref _pending, next);
    }

    private static float CalculateVoiceLevel(short[] pcm)
    {
        if (pcm.Length == 0)
            return 0f;

        double sumSquares = 0;
        foreach (short sample in pcm)
        {
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        return Math.Clamp(
            (float)Math.Sqrt(sumSquares / pcm.Length),
            0f,
            1f);
    }

    private void DisposeCaptureLocked()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _encoder?.Dispose();
        _encoder = null;
        _pendingCount = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();

        lock (_sync)
        {
            _disposed = true;
            DisposeCaptureLocked();
        }
    }
}
