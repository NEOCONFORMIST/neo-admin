using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NeoAdmin;

// Stage 3T uses a fixed whitelist of numeric action codes.
// The Windows app never sends raw console/RCON commands.
internal enum AdminActionCode : uint
{
    None = 0,
    Kick = 1,
    Slay = 2,
    Respawn = 3,
    MoveToT = 4,
    MoveToCT = 5,
    MoveToSpectator = 6,

    GiveItem = 23,

    // Stage 3U server map / round control.
    ChangeMap = 40,
    RestartRound = 41,
    RestartMatch = 42,
    EndWarmup = 43,
    PauseMatch = 44,
    UnpauseMatch = 45,
    SwapTeams = 46,
    AddBot = 47,
    RemoveBots = 48,

    // Stage 3V: ask the server to rescan game/csgo/maps
    // and return the authenticated type-13 map catalog.
    RequestMapCatalog = 49,

    // Request an authenticated type-14 health snapshot.
    RequestServerHealth = 50,
    RequestAdminAccounts = 100,
    SaveAdminAccount = 101,
    DeleteAdminAccount = 102,
    RequestAuditLog = 103,
    RequestGameAdmins = 104,
    SaveGameAdmin = 105,
    DeleteGameAdmin = 106,
    RequestBanCatalog = 110,
    SaveBan = 111,
    DeleteBan = 112,
    RequestDisciplineCatalog = 113,
    SaveRestriction = 114,
    DeleteRestriction = 115,
    RequestDisciplineHistory = 116,
    RequestMapRotation = 120,
    SaveMapRotation = 121,
    RunNextMap = 122,
    SaveScheduledMap = 123,
    DeleteScheduledMap = 124,
    RequestAnnouncements = 130,
    SendAnnouncementNow = 131,
    SaveAnnouncement = 132,
    DeleteAnnouncement = 133,

    RunServerConsole = 140,
    RequestZombieModeStatus = 141,
    SetZombieMode = 142,
    HostWorkshopMap = 143,
}

// NEO ADMIN authenticated Windows-to-server commands.
// Type 6 = drag teleport. Type 7 = targetless server-broadcast admin microphone.
internal static class BridgeCommandPacket
{
    private const int HeaderSize = 60;
    private const int AuthTagSize = 32;
    private const byte ProtocolVersion = 1;
    private const byte TeleportCommandType = 6;
    private const byte PushToTalkCommandType = 7;
    private const byte AdminLoginCommandType = 15;
    private const byte FirstOwnerClaimType = 18;
    private const byte AdminChatCommandType = 10;
    private const byte AdminActionCommandType = 11;
    private const byte OpusAudioFormat = 2;
    private const int MaxOpusPacketBytes = 1275;
    private const int MaxAdminChatBytes = 220;
    private const int MaxAdminActionTextBytes = 2048;

    public static byte[] BuildFirstOwnerClaim(
        uint sequence,
        string displayName,
        string accountId,
        string accessKey,
        string setupCode)
    {
        displayName = displayName.Trim();
        accountId = accountId.Trim();
        accessKey = accessKey.Trim();
        setupCode = NormalizeSetupCode(setupCode);

        byte[] name = Encoding.UTF8.GetBytes(displayName);
        if (name.Length is < 1 or > 64 ||
            displayName.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "Display name must be 1-64 UTF-8 bytes without control characters.");
        }

        byte[] account = Encoding.UTF8.GetBytes(accountId);
        if (account.Length is < 3 or > 32 ||
            account.Any(value =>
                !((value >= (byte)'a' && value <= (byte)'z') ||
                  (value >= (byte)'A' && value <= (byte)'Z') ||
                  (value >= (byte)'0' && value <= (byte)'9') ||
                  value is (byte)'.' or (byte)'_' or (byte)'-')))
        {
            throw new InvalidDataException(
                "Account ID must be 3-32 letters, numbers, dots, dashes, or underscores.");
        }

        if (accessKey.Length is < 32 or > 128 ||
            accessKey.Any(ch => ch <= 0x20 || ch > 0x7e))
        {
            throw new InvalidDataException(
                "The generated Owner access key is invalid.");
        }

        byte[] payload = Encoding.UTF8.GetBytes(
            accountId + "\n" + accessKey);
        int authenticatedLength = checked(
            HeaderSize + name.Length + payload.Length);
        byte[] datagram = new byte[checked(
            authenticatedLength + AuthTagSize)];
        Span<byte> header = datagram.AsSpan(0, HeaderSize);

