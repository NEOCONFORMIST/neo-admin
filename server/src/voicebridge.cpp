#include "voicebridge.h"
#include "voicebridge_protocol.h"
#include "neo_ptt.h"

#include <algorithm>
#include <cerrno>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <limits>
#include <span>
#include <sstream>
#include <string_view>

#if defined(__linux__)
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <sys/types.h>
#include <unistd.h>
#endif

#if defined(__linux__)
namespace
{
std::uint32_t FloatBits(float value)
{
    std::uint32_t encoded = 0;
    static_assert(sizeof(encoded) == sizeof(value));
    std::memcpy(&encoded, &value, sizeof(encoded));
    return encoded;
}

bool ReadCpuSnapshot(
    std::uint64_t& total,
    std::uint64_t& idle)
{
    std::ifstream input("/proc/stat");
    std::string label;
    std::uint64_t user = 0;
    std::uint64_t nice = 0;
    std::uint64_t system = 0;
    std::uint64_t idle_ticks = 0;
    std::uint64_t io_wait = 0;
    std::uint64_t irq = 0;
    std::uint64_t soft_irq = 0;
    std::uint64_t steal = 0;

    if (!(input >> label >> user >> nice >> system >> idle_ticks >>
            io_wait >> irq >> soft_irq >> steal) ||
        label != "cpu")
    {
        return false;
    }

    idle = idle_ticks + io_wait;
    total = user + nice + system + idle_ticks + io_wait +
        irq + soft_irq + steal;
    return total > 0;
}

float ReadMemoryUsagePercent()
{
    std::ifstream input("/proc/meminfo");
    std::string line;
    std::uint64_t total_kb = 0;
    std::uint64_t available_kb = 0;

    while (std::getline(input, line))
    {
        std::istringstream values(line);
        std::string key;
        std::uint64_t value = 0;

        if (!(values >> key >> value))
            continue;

        if (key == "MemTotal:")
            total_kb = value;
        else if (key == "MemAvailable:")
            available_kb = value;

        if (total_kb > 0 && available_kb > 0)
            break;
    }

    if (total_kb == 0 || available_kb > total_kb)
        return std::numeric_limits<float>::quiet_NaN();

    return 100.0F * static_cast<float>(total_kb - available_kb) /
        static_cast<float>(total_kb);
}

bool SameUdpEndpoint(
    const sockaddr_storage& left,
    socklen_t left_length,
    const sockaddr_storage& right,
    socklen_t right_length)
{
    if (left.ss_family != right.ss_family)
        return false;

    if (left.ss_family == AF_INET)
    {
        if (left_length < sizeof(sockaddr_in) ||
            right_length < sizeof(sockaddr_in))
        {
            return false;
        }

        const auto* a = reinterpret_cast<const sockaddr_in*>(&left);
        const auto* b = reinterpret_cast<const sockaddr_in*>(&right);
        return a->sin_port == b->sin_port &&
            a->sin_addr.s_addr == b->sin_addr.s_addr;
    }

    if (left.ss_family == AF_INET6)
    {
        if (left_length < sizeof(sockaddr_in6) ||
            right_length < sizeof(sockaddr_in6))
        {
            return false;
        }

        const auto* a = reinterpret_cast<const sockaddr_in6*>(&left);
        const auto* b = reinterpret_cast<const sockaddr_in6*>(&right);
        return a->sin6_port == b->sin6_port &&
            a->sin6_scope_id == b->sin6_scope_id &&
            std::memcmp(
                &a->sin6_addr,
                &b->sin6_addr,
                sizeof(in6_addr)) == 0;
    }

    return false;
}
} // namespace
#endif

VoiceBridge::~VoiceBridge()
{
    Shutdown();
}

bool VoiceBridge::HasOutputTransport() const
{
    return NeoPtt_HasPeer() ||
        (socket_fd_ >= 0 && !shared_secret_.empty());
}

bool VoiceBridge::IsConfigured() const
{
    return HasOutputTransport();
}

