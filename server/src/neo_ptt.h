#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "neo_admin_discipline.h"
#include "neo_admin_operations.h"

namespace voicebridge
{
    struct VoicePacketData;
}

using NeoPttSessionId = std::uint64_t;
constexpr NeoPttSessionId kInvalidNeoPttSessionId = 0;

struct NeoPttStats
{
    std::uint64_t received = 0;
    std::uint64_t authenticated = 0;
    std::uint64_t rejected = 0;
    std::uint64_t payload_bytes = 0;
    std::uint32_t last_sequence = 0;
};


struct NeoPttTeleportCommand
{
    NeoPttSessionId session_id = kInvalidNeoPttSessionId;
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::uint64_t steam_id = 0;
    std::int32_t player_slot = -1;
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

struct NeoPttAdminChatCommand
{
    NeoPttSessionId session_id = kInvalidNeoPttSessionId;
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;
    std::string message;
    std::string account_id;
    std::string operator_name;
    bool authorized = false;
    std::string denial_message;
};

struct NeoPttAdminActionCommand
{
    NeoPttSessionId session_id = kInvalidNeoPttSessionId;
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;

    std::uint32_t action = 0;
    std::int32_t player_slot = -1;
    std::int32_t value = 0;

    std::string text;
    std::string account_id;
    std::string operator_name;
    std::uint64_t permissions = 0;
    bool authorized = false;
    std::string denial_message;
};

struct NeoPttFrame
{
    std::uint32_t sequence = 0;
    std::uint32_t unix_time = 0;

    std::uint32_t sample_rate = 0;
    std::int32_t sequence_bytes = 0;
    std::uint32_t section_number = 0;
    std::uint32_t uncompressed_sample_offset = 0;
    std::uint32_t num_packets = 0;

    float voice_level = 0.0f;

    std::vector<std::uint8_t> payload;
};

bool NeoPtt_Start();
void NeoPtt_Shutdown();

bool NeoPtt_IsListening();
std::uint16_t NeoPtt_GetPort();
const char* NeoPtt_GetLastError();
bool NeoPtt_NeedsFirstOwnerSetup();
const char* NeoPtt_GetFirstOwnerSetupCode();

// Stage 2 only.
// Nonblocking receive/authenticate/count/discard.
// No CS2 voice injection is performed.
std::uint64_t NeoPtt_Poll();

bool NeoPtt_TryPop(NeoPttFrame& frame);

// Unified authenticated admin UDP transport.
//
// Every authenticated client keeps an independent endpoint, secret,
// permission set, and replay window. Live events are broadcast to every
// session; request replies are sent only to the originating session.
bool NeoPtt_HasPeer();
std::size_t NeoPtt_GetPeerCount();
bool NeoPtt_SendVoicePacket(
    const voicebridge::VoicePacketData& data);
bool NeoPtt_SendVoicePacketTo(
    NeoPttSessionId session_id,
    const voicebridge::VoicePacketData& data);

bool NeoPtt_TakeAdminChat(
    NeoPttAdminChatCommand& command);

bool NeoPtt_TakeAdminAction(
    NeoPttAdminActionCommand& command);

bool NeoPtt_TakeTeleport(
    NeoPttTeleportCommand& command);

bool NeoPtt_SendAccountCatalog(NeoPttSessionId session_id);
bool NeoPtt_SendGameAdminCatalog(NeoPttSessionId session_id);
bool NeoPtt_SendAuditCatalog(NeoPttSessionId session_id);
bool NeoPtt_SendBanCatalog(NeoPttSessionId session_id);
bool NeoPtt_SendDisciplineCatalog(NeoPttSessionId session_id);
bool NeoPtt_SendDisciplineHistory(
    NeoPttSessionId session_id,
    std::string_view steam_id);
bool NeoPtt_SendMapRotationCatalog(NeoPttSessionId session_id);
bool NeoPtt_SendAnnouncementCatalog(NeoPttSessionId session_id);
void NeoPtt_RecordAudit(
    std::string_view acting_account_id,
    std::string_view action,
    std::string_view target,
    bool success,
    std::string_view details);
bool NeoPtt_SaveBan(
    std::string_view request_json,
    std::string_view acting_account_id,
    std::uint64_t& steam_id,
    std::string& target,
    std::string& message);
bool NeoPtt_DeleteBan(
    std::string_view steam_id,
    std::string_view acting_account_id,
    std::string& target,
    std::string& message);
bool NeoPtt_SaveRestriction(
    std::string_view request_json,
    std::string_view acting_account_id,
    neo_admin::RestrictionRecord& saved,
    std::string& message);
bool NeoPtt_DeleteRestriction(
    std::string_view request_json,
    std::string_view acting_account_id,
    neo_admin::RestrictionRecord& removed,
    std::string& message);
bool NeoPtt_RecordDiscipline(
    std::uint64_t steam_id,
    std::string_view player_name,
    std::string_view action,
    std::string_view reason,
    std::string_view acting_account_id,
    std::uint64_t expires_unix = 0);
bool NeoPtt_SaveMapRotation(
    std::string_view request_json,
    const std::vector<std::string>& allowed_maps,
    std::string_view acting_account_id,
    std::string& message);
bool NeoPtt_SaveScheduledMap(
    std::string_view request_json,
    const std::vector<std::string>& allowed_maps,
    std::string_view acting_account_id,
    std::string& message);
bool NeoPtt_DeleteScheduledMap(std::string_view id, std::string& message);
bool NeoPtt_RunNextMap(neo_admin::DueMapChange& due, std::string& message);
bool NeoPtt_TakeDueMap(neo_admin::DueMapChange& due);
bool NeoPtt_SaveAnnouncement(
    std::string_view request_json,
    std::string_view acting_account_id,
    std::string& message);
bool NeoPtt_DeleteAnnouncement(std::string_view id, std::string& message);
bool NeoPtt_TakeDueAnnouncement(neo_admin::DueAnnouncement& due);
bool NeoPtt_IsSteamIdBanned(
    std::uint64_t steam_id,
    std::string& reason);
bool NeoPtt_GetInGameAdmin(
    std::uint64_t steam_id,
    std::string& account_id,
    std::string& display_name,
    std::uint64_t& permissions);
bool NeoPtt_SaveAdminAccount(
    std::string_view request_json,
    std::string_view acting_account_id,
    std::string& message);
bool NeoPtt_DeleteAdminAccount(
    std::string_view account_id,
    std::string_view acting_account_id,
    std::string& message);
bool NeoPtt_SaveGameAdmin(
    std::string_view request_json,
    std::string& message);
bool NeoPtt_DeleteGameAdmin(
    std::string_view steam_id,
    std::string& message);

NeoPttStats NeoPtt_GetStats();
