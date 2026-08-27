#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace voicebridge
{
constexpr std::uint8_t kProtocolVersion = 1;
constexpr std::uint8_t kMessageVoice = 1;
constexpr std::uint8_t kMessagePlayerConnected = 2;
constexpr std::uint8_t kMessagePlayerDisconnected = 3;
constexpr std::uint8_t kMessageMapChanged = 4;
constexpr std::uint8_t kMessagePlayerPosition = 5;
constexpr std::uint8_t kMessageTeleportCommand = 6;
constexpr std::uint8_t kMessagePushToTalkCommand = 7;
constexpr std::uint8_t kMessageConnectCommand = 8;

// NEO CHAT STAGE 3S
//
// Server -> Windows:
//   type 9 = chat event
//
// Windows -> server:
//   type 10 = authenticated admin chat command
constexpr std::uint8_t kMessageChatEvent = 9;
constexpr std::uint8_t kMessageAdminChatCommand = 10;

// NEO ADMIN CONTROL STAGE 3T
//
// Windows -> server:
//   type 11 = authenticated admin action
//
// Server -> Windows:
//   type 12 = authenticated action result
constexpr std::uint8_t kMessageAdminActionCommand = 11;
constexpr std::uint8_t kMessageAdminActionResult = 12;

// Stage 3V: server -> Windows filesystem map catalog.
constexpr std::uint8_t kMessageMapCatalog = 13;

// Server -> Windows authenticated health snapshot.
constexpr std::uint8_t kMessageServerHealth = 14;

// Permission-aware administrator authentication and account management.
constexpr std::uint8_t kMessageAdminLoginCommand = 15;
constexpr std::uint8_t kMessageAdminSession = 16;
constexpr std::uint8_t kMessageAdminAccountCatalog = 17;

// Windows -> server: one-time first-owner claim on an empty installation.
constexpr std::uint8_t kMessageFirstOwnerClaim = 18;

// Server-owned administrator security catalogs.
constexpr std::uint8_t kMessageAdminAuditCatalog = 19;
constexpr std::uint8_t kMessageAdminBanCatalog = 20;
constexpr std::uint8_t kMessageDisciplineCatalog = 21;
constexpr std::uint8_t kMessageDisciplineHistory = 22;
constexpr std::uint8_t kMessageMapRotationCatalog = 23;
constexpr std::uint8_t kMessageAnnouncementCatalog = 24;
constexpr std::uint8_t kMessageGameAdminCatalog = 25;
constexpr std::uint8_t kMessageMapOverviewChunk = 26;

enum class AdminActionCode : std::uint32_t
{
    None = 0,

    // Stage 3T player administration.
    Kick = 1,
    Slay = 2,
    Respawn = 3,
    MoveToT = 4,
    MoveToCT = 5,
    MoveToSpectator = 6,

    // Reserved for next stages.
    Freeze = 7,
    Unfreeze = 8,
    Mute = 9,
    Unmute = 10,
    Gag = 11,
    Ungag = 12,
    Ban = 13,

    SetHealth = 20,
    SetArmor = 21,
    SetMoney = 22,
    GiveItem = 23,

    ChangeMap = 40,
    RestartRound = 41,
    RestartMatch = 42,
    EndWarmup = 43,
    PauseMatch = 44,
    UnpauseMatch = 45,
    SwapTeams = 46,
    AddBot = 47,
    RemoveBots = 48,

    // Stage 3V: scan game/csgo/maps and send the discovered
    // filesystem map catalog to NEO ADMIN.
    RequestMapCatalog = 49,

    // Request an authenticated server health snapshot.
    RequestServerHealth = 50,
    RequestMapOverview = 51,

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

    SetWhitelistedCvar = 60,

    RequestPlayerInspector = 80,
};
constexpr std::size_t kHeaderSize = 60;
constexpr std::size_t kAuthTagSize = 32;
constexpr std::size_t kMaxUdpDatagram = 65507;

struct VoicePacketData
{
    std::uint8_t message_type = kMessageVoice;
    std::uint8_t audio_format = 0;
    std::uint8_t flags = 0;
    std::uint32_t sequence = 0;
    std::uint32_t tick = 0;
    std::uint64_t steam_id = 0;
    std::int32_t player_slot = -1;
    std::uint32_t sample_rate = 0;
    std::int32_t sequence_bytes = 0;
    std::uint32_t section_number = 0;
    std::uint32_t uncompressed_sample_offset = 0;
    std::uint32_t num_packets = 0;
    float voice_level = 0.0f;
    std::string_view player_name;
    std::span<const std::uint32_t> packet_offsets;
    std::span<const std::uint8_t> payload;
};

struct TeleportCommandData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::uint64_t steam_id = 0;
    std::int32_t player_slot = -1;
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

struct ConnectCommandData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
};