std::span<const std::uint8_t> VoiceBridge::SigningSecret() const
{
    const std::span<const std::uint8_t> peer_secret =
        NeoPtt_GetPeerSecret();
    return peer_secret.empty()
        ? std::span<const std::uint8_t>(shared_secret_)
        : peer_secret;
}

bool VoiceBridge::ConfigureFromEnvironment()
{
    Shutdown();

#if !defined(__linux__)
    return false;
#else
    sequence_.store(0);
    dropped_packets_.store(0);
    last_teleport_sequence_ = 0;
    last_teleport_time_ = 0;

    {
        std::scoped_lock lock(presence_mutex_);
        for (PlayerPresence& player : players_)
            player = {};
    }

    const auto now = std::chrono::steady_clock::now();
    next_presence_broadcast_ = now + std::chrono::seconds(1);
    next_position_frame_ = now;
    map_started_at_ = now;
    previous_health_sample_at_ = {};
    previous_health_tick_ = 0;
    has_health_tick_sample_ = false;

    has_cpu_sample_ = ReadCpuSnapshot(
        previous_cpu_total_,
        previous_cpu_idle_);

    const char* host = std::getenv("VOICEBRIDGE_HOST");
    const char* port = std::getenv("VOICEBRIDGE_PORT");
    const char* secret = std::getenv("VOICEBRIDGE_SECRET");

    if (!host || !*host || !secret || std::strlen(secret) < 16)
        return false;

    if (!port || !*port)
        port = "27120";

    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_protocol = IPPROTO_UDP;

    addrinfo* results = nullptr;
    if (getaddrinfo(host, port, &hints, &results) != 0 || !results)
        return false;

    bool configured = false;
    for (const addrinfo* current = results; current; current = current->ai_next)
    {
        if (current->ai_addrlen > sizeof(destination_))
            continue;

        const int candidate = socket(
            current->ai_family,
            current->ai_socktype,
            current->ai_protocol);

        if (candidate < 0)
            continue;

        const int current_flags = fcntl(candidate, F_GETFL, 0);
        if (current_flags >= 0)
            fcntl(candidate, F_SETFL, current_flags | O_NONBLOCK);

        std::memcpy(
            &destination_,
            current->ai_addr,
            current->ai_addrlen);

        destination_length_ =
            static_cast<socklen_t>(current->ai_addrlen);
        socket_fd_ = candidate;
        configured = true;
        break;
    }

    freeaddrinfo(results);

    if (!configured)
        return false;

    shared_secret_.assign(secret, secret + std::strlen(secret));
    return true;
#endif
}

void VoiceBridge::Shutdown()
{
#if defined(__linux__)
    if (socket_fd_ >= 0)
        close(socket_fd_);
#endif

    socket_fd_ = -1;
    shared_secret_.clear();
    last_teleport_sequence_ = 0;
    last_teleport_time_ = 0;
    previous_health_sample_at_ = {};
    previous_health_tick_ = 0;
    has_health_tick_sample_ = false;
    previous_cpu_total_ = 0;
    previous_cpu_idle_ = 0;
    has_cpu_sample_ = false;

    std::scoped_lock lock(presence_mutex_);
    for (PlayerPresence& player : players_)
        player = {};
    current_map_.clear();
}

void VoiceBridge::RememberPlayer(
    std::uint64_t steam_id,
    std::int32_t player_slot,
    const char* player_name)
{
    if (player_slot < 0 ||
        static_cast<std::size_t>(player_slot) >= players_.size())
    {
        return;
    }

    std::scoped_lock lock(presence_mutex_);
    PlayerPresence& player =
        players_[static_cast<std::size_t>(player_slot)];

    player.connected = true;
    player.steam_id = steam_id;
    player.player_slot = player_slot;

    if (player_name && *player_name)
        player.player_name = player_name;
}

void VoiceBridge::ForgetPlayer(std::int32_t player_slot)
{
    if (player_slot < 0 ||
        static_cast<std::size_t>(player_slot) >= players_.size())
    {
        return;
    }

    std::scoped_lock lock(presence_mutex_);
    players_[static_cast<std::size_t>(player_slot)] = {};
}

