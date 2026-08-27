using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NeoAdmin;

internal enum BridgeMessageType : byte
{
    Voice = 1,
    PlayerConnected = 2,
    PlayerDisconnected = 3,
    MapChanged = 4,
    PlayerPosition = 5,
    ChatEvent = 9,
    AdminActionResult = 12,
    MapCatalog = 13,
    ServerHealth = 14,
    AdminSession = 16,
    AdminAccountCatalog = 17,
    AdminAuditCatalog = 19,
    AdminBanCatalog = 20,
    DisciplineCatalog = 21,
    DisciplineHistory = 22,
    MapRotationCatalog = 23,
    AnnouncementCatalog = 24,
    GameAdminCatalog = 25,
    MapOverviewChunk = 26,
}

internal enum VoiceAudioFormat : byte
{
    Steam = 0,
    Engine = 1,
    Opus = 2,
    Pcm16Test = 10,
}

internal sealed record VoicePacket(
    byte Version,
    BridgeMessageType MessageType,
    VoiceAudioFormat AudioFormat,
    byte Flags,
    uint Sequence,
    uint Tick,
    ulong SteamId,
    int PlayerSlot,
    uint SampleRate,
    int SequenceBytes,
    uint SectionNumber,
    uint UncompressedSampleOffset,
    uint NumPackets,
    float VoiceLevel,
    string PlayerName,
    uint[] PacketOffsets,
    byte[] Payload)
{
    public const int HeaderSize = 60;
    public const int AuthTagSize = 32;
    private static ReadOnlySpan<byte> Magic => "CVB1"u8;

    public bool IsVoice => MessageType == BridgeMessageType.Voice;
    public string MapName => MessageType == BridgeMessageType.MapChanged
        ? PlayerName
        : string.Empty;

    public string ChatMessage => MessageType == BridgeMessageType.ChatEvent
        ? Encoding.UTF8.GetString(Payload)
        : string.Empty;

    // NEO ADMIN CONTROL Stage 3T server response.
    // The server echoes the originating request sequence in Tick,
    // the action code in SampleRate, and success in Flags bit 0.
    public bool AdminActionSucceeded =>
        MessageType == BridgeMessageType.AdminActionResult &&
        (Flags & 0x01) != 0;

    public uint AdminActionRequestSequence =>
        MessageType == BridgeMessageType.AdminActionResult
            ? Tick
            : 0;

    public uint AdminActionCode =>
        MessageType == BridgeMessageType.AdminActionResult
            ? SampleRate
            : 0;

    public string AdminActionMessage =>
        MessageType == BridgeMessageType.AdminActionResult
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;

    // Stage 3V filesystem map catalog. Tick contains the server-side
    // map count and Payload is newline-separated UTF-8 map tokens.
    public uint MapCatalogCount =>
        MessageType == BridgeMessageType.MapCatalog
            ? Tick
            : 0;

    public string MapCatalogText =>
        MessageType == BridgeMessageType.MapCatalog
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;

    // Server health reuses otherwise message-specific v1 header fields.
    // The receiver enriches authenticated responses with local RTT/loss.
    public uint HealthProbeSequence =>
        MessageType == BridgeMessageType.ServerHealth ? Tick : 0;
    public ulong MapUptimeSeconds =>
        MessageType == BridgeMessageType.ServerHealth ? SteamId : 0;
    public int ConnectedPlayers =>
        MessageType == BridgeMessageType.ServerHealth ? PlayerSlot : 0;
    public uint MaxPlayers =>
        MessageType == BridgeMessageType.ServerHealth ? SampleRate : 0;
    public float TickRate => BitConverter.Int32BitsToSingle(SequenceBytes);
    public float CpuUsagePercent => BitConverter.Int32BitsToSingle(
        unchecked((int)SectionNumber));
    public float MemoryUsagePercent => BitConverter.Int32BitsToSingle(
        unchecked((int)UncompressedSampleOffset));
    public uint ServerDroppedPackets =>
        MessageType == BridgeMessageType.ServerHealth ? NumPackets : 0;
    public string PluginVersion =>
        MessageType == BridgeMessageType.ServerHealth
            ? PlayerName
            : string.Empty;

    public bool AdminSessionAuthenticated =>
        MessageType == BridgeMessageType.AdminSession &&
        (Flags & 0x01) != 0;
    public AdminPermission AdminSessionPermissions =>
        MessageType == BridgeMessageType.AdminSession
            ? (AdminPermission)SteamId
            : AdminPermission.None;
    public string AdminSessionRole => Tick switch
    {
        1 => "Viewer",
        2 => "Moderator",
        3 => "Administrator",
        4 => "Owner",
        _ => "Custom",
    };
    public string AdminSessionMessage =>
        MessageType == BridgeMessageType.AdminSession
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;

    public string AdminAccountCatalogJson =>
        MessageType == BridgeMessageType.AdminAccountCatalog
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;
    public string GameAdminCatalogJson =>
        MessageType == BridgeMessageType.GameAdminCatalog
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;
    public string AdminAuditCatalogJson =>
        MessageType == BridgeMessageType.AdminAuditCatalog
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;
    public string AdminBanCatalogJson =>
        MessageType == BridgeMessageType.AdminBanCatalog
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;
    public string CatalogJson => MessageType is
        BridgeMessageType.DisciplineCatalog or
        BridgeMessageType.DisciplineHistory or
        BridgeMessageType.MapRotationCatalog or
        BridgeMessageType.AnnouncementCatalog
            ? Encoding.UTF8.GetString(Payload)
            : string.Empty;
    public string MapOverviewName =>
        MessageType == BridgeMessageType.MapOverviewChunk
            ? PlayerName
            : string.Empty;
    public int MapOverviewChunkIndex =>
        MessageType == BridgeMessageType.MapOverviewChunk
            ? PlayerSlot
            : -1;
    public uint MapOverviewChunkCount =>
        MessageType == BridgeMessageType.MapOverviewChunk
            ? SampleRate
            : 0;
    public ulong MapOverviewPackageLength =>
        MessageType == BridgeMessageType.MapOverviewChunk
            ? SteamId
            : 0;
    public uint MapOverviewPackageHash =>
        MessageType == BridgeMessageType.MapOverviewChunk
            ? unchecked((uint)SequenceBytes)
            : 0;
    public uint MapOverviewDefinitionLength =>
        MessageType == BridgeMessageType.MapOverviewChunk
            ? SectionNumber
            : 0;
    public double RoundTripMilliseconds { get; init; } = double.NaN;
    public double PacketLossPercent { get; init; } = double.NaN;

    // Position packets reuse otherwise-audio-only header fields. Keeping the
    // fixed v1 header makes the new feature compatible with the authenticated
    // transport already deployed on the server.
    public float PositionX => BitConverter.Int32BitsToSingle(
        unchecked((int)SampleRate));
    public float PositionY => BitConverter.Int32BitsToSingle(SequenceBytes);
    public float PositionZ => BitConverter.Int32BitsToSingle(
        unchecked((int)SectionNumber));
    public float ViewYaw => BitConverter.Int32BitsToSingle(
        unchecked((int)UncompressedSampleOffset));
    public int TeamNumber => unchecked((int)NumPackets);
    public int Health => Math.Max(0, (int)Math.Round(VoiceLevel));
    public bool IsAlive => (Flags & 0x01) != 0;
    public bool IsBot => (Flags & 0x02) != 0;

    public static bool TryParse(
        ReadOnlySpan<byte> datagram,
        ReadOnlySpan<byte> secret,
        out VoicePacket? packet,
        out string error)
    {
        packet = null;
        error = string.Empty;

        if (datagram.Length < HeaderSize + AuthTagSize)
        {
            error = "Datagram is too short.";
            return false;
        }

        int authenticatedLength = datagram.Length - AuthTagSize;
        using var hmac = new HMACSHA256(secret.ToArray());
        byte[] calculatedTag = hmac.ComputeHash(
            datagram[..authenticatedLength].ToArray());

        if (!CryptographicOperations.FixedTimeEquals(
                calculatedTag,
                datagram[authenticatedLength..]))
        {
            error = "Authentication failed.";
            return false;
        }

        ReadOnlySpan<byte> header = datagram[..HeaderSize];
        if (!header[..4].SequenceEqual(Magic))
        {
            error = "Wrong packet magic.";
            return false;
        }

        byte version = header[4];
        BridgeMessageType messageType =
            (BridgeMessageType)header[5];

        if (version != 1 ||
            messageType is not (
                BridgeMessageType.Voice or
                BridgeMessageType.PlayerConnected or
                BridgeMessageType.PlayerDisconnected or
                BridgeMessageType.MapChanged or
                BridgeMessageType.PlayerPosition or
                BridgeMessageType.ChatEvent or
                BridgeMessageType.AdminActionResult or
                BridgeMessageType.MapCatalog or
                BridgeMessageType.ServerHealth or
                BridgeMessageType.AdminSession or
                BridgeMessageType.AdminAccountCatalog or
                BridgeMessageType.AdminAuditCatalog or
                BridgeMessageType.AdminBanCatalog or
                BridgeMessageType.DisciplineCatalog or
                BridgeMessageType.DisciplineHistory or
                BridgeMessageType.MapRotationCatalog or
                BridgeMessageType.AnnouncementCatalog or
                BridgeMessageType.GameAdminCatalog or
                BridgeMessageType.MapOverviewChunk))
        {
            error =
                $"Unsupported protocol version/type: {version}/{(byte)messageType}.";
            return false;
        }

        VoiceAudioFormat format = (VoiceAudioFormat)header[6];
        byte flags = header[7];
        uint sequence =
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint tick =
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        ulong steamId =
            BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
        int playerSlot =
            BinaryPrimitives.ReadInt32LittleEndian(header[24..28]);
        uint sampleRate =
            BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
        int sequenceBytes =
            BinaryPrimitives.ReadInt32LittleEndian(header[32..36]);
        uint sectionNumber =
            BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]);
        uint uncompressedSampleOffset =
            BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
        uint numPackets =
            BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
        ushort nameLength =
            BinaryPrimitives.ReadUInt16LittleEndian(header[48..50]);
        ushort offsetCount =
            BinaryPrimitives.ReadUInt16LittleEndian(header[50..52]);
        uint payloadLength =
            BinaryPrimitives.ReadUInt32LittleEndian(header[52..56]);
        float voiceLevel = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(header[56..60]));

        long expectedLength =
            HeaderSize +
            nameLength +
            (long)offsetCount * sizeof(uint) +
            payloadLength +
            AuthTagSize;

        if (expectedLength != datagram.Length)
        {
            error =
                $"Length mismatch. Expected {expectedLength}, got {datagram.Length}.";
            return false;
        }

        if (messageType == BridgeMessageType.ChatEvent)
        {
            if (offsetCount != 0 ||
                payloadLength == 0 ||
                payloadLength > 512)
            {
                error = "A chat packet contains invalid payload metadata.";
                return false;
            }
        }
        else if (messageType == BridgeMessageType.AdminActionResult)
        {
            if (offsetCount != 0 ||
                payloadLength > 512)
            {
                error =
                    "An admin-action result contains invalid payload metadata.";
                return false;
            }
        }
        else if (messageType == BridgeMessageType.MapCatalog)
        {
            if (nameLength != 0 ||
                offsetCount != 0 ||
                payloadLength == 0 ||
                payloadLength > 60000)
            {
                error =
                    "A map-catalog packet contains invalid payload metadata.";
                return false;
            }
        }
        else if (messageType == BridgeMessageType.MapOverviewChunk)
        {
            if (nameLength == 0 ||
                nameLength > 96 ||
                offsetCount != 0 ||
                payloadLength == 0 ||
                payloadLength > 1200 ||
                playerSlot < 0 ||
                sampleRate == 0 ||
                sampleRate > 2048 ||
                (uint)playerSlot >= sampleRate ||
                steamId == 0 ||
                steamId > 2U * 1024U * 1024U ||
                sectionNumber == 0 ||
                sectionNumber > 64U * 1024U)
            {
                error =
                    "A map-overview chunk contains invalid payload metadata.";
                return false;
            }
        }
        else if (messageType == BridgeMessageType.ServerHealth)
        {
            if (nameLength > 128 ||
                offsetCount != 0 ||
                payloadLength != 0)
            {
                error =
                    "A server-health packet contains invalid payload metadata.";
                return false;
            }
        }
        else if (messageType == BridgeMessageType.AdminSession)
        {
            if (nameLength > 64 || offsetCount != 0 ||
                payloadLength is < 1 or > 512)
            {
                error = "An administrator-session packet is invalid.";
                return false;
            }
        }
        else if (messageType is BridgeMessageType.AdminAccountCatalog or
                 BridgeMessageType.AdminAuditCatalog or
                 BridgeMessageType.AdminBanCatalog or
                 BridgeMessageType.DisciplineCatalog or
                 BridgeMessageType.DisciplineHistory or
                 BridgeMessageType.MapRotationCatalog or
                 BridgeMessageType.AnnouncementCatalog or
                 BridgeMessageType.GameAdminCatalog)
        {
            if (nameLength != 0 || offsetCount != 0 ||
                payloadLength is < 1 or > 60000)
            {
                error = "An administrator security catalog is invalid.";
                return false;
            }
        }
        else if (messageType != BridgeMessageType.Voice &&
                 (offsetCount != 0 || payloadLength != 0))
        {
            error = "A non-voice packet contains unexpected audio data.";
            return false;
        }

        int cursor = HeaderSize;
        string playerName;
        try
        {
            playerName = Encoding.UTF8.GetString(
                datagram.Slice(cursor, nameLength));
        }
        catch (DecoderFallbackException)
        {
            error = "Player or map name is not valid UTF-8.";
            return false;
        }

        cursor += nameLength;

        uint[] offsets = new uint[offsetCount];
        for (int index = 0; index < offsets.Length; index++)
        {
            offsets[index] =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    datagram.Slice(cursor, sizeof(uint)));
            cursor += sizeof(uint);
        }

        byte[] payload = datagram
            .Slice(cursor, checked((int)payloadLength))
            .ToArray();

        if (messageType is
            BridgeMessageType.ChatEvent or
            BridgeMessageType.AdminActionResult or
            BridgeMessageType.MapCatalog or
            BridgeMessageType.AdminSession or
            BridgeMessageType.AdminAccountCatalog or
            BridgeMessageType.AdminAuditCatalog or
            BridgeMessageType.AdminBanCatalog or
            BridgeMessageType.DisciplineCatalog or
            BridgeMessageType.DisciplineHistory or
            BridgeMessageType.MapRotationCatalog or
            BridgeMessageType.AnnouncementCatalog or
            BridgeMessageType.GameAdminCatalog)
        {
            try
            {
                _ = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                    .GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                error =
                    "Text payload is not valid UTF-8.";
                return false;
            }
        }

        packet = new VoicePacket(
            version,
            messageType,
            format,
            flags,
            sequence,
            tick,
            steamId,
            playerSlot,
            sampleRate,
            sequenceBytes,
            sectionNumber,
            uncompressedSampleOffset,
            numPackets,
            voiceLevel,
            playerName,
            offsets,
            payload);

        return true;
    }
}
