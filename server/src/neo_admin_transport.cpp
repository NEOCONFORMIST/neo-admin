#include "neo_admin_transport.h"

#include "voicebridge_protocol.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <sys/socket.h>

namespace neo_admin::transport
{
namespace
{
constexpr std::size_t kMaximumPendingPackets = 512;

struct OwnedPacketData
{
    std::uint8_t message_type = voicebridge::kMessageVoice;
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
    std::string player_name;
    std::vector<std::uint32_t> packet_offsets;
    std::vector<std::uint8_t> payload;

    voicebridge::VoicePacketData View() const
    {
        return {
            .message_type = message_type,
            .audio_format = audio_format,
            .flags = flags,
            .sequence = sequence,
            .tick = tick,
            .steam_id = steam_id,
            .player_slot = player_slot,
            .sample_rate = sample_rate,
            .sequence_bytes = sequence_bytes,
            .section_number = section_number,
            .uncompressed_sample_offset = uncompressed_sample_offset,
            .num_packets = num_packets,
            .voice_level = voice_level,
            .player_name = player_name,
            .packet_offsets = packet_offsets,
            .payload = payload,
        };
    }
};

struct OutboundJob
{
    sockaddr_storage destination{};
    socklen_t destination_length = 0;
    OwnedPacketData data;
    std::vector<std::uint8_t> secret;
};

std::mutex g_mutex;
std::condition_variable g_ready;
std::deque<OutboundJob> g_queue;
std::thread g_worker;
bool g_stopping = false;
int g_socket = -1;
std::atomic<std::uint64_t> g_queued{0};
std::atomic<std::uint64_t> g_sent{0};
std::atomic<std::uint64_t> g_dropped{0};
std::atomic<std::uint64_t> g_coalesced{0};

bool SameEndpoint(const OutboundJob& left, const OutboundJob& right)
{
    if (left.destination_length != right.destination_length ||
        left.destination.ss_family != right.destination.ss_family)
    {
        return false;
    }
    return std::memcmp(
               &left.destination,
               &right.destination,
               left.destination_length) == 0;
}

OwnedPacketData Own(const voicebridge::VoicePacketData& data)
{
    OwnedPacketData owned{
        .message_type = data.message_type,
        .audio_format = data.audio_format,
        .flags = data.flags,
        .sequence = data.sequence,
        .tick = data.tick,
        .steam_id = data.steam_id,
        .player_slot = data.player_slot,
        .sample_rate = data.sample_rate,
        .sequence_bytes = data.sequence_bytes,
        .section_number = data.section_number,
        .uncompressed_sample_offset = data.uncompressed_sample_offset,
        .num_packets = data.num_packets,
        .voice_level = data.voice_level,
        .player_name = std::string(data.player_name),
        .packet_offsets = {},
        .payload = {},
    };
    owned.packet_offsets.assign(
        data.packet_offsets.begin(), data.packet_offsets.end());
    owned.payload.assign(data.payload.begin(), data.payload.end());
    return owned;
}

void ClearSecret(std::vector<std::uint8_t>& secret)
{
    std::fill(secret.begin(), secret.end(), 0);
    secret.clear();
}

void Run()
{
    for (;;)
    {
        OutboundJob job;
        int socket_fd = -1;
        {
            std::unique_lock lock(g_mutex);
            g_ready.wait(lock, [] { return g_stopping || !g_queue.empty(); });
            if (g_stopping && g_queue.empty())
                break;
            job = std::move(g_queue.front());
            g_queue.pop_front();
            socket_fd = g_socket;
        }

        const std::vector<std::uint8_t> packet =
            voicebridge::BuildAuthenticatedVoicePacket(
                job.data.View(), job.secret);
        ClearSecret(job.secret);
        if (socket_fd < 0 || packet.empty())
        {
            ++g_dropped;
            continue;
        }

        const ssize_t sent = ::sendto(
            socket_fd,
            packet.data(),
            packet.size(),
            MSG_DONTWAIT,
            reinterpret_cast<const sockaddr*>(&job.destination),
            job.destination_length);
        if (sent == static_cast<ssize_t>(packet.size()))
            ++g_sent;
        else
            ++g_dropped;
    }
}
} // namespace

bool Start(int socket_fd)
{
    if (socket_fd < 0)
        return false;
    std::scoped_lock lock(g_mutex);
    if (g_worker.joinable())
        return g_socket == socket_fd;
    g_socket = socket_fd;
    g_stopping = false;
    g_queue.clear();
    g_queued = 0;
    g_sent = 0;
    g_dropped = 0;
    g_coalesced = 0;
    g_worker = std::thread(Run);
    return true;
}

void Stop()
{
    {
        std::scoped_lock lock(g_mutex);
        if (!g_worker.joinable())
        {
            g_socket = -1;
            return;
        }
        g_stopping = true;
    }
    g_ready.notify_all();
    g_worker.join();

    std::scoped_lock lock(g_mutex);
    for (OutboundJob& job : g_queue)
        ClearSecret(job.secret);
    g_queue.clear();
    g_socket = -1;
    g_stopping = false;
}

bool Enqueue(
    const sockaddr_storage& destination,
    socklen_t destination_length,
    const voicebridge::VoicePacketData& data,
    std::span<const std::uint8_t> shared_secret)
{
    if (destination_length == 0 || shared_secret.empty())
        return false;

    OutboundJob job{
        .destination = destination,
        .destination_length = destination_length,
        .data = Own(data),
        .secret = std::vector<std::uint8_t>(
            shared_secret.begin(), shared_secret.end()),
    };

    {
        std::scoped_lock lock(g_mutex);
        if (!g_worker.joinable() || g_stopping)
        {
            ClearSecret(job.secret);
            return false;
        }

        if (data.message_type == voicebridge::kMessagePlayerPosition)
        {
            const auto pending = std::find_if(
                g_queue.rbegin(),
                g_queue.rend(),
                [&](const OutboundJob& queued)
                {
                    return queued.data.message_type ==
                               voicebridge::kMessagePlayerPosition &&
                           queued.data.player_slot == data.player_slot &&
                           SameEndpoint(queued, job);
                });
            if (pending != g_queue.rend())
            {
                ClearSecret(pending->secret);
                *pending = std::move(job);
                ++g_coalesced;
                return true;
            }
        }

        if (g_queue.size() >= kMaximumPendingPackets)
        {
            const auto disposable = std::find_if(
                g_queue.begin(),
                g_queue.end(),
                [](const OutboundJob& queued)
                {
                    return queued.data.message_type ==
                        voicebridge::kMessagePlayerPosition;
                });
            if (disposable == g_queue.end())
            {
                ClearSecret(job.secret);
                ++g_dropped;
                return false;
            }
            ClearSecret(disposable->secret);
            g_queue.erase(disposable);
            ++g_dropped;
        }

        g_queue.push_back(std::move(job));
        ++g_queued;
    }
    g_ready.notify_one();
    return true;
}

Stats GetStats()
{
    std::scoped_lock lock(g_mutex);
    return {
        .queued = g_queued.load(),
        .sent = g_sent.load(),
        .dropped = g_dropped.load(),
        .coalesced = g_coalesced.load(),
        .pending = g_queue.size(),
    };
}
} // namespace neo_admin::transport