bool VoiceBridge::SendVoice(
    std::uint8_t audio_format,
    std::uint32_t tick,
    std::uint64_t steam_id,
    std::int32_t player_slot,
    const char* player_name,
    std::uint32_t sample_rate,
    std::int32_t sequence_bytes,
    std::uint32_t section_number,
    std::uint32_t uncompressed_sample_offset,
    std::uint32_t num_packets,
    const std::vector<std::uint32_t>& packet_offsets,
    const std::string& encoded_audio,
    float voice_level)
{
#if !defined(__linux__)
    (void)audio_format;
    (void)tick;
    (void)steam_id;
    (void)player_slot;
    (void)player_name;
    (void)sample_rate;
    (void)sequence_bytes;
    (void)section_number;
    (void)uncompressed_sample_offset;
    (void)num_packets;
    (void)packet_offsets;
    (void)encoded_audio;
    (void)voice_level;
    return false;
#else
    if (!HasOutputTransport() ||
        encoded_audio.empty())
    {
        return false;
    }

    RememberPlayer(steam_id, player_slot, player_name);

    const auto* payload_begin =
        reinterpret_cast<const std::uint8_t*>(encoded_audio.data());

    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageVoice,
        .audio_format = audio_format,
        .flags = 0,
        .sequence = sequence_.fetch_add(
            1,
            std::memory_order_relaxed),
        .tick = tick,
        .steam_id = steam_id,
        .player_slot = player_slot,
        .sample_rate = sample_rate,
        .sequence_bytes = sequence_bytes,
        .section_number = section_number,
        .uncompressed_sample_offset = uncompressed_sample_offset,
        .num_packets = num_packets,
        .voice_level = voice_level,
        .player_name = player_name
            ? std::string_view(player_name)
            : std::string_view(),
        .packet_offsets = packet_offsets,
        .payload = std::span<const std::uint8_t>(
            payload_begin,
            encoded_audio.size()),
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(
            data,
            SigningSecret()));
#endif
}

bool VoiceBridge::SendPlayerConnected(
    std::uint64_t steam_id,
    std::int32_t player_slot,
    const char* player_name)
{
    RememberPlayer(steam_id, player_slot, player_name);

    return SendPlayerState(
        voicebridge::kMessagePlayerConnected,
        steam_id,
        player_slot,
        player_name);
}

bool VoiceBridge::SendPlayerDisconnected(
    std::uint64_t steam_id,
    std::int32_t player_slot,
    const char* player_name)
{
    const bool sent = SendPlayerState(
        voicebridge::kMessagePlayerDisconnected,
        steam_id,
        player_slot,
        player_name);

    ForgetPlayer(player_slot);
    return sent;
}

// NEO ADMIN CONTROL STAGE 3T RESULT BEGIN
bool VoiceBridge::SendAdminActionResult(
    std::uint32_t request_sequence,
    std::uint32_t action,
    std::int32_t player_slot,
    bool success,
    const char* message)
{
#if !defined(__linux__)
    (void)request_sequence;
    (void)action;
    (void)player_slot;
    (void)success;
    (void)message;
    return false;
#else
    if (!HasOutputTransport())
    {
        return false;
    }

    const std::string_view message_view =
        message
            ? std::string_view(message)
            : std::string_view();

    if (message_view.size() > 512)
        return false;

    const auto* payload_begin =
        reinterpret_cast<const std::uint8_t*>(
            message_view.data());

    const voicebridge::VoicePacketData data{
        .message_type =
            voicebridge::kMessageAdminActionResult,
        .audio_format = 0,
        .flags =
            success
                ? static_cast<std::uint8_t>(0x01U)
                : static_cast<std::uint8_t>(0x00U),
        .sequence = sequence_.fetch_add(
            1,
            std::memory_order_relaxed),

        // Echo the originating request sequence.
        .tick = request_sequence,

        .steam_id = 0,
        .player_slot = player_slot,

        // Echo the action code.
        .sample_rate = action,

        .sequence_bytes = 0,
        .section_number = 0,
        .uncompressed_sample_offset = 0,
        .num_packets = 0,
        .voice_level = 0.0f,
        .player_name = {},
        .packet_offsets = {},
        .payload =
            std::span<const std::uint8_t>(
                payload_begin,
                message_view.size()),
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(
            data,
            SigningSecret()));
#endif
}
// NEO ADMIN CONTROL STAGE 3T RESULT END


