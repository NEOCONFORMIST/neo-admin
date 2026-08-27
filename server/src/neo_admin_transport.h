#pragma once

#include <cstddef>
#include <cstdint>
#include <span>

#include <sys/socket.h>

namespace voicebridge
{
struct VoicePacketData;
}

namespace neo_admin::transport
{
struct Stats
{
    std::uint64_t queued = 0;
    std::uint64_t sent = 0;
    std::uint64_t dropped = 0;
    std::uint64_t coalesced = 0;
    std::size_t pending = 0;
};

bool Start(int socket_fd);
void Stop();

// Deep-copies packet data before returning. HMAC generation and sendto run on
// the worker, so no engine object or temporary view crosses the thread boundary.
bool Enqueue(
    const sockaddr_storage& destination,
    socklen_t destination_length,
    const voicebridge::VoicePacketData& data,
    std::span<const std::uint8_t> shared_secret);

Stats GetStats();
} // namespace neo_admin::transport