struct AdminLoginCommandData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::string account_id;
    std::string display_name;
};

struct FirstOwnerClaimData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::string account_id;
    std::string display_name;
    std::string access_key;
};

struct AdminChatCommandData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::string message;
};

struct AdminActionCommandData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;

    std::uint32_t action = 0;
    std::int32_t player_slot = -1;

    // General integer argument for future whitelisted actions.
    std::int32_t value = 0;

    // Optional UTF-8/plain-text argument.
    std::string text;
};

struct PushToTalkCommandData
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::uint64_t steam_id = 0;
    std::int32_t player_slot = -1;
    std::uint32_t sample_rate = 0;
    std::int32_t sequence_bytes = 0;
    std::uint32_t section_number = 0;
    std::uint32_t uncompressed_sample_offset = 0;
    std::uint32_t num_packets = 0;
    float voice_level = 0.0f;
    std::vector<std::uint8_t> payload;
};

// Authenticated Windows admin registration.
// This is a fixed 60-byte CVB1 header followed by a 32-byte HMAC.
bool TryParseAuthenticatedConnectCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    ConnectCommandData& command);

// Reads the claimed account ID after structural validation, before a key is
// selected. The caller must still call TryParseAuthenticatedAdminLoginCommand.
bool TryReadAdminLoginAccountId(
    std::span<const std::uint8_t> datagram,
    std::string& account_id);

// Builds a non-secret account selector from an access key. New clients can
// authenticate with only the access key while the server still resolves the
// real account before verifying the signed login packet.
std::string BuildAdminAccessSelector(
    std::span<const std::uint8_t> account_secret);

bool TryParseAuthenticatedAdminLoginCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> account_secret,
    AdminLoginCommandData& command);

bool TryReadFirstOwnerClaim(
    std::span<const std::uint8_t> datagram,
    FirstOwnerClaimData& command);

bool TryParseAuthenticatedFirstOwnerClaim(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> setup_secret,
    FirstOwnerClaimData& command);

// Authenticated NEO ADMIN chat command.
//
// Payload:
//   UTF-8/plain text, 1..220 bytes.
//
// Newlines, NULs, and control characters are rejected.
bool TryParseAuthenticatedAdminChatCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    AdminChatCommandData& command);

// NEO ADMIN CONTROL STAGE 3T
//
// Header mapping for type 11:
//
//   sequence       offset 8
//   unix_time      offset 12
//   player_slot    offset 24
//   action         offset 28 (sample_rate field)
//   value          offset 32 (sequence_bytes field)
//   text           normal payload field
//
// No arbitrary server command text is accepted.
bool TryParseAuthenticatedAdminActionCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    AdminActionCommandData& command);

// Stage 2: authenticate and parse Windows PTT only.
// No CS2 voice injection is performed here.
bool TryParseAuthenticatedPushToTalkCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    PushToTalkCommandData& command);

// Verifies and parses a fixed-size authenticated Windows-to-server
// drag-teleport command.
bool TryParseAuthenticatedTeleportCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    TeleportCommandData& command);

// Returns an empty vector when the packet would exceed the UDP payload limit
// or a field cannot be represented by protocol v1.
std::vector<std::uint8_t> BuildAuthenticatedVoicePacket(
    const VoicePacketData& data,
    std::span<const std::uint8_t> shared_secret);

} // namespace voicebridge