// NEO MAP CATALOG STAGE 3V BEGIN
bool VoiceBridge::SendMapCatalog(
    std::string_view catalog,
    std::uint32_t map_count)
{
#if !defined(__linux__)
    (void)catalog;
    (void)map_count;
    return false;
#else
    constexpr std::size_t
        kMaxMapCatalogPayloadBytes = 60000;

    if (!HasOutputTransport() ||
        catalog.empty() ||
        catalog.size() >
            kMaxMapCatalogPayloadBytes)
    {
        return false;
    }

    const auto* payload_begin =
        reinterpret_cast<const std::uint8_t*>(
            catalog.data());

    const voicebridge::VoicePacketData data{
        .message_type =
            voicebridge::kMessageMapCatalog,

        .audio_format = 0,
        .flags = 0,

        .sequence =
            sequence_.fetch_add(
                1,
                std::memory_order_relaxed),

        // Map count is sent in tick.
        .tick = map_count,

        .steam_id = 0,
        .player_slot = -1,

        .sample_rate = 0,
        .sequence_bytes = 0,
        .section_number = 0,
        .uncompressed_sample_offset = 0,
        .num_packets = 0,
        .voice_level = 0.0f,

        .player_name = {},
        .packet_offsets = {},

        .payload =
            std::span<const std::uint8_t>(
                payload_begin,
                catalog.size()),
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(
            data,
            SigningSecret()));
#endif
}
// NEO MAP CATALOG STAGE 3V END


bool VoiceBridge::SendServerHealth(
    std::uint32_t request_sequence,
    std::uint32_t current_tick,
    std::int32_t connected_players,
    std::uint32_t max_players,
    const char* plugin_version)
{
#if !defined(__linux__)
    (void)request_sequence;
    (void)current_tick;
    (void)connected_players;
    (void)max_players;
    (void)plugin_version;
    return false;
#else
    if (!HasOutputTransport())
        return false;

    const auto now = std::chrono::steady_clock::now();
    float tick_rate = std::numeric_limits<float>::quiet_NaN();

    if (has_health_tick_sample_ &&
        previous_health_sample_at_.time_since_epoch().count() != 0)
    {
        const double elapsed = std::chrono::duration<double>(
            now - previous_health_sample_at_).count();

        if (elapsed > 0.0)
        {
            const std::uint32_t tick_delta =
                current_tick - previous_health_tick_;
            const double observed =
                static_cast<double>(tick_delta) / elapsed;

            if (observed >= 0.0 && observed <= 1024.0)
                tick_rate = static_cast<float>(observed);
        }
    }

    previous_health_sample_at_ = now;
    previous_health_tick_ = current_tick;
    has_health_tick_sample_ = true;

    float cpu_percent =
        std::numeric_limits<float>::quiet_NaN();
    std::uint64_t cpu_total = 0;
    std::uint64_t cpu_idle = 0;

    if (ReadCpuSnapshot(cpu_total, cpu_idle))
    {
        if (has_cpu_sample_ &&
            cpu_total > previous_cpu_total_ &&
            cpu_idle >= previous_cpu_idle_)
        {
            const std::uint64_t total_delta =
                cpu_total - previous_cpu_total_;
            const std::uint64_t idle_delta =
                std::min(
                    total_delta,
                    cpu_idle - previous_cpu_idle_);

            cpu_percent = 100.0F *
                static_cast<float>(total_delta - idle_delta) /
                static_cast<float>(total_delta);
        }

        previous_cpu_total_ = cpu_total;
        previous_cpu_idle_ = cpu_idle;
        has_cpu_sample_ = true;
    }

    const float memory_percent =
        ReadMemoryUsagePercent();

    std::uint64_t map_uptime = 0;
    if (map_started_at_.time_since_epoch().count() != 0)
    {
        map_uptime = static_cast<std::uint64_t>(
            std::chrono::duration_cast<std::chrono::seconds>(
                now - map_started_at_).count());
    }

    const std::uint64_t dropped =
        dropped_packets_.load(std::memory_order_relaxed);

    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageServerHealth,
        .audio_format = 0,
        .flags = 0,
        .sequence = sequence_.fetch_add(
            1,
            std::memory_order_relaxed),

        // Echo the request sequence so Windows can calculate RTT.
        .tick = request_sequence,

        // Health packets reuse the fixed v1 header fields.
        .steam_id = map_uptime,
        .player_slot = std::max(0, connected_players),
        .sample_rate = max_players,
        .sequence_bytes = static_cast<std::int32_t>(
            FloatBits(tick_rate)),
        .section_number = FloatBits(cpu_percent),
        .uncompressed_sample_offset = FloatBits(memory_percent),
        .num_packets = static_cast<std::uint32_t>(
            std::min<std::uint64_t>(
                dropped,
                std::numeric_limits<std::uint32_t>::max())),
        .voice_level = 0.0F,
        .player_name = plugin_version
            ? std::string_view(plugin_version)
            : std::string_view(),
        .packet_offsets = {},
        .payload = {},
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(
            data,
            SigningSecret()));