        "CVB1"u8.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = FirstOwnerClaimType;
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], -1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[48..50],
            checked((ushort)name.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..56],
            checked((uint)payload.Length));

        name.CopyTo(datagram.AsSpan(HeaderSize));
        payload.CopyTo(datagram.AsSpan(HeaderSize + name.Length));

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(setupCode));
        byte[] tag = hmac.ComputeHash(
            datagram,
            0,
            authenticatedLength);
        tag.CopyTo(datagram, authenticatedLength);
        return datagram;
    }

    private static string NormalizeSetupCode(string setupCode)
    {
        string compact = new(
            setupCode
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        if (compact.Length != 24 || compact.Any(ch => !alphabet.Contains(ch)))
        {
            throw new InvalidDataException(
                "Enter the 24-character setup code shown in the CS2 server console.");
        }

        return string.Join(
            "-",
            Enumerable.Range(0, 6)
                .Select(index => compact.Substring(index * 4, 4)));
    }

    public static byte[] BuildAdminLogin(
        uint sequence,
        string accountId,
        ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
            throw new InvalidOperationException(
                "SharedSecret is empty.");

        byte[] payload = Encoding.UTF8.GetBytes(accountId.Trim());
        if (payload.Length is < 3 or > 32 ||
            payload.Any(value =>
                !((value >= (byte)'a' && value <= (byte)'z') ||
                  (value >= (byte)'A' && value <= (byte)'Z') ||
                  (value >= (byte)'0' && value <= (byte)'9') ||
                  value is (byte)'.' or (byte)'_' or (byte)'-')))
        {
            throw new InvalidDataException("Administrator account ID is invalid.");
        }

        int authenticatedLength = HeaderSize + payload.Length;
        byte[] datagram = new byte[authenticatedLength + AuthTagSize];

        Span<byte> header =
            datagram.AsSpan(0, HeaderSize);

        // CVB1 protocol header.
        "CVB1"u8.CopyTo(header);

        header[4] = ProtocolVersion;
        header[5] = AdminLoginCommandType;
        header[6] = 0;
        header[7] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[8..12],
            sequence);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            unchecked(
                (uint)DateTimeOffset.UtcNow
                    .ToUnixTimeSeconds()));

        // CONNECT does not identify a game player.
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[16..24],
            0UL);

        BinaryPrimitives.WriteInt32LittleEndian(
            header[24..28],
            -1);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..56],
            checked((uint)payload.Length));

        payload.CopyTo(datagram.AsSpan(HeaderSize, payload.Length));

        using var hmac =
            new HMACSHA256(secret.ToArray());

        byte[] tag =
            hmac.ComputeHash(
                datagram,
                0,
                authenticatedLength);

        tag.CopyTo(
            datagram,
            authenticatedLength);

        return datagram;
    }

    public static byte[] BuildTeleport(
        uint sequence,
        ulong steamId,
        int playerSlot,
        float x,
        float y,
        float z,
        ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
            throw new InvalidOperationException("SharedSecret is empty.");

        byte[] datagram = new byte[HeaderSize + AuthTagSize];
        Span<byte> header = datagram.AsSpan(0, HeaderSize);

        "CVB1"u8.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = TeleportCommandType;
        header[6] = 0;
        header[7] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..24], steamId);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], playerSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..32],
            unchecked((uint)BitConverter.SingleToInt32Bits(x)));
        BinaryPrimitives.WriteInt32LittleEndian(
            header[32..36],
            BitConverter.SingleToInt32Bits(y));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[36..40],
            unchecked((uint)BitConverter.SingleToInt32Bits(z)));

        using var hmac = new HMACSHA256(secret.ToArray());
        byte[] tag = hmac.ComputeHash(datagram, 0, HeaderSize);
        tag.CopyTo(datagram, HeaderSize);
        return datagram;
    }

    public static byte[] BuildAdminChat(
        uint sequence,
        string message,
        ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
            throw new InvalidOperationException(
                "SharedSecret is empty.");

        string normalized = message.Trim();

        if (normalized.Length == 0)
            throw new ArgumentException(
                "Chat message is empty.",
                nameof(message));

        foreach (char ch in normalized)
        {
            if (ch == '\r' ||
                ch == '\n' ||
                ch == '\0' ||
                (char.IsControl(ch) && ch != '\t'))
            {
                throw new ArgumentException(
                    "Chat message contains unsupported control characters.",
                    nameof(message));
            }
        }

        byte[] payload =
            Encoding.UTF8.GetBytes(normalized);

        if (payload.Length > MaxAdminChatBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"Chat message must be {MaxAdminChatBytes} UTF-8 bytes or fewer.");
        }

        int authenticatedLength =
            checked(HeaderSize + payload.Length);

        byte[] datagram =
            new byte[checked(authenticatedLength + AuthTagSize)];

        Span<byte> header =
            datagram.AsSpan(0, HeaderSize);

        "CVB1"u8.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = AdminChatCommandType;
        header[6] = 0;
        header[7] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[8..12],
            sequence);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            unchecked(
                (uint)DateTimeOffset.UtcNow
                    .ToUnixTimeSeconds()));

        BinaryPrimitives.WriteUInt64LittleEndian(
            header[16..24],
            0UL);

        BinaryPrimitives.WriteInt32LittleEndian(
            header[24..28],
            -1);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..56],
            checked((uint)payload.Length));

        payload.CopyTo(
            datagram.AsSpan(
                HeaderSize,
                payload.Length));

        using var hmac =
            new HMACSHA256(secret.ToArray());

        byte[] tag =
            hmac.ComputeHash(
                datagram,
                0,
                authenticatedLength);

        tag.CopyTo(
            datagram,
            authenticatedLength);

        return datagram;
    }

    public static byte[] BuildAdminAction(
        uint sequence,
        AdminActionCode action,
        int playerSlot,
        int value,
        string? text,
        ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
            throw new InvalidOperationException(
                "SharedSecret is empty.");

        if (action == AdminActionCode.None ||
            (uint)action > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(action));
        }

        if (playerSlot < -1 || playerSlot > 255)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerSlot));
        }

        string normalized =
            text?.Trim() ?? string.Empty;

        foreach (char ch in normalized)
        {
            if (ch == '\r' ||
                ch == '\n' ||
                ch == '\0' ||
                (char.IsControl(ch) && ch != '\t'))
            {
                throw new ArgumentException(
                    "Admin action text contains unsupported control characters.",
                    nameof(text));
            }
        }

        byte[] payload =
            Encoding.UTF8.GetBytes(normalized);

        if (payload.Length > MaxAdminActionTextBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Admin action text must be {MaxAdminActionTextBytes} UTF-8 bytes or fewer.");
        }

        int authenticatedLength =
            checked(HeaderSize + payload.Length);

        byte[] datagram =
            new byte[checked(authenticatedLength + AuthTagSize)];

        Span<byte> header =
            datagram.AsSpan(0, HeaderSize);

        "CVB1"u8.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = AdminActionCommandType;
        header[6] = 0;
        header[7] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[8..12],
            sequence);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            unchecked(
                (uint)DateTimeOffset.UtcNow
                    .ToUnixTimeSeconds()));

        BinaryPrimitives.WriteUInt64LittleEndian(
            header[16..24],
            0UL);

        BinaryPrimitives.WriteInt32LittleEndian(
            header[24..28],
            playerSlot);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..32],
            (uint)action);

        BinaryPrimitives.WriteInt32LittleEndian(
            header[32..36],
            value);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..56],
            checked((uint)payload.Length));

        if (payload.Length > 0)
        {
            payload.CopyTo(
                datagram.AsSpan(
                    HeaderSize,
                    payload.Length));
        }

        using var hmac =
            new HMACSHA256(secret.ToArray());

        byte[] tag =
            hmac.ComputeHash(
                datagram,
                0,
                authenticatedLength);

        tag.CopyTo(
            datagram,
            authenticatedLength);

        return datagram;
    }

    public static byte[] BuildPushToTalk(
        uint sequence,
        ReadOnlySpan<byte> opusPayload,
        int sequenceBytes,
        uint sectionNumber,
        uint uncompressedSampleOffset,
        float voiceLevel,
        ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
            throw new InvalidOperationException("SharedSecret is empty.");
        if (opusPayload.IsEmpty || opusPayload.Length > MaxOpusPacketBytes)
            throw new ArgumentOutOfRangeException(nameof(opusPayload));

        int authenticatedLength = checked(HeaderSize + opusPayload.Length);
        byte[] datagram = new byte[checked(authenticatedLength + AuthTagSize)];
        Span<byte> header = datagram.AsSpan(0, HeaderSize);

        "CVB1"u8.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = PushToTalkCommandType;
        header[6] = OpusAudioFormat;
        header[7] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        // Synthetic SERVER BROADCAST identity. No player is impersonated.
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..24], 0UL);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], -1);

        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], 48000);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..36], sequenceBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..40], sectionNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[40..44],
            uncompressedSampleOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[48..50], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[50..52], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..56],
            checked((uint)opusPayload.Length));
        BinaryPrimitives.WriteInt32LittleEndian(
            header[56..60],
            BitConverter.SingleToInt32Bits(Math.Clamp(voiceLevel, 0f, 1f)));

        opusPayload.CopyTo(datagram.AsSpan(HeaderSize, opusPayload.Length));

        using var hmac = new HMACSHA256(secret.ToArray());
        byte[] tag = hmac.ComputeHash(datagram, 0, authenticatedLength);
        tag.CopyTo(datagram, authenticatedLength);
        return datagram;
    }
}
