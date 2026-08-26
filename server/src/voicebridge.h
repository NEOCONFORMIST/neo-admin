#pragma once

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <span>
#include <string>
#include <vector>

#if defined(__linux__)
#include <sys/socket.h>
#endif

class VoiceBridge
{
public:
    VoiceBridge() = default;
    ~VoiceBridge();

    VoiceBridge(const VoiceBridge&) = delete;
    VoiceBridge& operator=(const VoiceBridge&) = delete;

    bool ConfigureFromEnvironment();
    void Shutdown();
    bool IsConfigured() const;
    std::uint64_t DroppedPackets() const { return dropped_packets_.load(); }

    struct TeleportCommand
    {
        std::uint32_t sequence = 0;
        std::uint32_t unix_time = 0;
        std::uint64_t steam_id = 0;
        std::int32_t player_slot = -1;
        float x = 0.0f;
        float y = 0.0f;
        float z = 0.0f;
    };

    // Reads one authenticated command from the existing nonblocking UDP socket.
    bool ReceiveTeleportCommand(TeleportCommand& command);

    bool SendVoice(
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
        float voice_level);

    bool SendPlayerConnected(
        std::uint64_t steam_id,
        std::int32_t player_slot,
        const char* player_name);

    bool SendPlayerDisconnected(
        std::uint64_t steam_id,
        std::int32_t player_slot,
        const char* player_name);

    bool SendAdminActionResult(
        std::uint32_t request_sequence,
        std::uint32_t action,
        std::int32_t player_slot,
        bool success,
        const char* message);

    bool SendMapCatalog(
        std::string_view catalog,
        std::uint32_t map_count);

    bool SendServerHealth(
        std::uint32_t request_sequence,
        std::uint32_t current_tick,
        std::int32_t connected_players,
        std::uint32_t max_players,
        const char* plugin_version);

    bool SendChatMessage(
        std::int32_t player_slot,
        const char* player_name,
        const char* message,
        std::uint8_t flags = 0);

    void SetCurrentMap(const char* map_name);

    bool SendPlayerPosition(
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
        bool bot);

    // Returns true at approximately 10 Hz, independent of CS2 map curtime.
    bool ShouldSendPositionFrame();

    // Re-sends the current roster and map every two seconds. This lets a
    // restarted listener repopulate without waiting for new events.
    void TickPresence();

private:
    static constexpr std::size_t kTrackedPlayerSlots = 128;

    struct PlayerPresence
    {
        bool connected = false;
        std::uint64_t steam_id = 0;
        std::int32_t player_slot = -1;
        std::string player_name;
    };

    void RememberPlayer(
        std::uint64_t steam_id,
        std::int32_t player_slot,
        const char* player_name);

    void ForgetPlayer(std::int32_t player_slot);

    bool SendPlayerState(
        std::uint8_t message_type,
        std::uint64_t steam_id,
        std::int32_t player_slot,
        const char* player_name);

    bool SendMapState(const char* map_name);
    bool SendDatagram(const std::vector<std::uint8_t>& packet);
    bool HasOutputTransport() const;
    std::span<const std::uint8_t> SigningSecret() const;

    int socket_fd_ = -1;
#if defined(__linux__)
    sockaddr_storage destination_{};
    socklen_t destination_length_ = 0;
#endif
    std::vector<std::uint8_t> shared_secret_;
    std::atomic<std::uint32_t> sequence_{0};
    std::atomic<std::uint64_t> dropped_packets_{0};
    std::uint32_t last_teleport_sequence_ = 0;
    std::uint32_t last_teleport_time_ = 0;

    std::mutex presence_mutex_;
    std::array<PlayerPresence, kTrackedPlayerSlots> players_{};
    std::string current_map_;
    std::chrono::steady_clock::time_point next_presence_broadcast_{};
    std::chrono::steady_clock::time_point next_position_frame_{};
    std::chrono::steady_clock::time_point map_started_at_{};
    std::chrono::steady_clock::time_point previous_health_sample_at_{};
    std::uint32_t previous_health_tick_ = 0;
    bool has_health_tick_sample_ = false;
    std::uint64_t previous_cpu_total_ = 0;
    std::uint64_t previous_cpu_idle_ = 0;
    bool has_cpu_sample_ = false;
};