#endif
}


// NEO CHAT STAGE 3S SERVER EVENT BEGIN
bool VoiceBridge::SendChatMessage(
    std::int32_t player_slot,
    const char* player_name,
    const char* message,
    std::uint8_t flags)
{
#if !defined(__linux__)
    (void)player_slot;
    (void)player_name;
    (void)message;
    (void)flags;
    return false;
#else
    if (!HasOutputTransport() ||
        !message ||
        !*message)
    {
        return false;
    }

    const std::string_view message_view(
        message);

    // CS2 chat is small; prevent accidental large datagrams.
    if (message_view.size() > 512)
        return false;

    const auto* payload_begin =
        reinterpret_cast<const std::uint8_t*>(
            message_view.data());

    const voicebridge::VoicePacketData data{
        .message_type =
            voicebridge::kMessageChatEvent,
        .audio_format = 0,
        .flags = flags,
        .sequence = sequence_.fetch_add(
            1,
            std::memory_order_relaxed),
        .tick = 0,
        .steam_id = 0,
        .player_slot = player_slot,
        .sample_rate = 0,
        .sequence_bytes = 0,
        .section_number = 0,
        .uncompressed_sample_offset = 0,
        .num_packets = 0,
        .voice_level = 0.0f,
        .player_name = player_name
            ? std::string_view(player_name)
            : std::string_view(),
        .packet_offsets = {},
        .payload =
            std::span<const std::uint8_t>(
                payload_begin,
                message_view.size()),
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(
            data,
            SigningSecret()));
#endif
}
// NEO CHAT STAGE 3S SERVER EVENT END


bool VoiceBridge::SendPlayerState(
    std::uint8_t message_type,
    std::uint64_t steam_id,
    std::int32_t player_slot,
    const char* player_name)
{
#if !defined(__linux__)
    (void)message_type;
    (void)steam_id;
    (void)player_slot;
    (void)player_name;
    return false;
#else
    if (!HasOutputTransport())
        return false;

    const voicebridge::VoicePacketData data{
        .message_type = message_type,
        .audio_format = 0,
        .flags = 0,
        .sequence = sequence_.fetch_add(
            1,
            std::memory_order_relaxed),
        .tick = 0,
        .steam_id = steam_id,
        .player_slot = player_slot,
        .sample_rate = 0,
        .sequence_bytes = 0,
        .section_number = 0,
        .uncompressed_sample_offset = 0,
        .num_packets = 0,
        .voice_level = 0.0f,
        .player_name = player_name
            ? std::string_view(player_name)
            : std::string_view(),
        .packet_offsets = {},
        .payload = {},
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(
            data,
            SigningSecret()));
#endif
}

