using System.Buffers;
using System.Threading.Channels;
using Android.Media;
using Concentus;
using Concentus.Enums;
using AndroidAudioEncoding = Android.Media.Encoding;

namespace NeoAdmin.AndroidApp;

internal sealed class MobileVoicePlayback : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int MaxOpusFrameSamples = 5760;
    private const int MaxPlayerStreams = 16;

    private readonly object _streamSync = new();
    private readonly Dictionary<ulong, PlayerStream> _streams = new();
    private readonly Channel<QueuedVoicePacket> _packets;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private volatile bool _userMuted;
    private volatile bool _pttSuppressed;
    private string _lastReportedSpeaker = string.Empty;
    private DateTime _lastVoiceActivityUtc;
    private bool _disposed;
    private long _playbackEpoch;

    public MobileVoicePlayback()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _packets = Channel.CreateBounded<QueuedVoicePacket>(
            new BoundedChannelOptions(96)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        _worker = Task.Run(() => PlaybackLoopAsync(_lifetime.Token));
    }

    public event Action<string>? PlaybackError;
    public event Action<string>? VoiceActivity;

    public bool UserMuted => _userMuted;

    public void HandlePacket(VoicePacket packet)
    {
        if (_disposed || _userMuted || !packet.IsVoice)
            return;

        _packets.Writer.TryWrite(new QueuedVoicePacket(
            packet,
            Interlocked.Read(ref _playbackEpoch)));
    }

    public void SetUserMuted(bool muted)
    {
        _userMuted = muted;
        ApplyOutputVolume();
    }

    public void SetPttSuppressed(bool suppressed)
    {
        _pttSuppressed = suppressed;
        ApplyOutputVolume();
    }

    public void Reset()
    {
        Interlocked.Increment(ref _playbackEpoch);
        while (_packets.Reader.TryRead(out _))
        {
        }

        lock (_streamSync)
        {
            foreach (PlayerStream stream in _streams.Values)
                stream.Dispose();
            _streams.Clear();
        }

        _lastReportedSpeaker = string.Empty;
        _lastVoiceActivityUtc = default;
    }

    private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (QueuedVoicePacket queued in
                _packets.Reader.ReadAllAsync(cancellationToken))
            {
                if (queued.Epoch != Interlocked.Read(ref _playbackEpoch))
                    continue;

                if (_userMuted)
                    continue;

                try
                {
                    VoicePacket packet = queued.Packet;
                    int decodedSamples = DecodeAndPlay(packet, queued.Epoch);
                    if (decodedSamples > 0)
                        ReportVoiceActivity(packet.PlayerName);
                }
                catch (Exception exception)
                {
                    PlaybackError?.Invoke(
                        $"Player voice could not be played: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
    }

    private int DecodeAndPlay(VoicePacket packet, long expectedEpoch)
    {
        ulong playerKey = packet.SteamId != 0
            ? packet.SteamId
            : 0x8000000000000000UL | unchecked((uint)packet.PlayerSlot);

        lock (_streamSync)
        {
            if (expectedEpoch != Interlocked.Read(ref _playbackEpoch))
                return 0;

            PlayerStream stream = GetOrCreateStream(playerKey);
            if (stream.HasSequence &&
                !IsNewerSequence(packet.Sequence, stream.LastSequence))
            {
                return 0;
            }

            stream.LastSequence = packet.Sequence;
            stream.HasSequence = true;
            stream.LastUsedUtc = DateTime.UtcNow;

            int decodedSamples = 0;
            switch (packet.AudioFormat)
            {
                case VoiceAudioFormat.Opus:
                    foreach ((int offset, int length) in SplitOpusPackets(packet))
                    {
                        if (length > 0)
                        {
                            decodedSamples += DecodeOpusFrame(
                                stream,
                                packet.Payload,
                                offset,
                                length);
                        }
                    }
                    break;
                case VoiceAudioFormat.Pcm16Test:
                    decodedSamples = PlayPcm16(stream, packet.Payload);
                    break;
                case VoiceAudioFormat.Steam:
                case VoiceAudioFormat.Engine:
                    throw new NotSupportedException(
                        $"Voice format {packet.AudioFormat} is not decodable.");
                default:
                    throw new NotSupportedException(
                        $"Unknown voice format {(byte)packet.AudioFormat}.");
            }

            return decodedSamples;
        }
    }

    private void ReportVoiceActivity(string playerName)
    {
        string speaker = string.IsNullOrWhiteSpace(playerName)
            ? "player"
            : playerName.Trim();
        DateTime now = DateTime.UtcNow;
        if (string.Equals(speaker, _lastReportedSpeaker, StringComparison.Ordinal) &&
            now - _lastVoiceActivityUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastReportedSpeaker = speaker;
        _lastVoiceActivityUtc = now;
        VoiceActivity?.Invoke(speaker);
    }

    private PlayerStream GetOrCreateStream(ulong playerKey)
    {
        if (_streams.TryGetValue(playerKey, out PlayerStream? existing))
            return existing;

        if (_streams.Count >= MaxPlayerStreams)
        {
            KeyValuePair<ulong, PlayerStream> oldest = _streams
                .OrderBy(pair => pair.Value.LastUsedUtc)
                .First();
            _streams.Remove(oldest.Key);
            oldest.Value.Dispose();
        }

        int minimumBuffer = AudioTrack.GetMinBufferSize(
            SampleRate,
            ChannelOut.Mono,
            AndroidAudioEncoding.Pcm16bit);
        if (minimumBuffer <= 0)
            throw new InvalidOperationException("Android did not provide an audio output buffer.");

#pragma warning disable CA1422
        var output = new AudioTrack(
            Android.Media.Stream.Music,
            SampleRate,
            ChannelConfiguration.Mono,
            AndroidAudioEncoding.Pcm16bit,
            Math.Max(minimumBuffer, SampleRate * sizeof(short) / 5),
            AudioTrackMode.Stream);
#pragma warning restore CA1422
        if (output.State != AudioTrackState.Initialized)
        {
            output.Dispose();
            throw new InvalidOperationException("Android could not initialize player voice output.");
        }

        output.SetVolume(_userMuted || _pttSuppressed ? 0f : 1f);
        var created = new PlayerStream(
            output,
            OpusCodecFactory.CreateDecoder(SampleRate, Channels));
        _streams.Add(playerKey, created);
        return created;
    }

    private static int DecodeOpusFrame(
        PlayerStream stream,
        byte[] payload,
        int offset,
        int length)
    {
        short[] pcm = ArrayPool<short>.Shared.Rent(MaxOpusFrameSamples);
        try
        {
            int decoded = stream.Decoder.Decode(
                payload.AsSpan(offset, length),
                pcm.AsSpan(0, MaxOpusFrameSamples),
                MaxOpusFrameSamples,
                decode_fec: false);
            if (decoded > 0)
                stream.Write(pcm, decoded);
            return Math.Max(decoded, 0);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(pcm);
        }
    }

    private static int PlayPcm16(PlayerStream stream, byte[] payload)
    {
        int sampleCount = payload.Length / sizeof(short);
        if (sampleCount == 0)
            return 0;

        short[] pcm = ArrayPool<short>.Shared.Rent(sampleCount);
        try
        {
            Buffer.BlockCopy(payload, 0, pcm, 0, sampleCount * sizeof(short));
            stream.Write(pcm, sampleCount);
            return sampleCount;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(pcm);
        }
    }

    private void ApplyOutputVolume()
    {
        float volume = _userMuted || _pttSuppressed ? 0f : 1f;
        lock (_streamSync)
        {
            foreach (PlayerStream stream in _streams.Values)
                stream.Output.SetVolume(volume);
        }
    }

    private static bool IsNewerSequence(uint candidate, uint previous) =>
        unchecked((int)(candidate - previous)) > 0;

    private static IEnumerable<(int Offset, int Length)> SplitOpusPackets(
        VoicePacket packet)
    {
        int payloadLength = packet.Payload.Length;
        if (payloadLength == 0)
            yield break;

        int declaredPacketCount = checked((int)Math.Min(
            packet.NumPackets,
            (uint)packet.PacketOffsets.Length));
        uint[] offsets = declaredPacketCount == packet.PacketOffsets.Length
            ? packet.PacketOffsets
            : packet.PacketOffsets.Take(declaredPacketCount).ToArray();
        if (packet.NumPackets <= 1 || offsets.Length == 0)
        {
            yield return (0, payloadLength);
            yield break;
        }

        bool increasing = true;
        for (int index = 1; index < offsets.Length; index++)
            increasing &= offsets[index] >= offsets[index - 1];

        if (increasing && offsets[0] == 0 && offsets[^1] < payloadLength)
        {
            for (int index = 0; index < offsets.Length; index++)
            {
                int start = checked((int)offsets[index]);
                int end = index + 1 < offsets.Length
                    ? checked((int)offsets[index + 1])
                    : payloadLength;
                if (end >= start && end <= payloadLength)
                    yield return (start, end - start);
            }
            yield break;
        }

        if (increasing && offsets[^1] == payloadLength)
        {
            int start = 0;
            foreach (uint rawEnd in offsets)
            {
                int end = checked((int)rawEnd);
                if (end >= start && end <= payloadLength)
                    yield return (start, end - start);
                start = end;
            }
            yield break;
        }

        ulong total = 0;
        foreach (uint value in offsets)
            total += value;
        if (total == (ulong)payloadLength)
        {
            int start = 0;
            foreach (uint rawLength in offsets)
            {
                int length = checked((int)rawLength);
                yield return (start, length);
                start += length;
            }
            yield break;
        }

        yield return (0, payloadLength);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _packets.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancellation is expected during shutdown.
        }

        lock (_streamSync)
        {
            foreach (PlayerStream stream in _streams.Values)
                stream.Dispose();
            _streams.Clear();
        }
        _lifetime.Dispose();
    }

    private readonly record struct QueuedVoicePacket(
        VoicePacket Packet,
        long Epoch);

    private sealed class PlayerStream : IDisposable
    {
        private const int StartupBufferSamples = 960 * 3;
        private readonly List<short> _startupSamples =
            new(StartupBufferSamples * 2);

        public PlayerStream(AudioTrack output, IOpusDecoder decoder)
        {
            Output = output;
            Decoder = decoder;
        }

        public AudioTrack Output { get; }
        public IOpusDecoder Decoder { get; }
        public uint LastSequence { get; set; }
        public bool HasSequence { get; set; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public int BufferedSamples { get; private set; }
        public bool Started { get; private set; }

        public void Write(short[] pcm, int sampleCount)
        {
            if (Started)
            {
                WriteToOutput(pcm, 0, sampleCount);
                return;
            }

            for (int index = 0; index < sampleCount; index++)
                _startupSamples.Add(pcm[index]);
            BufferedSamples += sampleCount;
            if (BufferedSamples < StartupBufferSamples)
                return;

            // Some Android audio drivers silently discard stream writes made
            // before Play(). Keep the jitter buffer in memory, start the route,
            // and only then hand the buffered speech to AudioTrack.
            Output.Play();
            short[] startup = _startupSamples.ToArray();
            WriteToOutput(startup, 0, startup.Length);
            _startupSamples.Clear();
            Started = true;
        }

        private void WriteToOutput(short[] pcm, int offset, int sampleCount)
        {
            int written = Output.Write(pcm, offset, sampleCount);
            if (written < 0)
            {
                throw new IOException(
                    $"Android audio output failed with error {written}.");
            }
        }

        public void Dispose()
        {
            try
            {
                Output.Stop();
            }
            catch
            {
                // A route change can stop an Android audio track first.
            }
            Output.Release();
            Output.Dispose();
            Decoder.Dispose();
        }
    }
}

internal sealed class MobilePttCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;

    private const int Channels = 1;
    private const int MaxOpusPacketBytes = 1275;

    private readonly object _sync = new();
    private CaptureSession? _session;
    private float _microphoneGain = 1f;
    private bool _disposed;

    public event Action<byte[], int, uint, uint, float>? OpusFrameReady;
    public event Action<string>? CaptureError;

    public float MicrophoneGain
    {
        get => Volatile.Read(ref _microphoneGain);
        set => Volatile.Write(ref _microphoneGain, Math.Clamp(value, 0.5f, 3f));
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _session is not null;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                return;

            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
                SampleRate,
                Channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 32000;
            encoder.Complexity = 7;
            encoder.UseVBR = true;

            int minimumBuffer = AudioRecord.GetMinBufferSize(
                SampleRate,
                ChannelIn.Mono,
                AndroidAudioEncoding.Pcm16bit);
            if (minimumBuffer <= 0)
            {
                encoder.Dispose();
                throw new InvalidOperationException(
                    "Android did not provide a microphone input buffer.");
            }

            var recorder = new AudioRecord(
                AudioSource.VoiceCommunication,
                SampleRate,
                ChannelIn.Mono,
                AndroidAudioEncoding.Pcm16bit,
                Math.Max(minimumBuffer, FrameSamples * sizeof(short) * 4));
            if (recorder.State != State.Initialized)
            {
                recorder.Dispose();
                encoder.Dispose();
                throw new InvalidOperationException("Android could not initialize the microphone.");
            }

            var session = new CaptureSession(recorder, encoder);
            try
            {
                recorder.StartRecording();
                if (recorder.RecordingState != RecordState.Recording)
                    throw new InvalidOperationException("Android did not start microphone recording.");
                _session = session;
                session.Worker = Task.Run(() => CaptureLoop(session));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }

    public void Stop()
    {
        CaptureSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        if (session is null)
            return;

        session.Cancellation.Cancel();
        try
        {
            session.Recorder.Stop();
        }
        catch
        {
            // The audio route may already have stopped recording.
        }

        try
        {
            session.Worker?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancellation is expected when the PTT button is released.
        }
        session.Dispose();
    }

    private void CaptureLoop(CaptureSession session)
    {
        short[] readBuffer = new short[FrameSamples * 2];
        short[] pending = new short[FrameSamples * 4];
        int pendingCount = 0;

        try
        {
            while (!session.Cancellation.IsCancellationRequested)
            {
                int read = session.Recorder.Read(readBuffer, 0, readBuffer.Length);
                if (read <= 0)
                    throw new IOException($"Microphone read failed with Android audio error {read}.");
                if (session.Cancellation.IsCancellationRequested)
                    break;

                if (pendingCount + read > pending.Length)
                    Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingCount + read));
                Array.Copy(readBuffer, 0, pending, pendingCount, read);
                pendingCount += read;

                while (pendingCount >= FrameSamples)
                {
                    short[] frame = new short[FrameSamples];
                    Array.Copy(pending, 0, frame, 0, FrameSamples);
                    pendingCount -= FrameSamples;
                    if (pendingCount > 0)
                        Array.Copy(pending, FrameSamples, pending, 0, pendingCount);
                    EncodeAndPublish(session, frame);
                }
            }
        }
        catch (Exception exception) when (!session.Cancellation.IsCancellationRequested)
        {
            CaptureError?.Invoke($"Microphone capture stopped: {exception.Message}");
        }
        finally
        {
            bool ownsSession;
            lock (_sync)
            {
                ownsSession = ReferenceEquals(_session, session);
                if (ownsSession)
                    _session = null;
            }
            if (ownsSession)
                session.Dispose();
        }
    }

    private void EncodeAndPublish(CaptureSession session, short[] frame)
    {
        ApplyMicrophoneGain(frame, MicrophoneGain);

        byte[] encoded = new byte[MaxOpusPacketBytes];
        int encodedLength = session.Encoder.Encode(
            frame,
            FrameSamples,
            encoded,
            encoded.Length);
        if (encodedLength <= 0)
            return;

        Array.Resize(ref encoded, encodedLength);
        int sequenceBytes = session.SequenceBytes;
        uint sampleOffset = session.SampleOffset;
        session.SequenceBytes = unchecked(session.SequenceBytes + encodedLength);
        session.SampleOffset = unchecked(session.SampleOffset + FrameSamples);
        OpusFrameReady?.Invoke(
            encoded,
            sequenceBytes,
            session.SectionNumber,
            sampleOffset,
            CalculateVoiceLevel(frame));
    }

    private static void ApplyMicrophoneGain(short[] pcm, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.001f)
            return;

        for (int index = 0; index < pcm.Length; index++)
        {
            int amplified = (int)MathF.Round(pcm[index] * gain);
            pcm[index] = (short)Math.Clamp(
                amplified,
                short.MinValue,
                short.MaxValue);
        }
    }

    private static float CalculateVoiceLevel(short[] pcm)
    {
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

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        lock (_sync)
            _disposed = true;
    }

    private sealed class CaptureSession : IDisposable
    {
        private int _disposed;

        public CaptureSession(AudioRecord recorder, IOpusEncoder encoder)
        {
            Recorder = recorder;
            Encoder = encoder;
            SectionNumber = unchecked((uint)Environment.TickCount);
            if (SectionNumber == 0)
                SectionNumber = 1;
        }

        public AudioRecord Recorder { get; }
        public IOpusEncoder Encoder { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Worker { get; set; }
        public int SequenceBytes { get; set; }
        public uint SectionNumber { get; }
        public uint SampleOffset { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Cancellation.Cancel();
            Recorder.Release();
            Recorder.Dispose();
            Encoder.Dispose();
            Cancellation.Dispose();
        }
    }
}
