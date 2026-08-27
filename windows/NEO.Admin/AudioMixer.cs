using System.Buffers;
using Concentus;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NeoAdmin;

internal sealed class AudioMixer : IDisposable
{
    private const int OutputSampleRate = 48000;
    private const int Channels = 1;
    private const int MaxOpusFrameSamples = 5760;

    private readonly object _sync = new();
    private readonly MixingSampleProvider _mixer;
    private readonly VolumeSampleProvider _masterVolume;
    private readonly RecordingWaveProvider _recordingProvider;
    private readonly WaveOutEvent _output;
    private readonly Dictionary<ulong, PlayerAudioStream> _players = new();

    public event Action<ulong, string>? DecodeError;
    public event Action<string>? RecordingError;

    public AudioMixer(float initialVolume)
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;

        _mixer = new MixingSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, Channels))
        {
            ReadFully = true,
        };

        _masterVolume = new VolumeSampleProvider(_mixer)
        {
            Volume = Math.Clamp(initialVolume, 0f, 1f),
        };

        // Convert the final mixed signal to standard 16-bit PCM before playback
        // and recording. The WAV file therefore contains exactly the audio heard
        // after player mutes and master-volume adjustment.
        var pcmOutput = new SampleToWaveProvider16(_masterVolume);
        _recordingProvider = new RecordingWaveProvider(pcmOutput);
        _recordingProvider.RecordingError += message =>
            RecordingError?.Invoke(message);

        _output = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3,
            Volume = 1f,
        };
        _output.Init(_recordingProvider);
        _output.Play();
    }

    public float MasterVolume
    {
        get => _masterVolume.Volume;
        set => _masterVolume.Volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsRecording => _recordingProvider.IsRecording;

    public string? RecordingPath => _recordingProvider.RecordingPath;

    public void StartRecording(string path)
    {
        _recordingProvider.StartRecording(path);
    }

    public string? StopRecording()
    {
        return _recordingProvider.StopRecording();
    }

    public void SetPlayerMuted(ulong steamId, bool muted)
    {
        lock (_sync)
        {
            if (_players.TryGetValue(steamId, out PlayerAudioStream? stream))
                stream.Volume.Volume = muted ? 0f : 1f;
        }
    }

    public void HandlePacket(VoicePacket packet)
    {
        lock (_sync)
        {
            PlayerAudioStream stream = GetOrCreatePlayer(packet.SteamId);
            if (stream.HasSequence && !IsNewerSequence(packet.Sequence, stream.LastSequence))
                return;

            stream.LastSequence = packet.Sequence;
            stream.HasSequence = true;

            try
            {
                switch (packet.AudioFormat)
                {
                    case VoiceAudioFormat.Opus:
                        DecodeOpus(stream, packet);
                        break;
                    case VoiceAudioFormat.Pcm16Test:
                        AddPcm16(stream, packet.Payload);
                        break;
                    case VoiceAudioFormat.Steam:
                    case VoiceAudioFormat.Engine:
                        DecodeError?.Invoke(
                            packet.SteamId,
                            $"Format {packet.AudioFormat} is visible but not decodable by this prototype.");
                        break;
                    default:
                        DecodeError?.Invoke(
                            packet.SteamId,
                            $"Unknown audio format {(byte)packet.AudioFormat}.");
                        break;
                }
            }
            catch (Exception exception)
            {
                DecodeError?.Invoke(packet.SteamId, exception.Message);
            }
        }
    }

    private PlayerAudioStream GetOrCreatePlayer(ulong steamId)
    {
        if (_players.TryGetValue(steamId, out PlayerAudioStream? existing))
            return existing;

        var buffer = new BufferedWaveProvider(
            new WaveFormat(OutputSampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        var volume = new VolumeSampleProvider(buffer.ToSampleProvider())
        {
            Volume = 1f,
        };
        _mixer.AddMixerInput(volume);

        var stream = new PlayerAudioStream(
            buffer,
            volume,
            OpusCodecFactory.CreateDecoder(OutputSampleRate, Channels));

        _players.Add(steamId, stream);
        return stream;
    }

    private static bool IsNewerSequence(uint candidate, uint previous)
    {
        return unchecked((int)(candidate - previous)) > 0;
    }

    private static void AddPcm16(PlayerAudioStream stream, byte[] bytes)
    {
        int evenLength = bytes.Length & ~1;
        if (evenLength > 0)
            stream.Buffer.AddSamples(bytes, 0, evenLength);
    }

    private static void DecodeOpus(PlayerAudioStream stream, VoicePacket packet)
    {
        foreach ((int offset, int length) in SplitOpusPackets(packet))
        {
            if (length <= 0)
                continue;

            short[] pcm = ArrayPool<short>.Shared.Rent(MaxOpusFrameSamples);
            byte[] pcmBytes = ArrayPool<byte>.Shared.Rent(
                MaxOpusFrameSamples * sizeof(short));

            try
            {
                int samplesDecoded = stream.Decoder.Decode(
                    packet.Payload.AsSpan(offset, length),
                    pcm.AsSpan(0, MaxOpusFrameSamples),
                    MaxOpusFrameSamples,
                    decode_fec: false);

                int byteCount = samplesDecoded * sizeof(short);
                Buffer.BlockCopy(pcm, 0, pcmBytes, 0, byteCount);
                stream.Buffer.AddSamples(pcmBytes, 0, byteCount);
            }
            finally
            {
                ArrayPool<short>.Shared.Return(pcm);
                ArrayPool<byte>.Shared.Return(pcmBytes);
            }
        }
    }

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

                if (start >= 0 && end >= start && end <= payloadLength)
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

        ulong sum = 0;
        foreach (uint value in offsets)
            sum += value;

        if (sum == (ulong)payloadLength)
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
        lock (_sync)
        {
            _output.Stop();
            _recordingProvider.StopRecording();
            _output.Dispose();
            _recordingProvider.Dispose();

            foreach (PlayerAudioStream stream in _players.Values)
                stream.Dispose();

            _players.Clear();
        }
    }

    private sealed class PlayerAudioStream : IDisposable
    {
        public PlayerAudioStream(
            BufferedWaveProvider buffer,
            VolumeSampleProvider volume,
            IOpusDecoder decoder)
        {
            Buffer = buffer;
            Volume = volume;
            Decoder = decoder;
        }

        public BufferedWaveProvider Buffer { get; }
        public VolumeSampleProvider Volume { get; }
        public IOpusDecoder Decoder { get; }
        public uint LastSequence { get; set; }
        public bool HasSequence { get; set; }

        public void Dispose()
        {
            Decoder.Dispose();
        }
    }

    private sealed class RecordingWaveProvider : IWaveProvider, IDisposable
    {
        private readonly object _recordingSync = new();
        private readonly IWaveProvider _source;
        private WaveFileWriter? _writer;
        private string? _recordingPath;
        private bool _disposed;

        public RecordingWaveProvider(IWaveProvider source)
        {
            _source = source;
        }

        public event Action<string>? RecordingError;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public bool IsRecording
        {
            get
            {
                lock (_recordingSync)
                    return _writer is not null;
            }
        }

        public string? RecordingPath
        {
            get
            {
                lock (_recordingSync)
                    return _recordingPath;
            }
        }

        public void StartRecording(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "A recording file path is required.",
                    nameof(path));

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            lock (_recordingSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_writer is not null)
                    throw new InvalidOperationException(
                        "A recording is already in progress.");

                _writer = new WaveFileWriter(fullPath, WaveFormat);
                _recordingPath = fullPath;
            }
        }

        public string? StopRecording()
        {
            lock (_recordingSync)
            {
                string? completedPath = _recordingPath;
                StopRecordingLocked();
                return completedPath;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _source.Read(buffer, offset, count);
            if (bytesRead <= 0)
                return bytesRead;

            string? error = null;

            lock (_recordingSync)
            {
                if (_writer is not null)
                {
                    try
                    {
                        _writer.Write(buffer, offset, bytesRead);
                    }
                    catch (Exception exception)
                    {
                        error = exception.Message;
                        StopRecordingLocked();
                    }
                }
            }

            if (error is not null)
                RecordingError?.Invoke(error);

            return bytesRead;
        }

        private void StopRecordingLocked()
        {
            WaveFileWriter? writer = _writer;
            _writer = null;
            _recordingPath = null;

            if (writer is not null)
            {
                try
                {
                    writer.Dispose();
                }
                catch
                {
                    // The writer is already being stopped because of an I/O
                    // problem. Do not allow a second disposal error to crash
                    // the playback thread.
                }
            }
        }

        public void Dispose()
        {
            lock (_recordingSync)
            {
                if (_disposed)
                    return;

                StopRecordingLocked();
                _disposed = true;
            }
        }
    }
}