void VoiceBridge::SetCurrentMap(const char* map_name)
{
    std::string normalized = map_name ? map_name : "";
    map_started_at_ = std::chrono::steady_clock::now();

    {
        std::scoped_lock lock(presence_mutex_);
        current_map_ = normalized;

        // CS2 can replace bots during a level load without delivering a
        // disconnect callback for every old slot. Active players are learned
        // again from the next position frames; SourceTV has no player pawn, so
        // retain its presence entry explicitly.
        for (PlayerPresence& player : players_)
        {
            if (player.player_name != "SourceTV")
                player = {};
        }
    }

    if (!normalized.empty())
        SendMapState(normalized.c_str());
}

bool VoiceBridge::SendMapState(const char* map_name)
{
#if !defined(__linux__)
    (void)map_name;
    return false;
#else
    if (!HasOutputTransport() || !map_name || !*map_name)
        return false;

    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageMapChanged,
        .audio_format = 0,
        .flags = 0,
        .sequence = sequence_.fetch_add(1, std::memory_order_relaxed),
        .tick = 0,
        .steam_id = 0,
        .player_slot = -1,
        .sample_rate = 0,
        .sequence_bytes = 0,
        .section_number = 0,
        .uncompressed_sample_offset = 0,
        .num_packets = 0,
        .voice_level = 0.0f,
        .player_name = std::string_view(map_name),
        .packet_offsets = {},
        .payload = {},
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(data, SigningSecret()));
#endif
}

bool VoiceBridge::SendPlayerPosition(
    std::uint32_t tick,
    std::uint64_t steam_id,
    std::int32_t player_slot,
    const char* player_name,
    float x,
    float y,
    float z,
    float yaw,
    std::int32_t team,
    std::int32_t health,
    bool alive,
    bool bot)
{
#if !defined(__linux__)
    (void)tick;
    (void)steam_id;
    (void)player_slot;
    (void)player_name;
    (void)x;
    (void)y;
    (void)z;
    (void)yaw;
    (void)team;
    (void)health;
    (void)alive;
    (void)bot;
    return false;
#else
    if (!HasOutputTransport())
        return false;

    RememberPlayer(steam_id, player_slot, player_name);

    std::uint8_t flags = 0;
    if (alive)
        flags |= 0x01U;
    if (bot)
        flags |= 0x02U;

    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessagePlayerPosition,
        .audio_format = 0,
        .flags = flags,
        .sequence = sequence_.fetch_add(1, std::memory_order_relaxed),
        .tick = tick,
        .steam_id = steam_id,
        .player_slot = player_slot,
        .sample_rate = FloatBits(x),
        .sequence_bytes = static_cast<std::int32_t>(FloatBits(y)),
        .section_number = FloatBits(z),
        .uncompressed_sample_offset = FloatBits(yaw),
        .num_packets = static_cast<std::uint32_t>(team),
        .voice_level = static_cast<float>(health),
        .player_name = player_name
            ? std::string_view(player_name)
            : std::string_view(),
        .packet_offsets = {},
        .payload = {},
    };

    return SendDatagram(
        voicebridge::BuildAuthenticatedVoicePacket(data, SigningSecret()));
#endif
}

bool VoiceBridge::ReceiveTeleportCommand(
    TeleportCommand& command)
{
#if !defined(__linux__)
    (void)command;
    return false;
#else
    // New unified UDP 27122 command path.
    //
    // Prefer commands received by the authenticated NEO admin
    // transport. Retain the original socket path below as a
    // temporary compatibility fallback.
    NeoPttTeleportCommand neo_command{};

    if (NeoPtt_TakeTeleport(neo_command))
    {
        command.sequence =
            neo_command.sequence;

        command.unix_time =
            neo_command.unix_time;

        command.steam_id =
            neo_command.steam_id;

        command.player_slot =
            neo_command.player_slot;

        command.x = neo_command.x;
        command.y = neo_command.y;
        command.z = neo_command.z;

        return true;
    }

    if (!HasOutputTransport())
        return false;

    std::array<std::uint8_t,
        voicebridge::kHeaderSize + voicebridge::kAuthTagSize> datagram{};

    sockaddr_storage sender{};
    socklen_t sender_length = sizeof(sender);
    const ssize_t received = recvfrom(
        socket_fd_,
        datagram.data(),
        datagram.size(),
        MSG_DONTWAIT,
        reinterpret_cast<sockaddr*>(&sender),
        &sender_length);

    if (received < 0)
        return false;

    if (received != static_cast<ssize_t>(datagram.size()) ||
        !SameUdpEndpoint(
            sender,
            sender_length,
            destination_,
            destination_length_))
    {
        return false;
    }

    voicebridge::TeleportCommandData parsed{};
    if (!voicebridge::TryParseAuthenticatedTeleportCommand(
            datagram,
            shared_secret_,
            parsed))
    {
        return false;
    }

    const std::time_t now = std::time(nullptr);
    if (now < 0)
        return false;

    const std::uint64_t current_time =
        static_cast<std::uint64_t>(now);
    const std::uint64_t command_time = parsed.unix_time;

    // Reject stale or implausibly future commands.
    if (command_time + 15U < current_time ||
        command_time > current_time + 15U)
    {
        return false;
    }

    // Reject an exact replay of the most recently accepted command.
    if (parsed.sequence == last_teleport_sequence_ &&
        parsed.unix_time == last_teleport_time_)
    {
        return false;
    }

    last_teleport_sequence_ = parsed.sequence;
    last_teleport_time_ = parsed.unix_time;

    command.sequence = parsed.sequence;
    command.unix_time = parsed.unix_time;
    command.steam_id = parsed.steam_id;
    command.player_slot = parsed.player_slot;
    command.x = parsed.x;
    command.y = parsed.y;
    command.z = parsed.z;
    return true;
#endif
}

bool VoiceBridge::ShouldSendPositionFrame()
{
#if !defined(__linux__)
    return false;
#else
    if (!HasOutputTransport())
        return false;

    const auto now = std::chrono::steady_clock::now();
    if (now < next_position_frame_)
        return false;

    next_position_frame_ = now + std::chrono::milliseconds(100);
    return true;
#endif
}

void VoiceBridge::TickPresence()
{
#if defined(__linux__)
    if (!HasOutputTransport())
        return;

    const auto now = std::chrono::steady_clock::now();
    if (now < next_presence_broadcast_)
        return;

    next_presence_broadcast_ = now + std::chrono::seconds(2);

    std::vector<PlayerPresence> snapshot;
    std::string map_snapshot;
    {
        std::scoped_lock lock(presence_mutex_);
        snapshot.reserve(players_.size());
        map_snapshot = current_map_;

        for (const PlayerPresence& player : players_)
        {
            if (player.connected)
                snapshot.push_back(player);
        }
    }

    if (!map_snapshot.empty())
        SendMapState(map_snapshot.c_str());

    for (const PlayerPresence& player : snapshot)
    {
        SendPlayerState(
            voicebridge::kMessagePlayerConnected,
            player.steam_id,
            player.player_slot,
            player.player_name.c_str());
    }
#endif
}

bool VoiceBridge::SendDatagram(
    const std::vector<std::uint8_t>& packet)
{
#if !defined(__linux__)
    (void)packet;
    return false;
#else
    if (packet.empty())
    {
        dropped_packets_.fetch_add(
            1,
            std::memory_order_relaxed);
        return false;
    }

    // Once Windows has authenticated itself with CONNECT/PTT,
    // send all server->Windows traffic through the SAME UDP
    // 27122 socket. This is important for NAT/firewall return
    // traffic and removes dependency on a fixed Windows IP.
    if (NeoPtt_HasPeer())
    {
        const bool sent =
            NeoPtt_SendDatagram(packet);

        if (!sent)
        {
            dropped_packets_.fetch_add(
                1,
                std::memory_order_relaxed);
        }

        return sent;
    }

    // Temporary legacy fallback until the dynamic CONNECT
    // path is proven live.
    const ssize_t sent = sendto(
        socket_fd_,
        packet.data(),
        packet.size(),
        MSG_DONTWAIT,
        reinterpret_cast<const sockaddr*>(&destination_),
        destination_length_);

    if (sent != static_cast<ssize_t>(packet.size()))
    {
        // Dropping a datagram is safer than stalling the CS2 game thread.
        dropped_packets_.fetch_add(
            1,
            std::memory_order_relaxed);
        return false;
    }

    return true;
#endif
}
