#include "neo_admin_transport.h"
#include "voicebridge_protocol.h"

#include <array>
#include <cerrno>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <span>
#include <vector>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>

namespace
{
int Fail(const char* message)
{
    std::cerr << message << " (errno=" << errno << ": "
              << std::strerror(errno) << ")\n";
    return 1;
}
}

int main()
{
    const int receiver = ::socket(AF_INET, SOCK_DGRAM, 0);
    const int sender = ::socket(AF_INET, SOCK_DGRAM, 0);
    if (receiver < 0 || sender < 0)
        return Fail("socket creation failed");

    sockaddr_in receive_address{};
    receive_address.sin_family = AF_INET;
    receive_address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    receive_address.sin_port = 0;
    if (::bind(
            receiver,
            reinterpret_cast<const sockaddr*>(&receive_address),
            sizeof(receive_address)) != 0)
    {
        return Fail("receiver bind failed");
    }

    socklen_t receive_length = sizeof(receive_address);
    if (::getsockname(
            receiver,
            reinterpret_cast<sockaddr*>(&receive_address),
            &receive_length) != 0)
    {
        return Fail("receiver address lookup failed");
    }

    sockaddr_storage destination{};
    std::memcpy(&destination, &receive_address, sizeof(receive_address));
    constexpr std::array<std::uint8_t, 16> secret{
        0x4e, 0x45, 0x4f, 0x2d, 0x41, 0x44, 0x4d, 0x49,
        0x4e, 0x2d, 0x54, 0x45, 0x53, 0x54, 0x2d, 0x31,
    };
    constexpr std::array<std::uint8_t, 5> payload{1, 3, 5, 7, 9};
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageServerCapabilities,
        .sequence = 42,
        .tick = voicebridge::kProtocolMajor,
        .steam_id = voicebridge::kServerCapabilities,
        .player_slot = -1,
        .sample_rate = voicebridge::kProtocolMinor,
        .player_name = "NEO ADMIN transport test",
        .packet_offsets = {},
        .payload = payload,
    };
    const std::vector<std::uint8_t> expected =
        voicebridge::BuildAuthenticatedVoicePacket(data, secret);

    if (!neo_admin::transport::Start(sender))
        return Fail("transport worker did not start");
    if (!neo_admin::transport::Enqueue(
            destination, sizeof(receive_address), data, secret))
    {
        neo_admin::transport::Stop();
        return Fail("packet enqueue failed");
    }

    pollfd ready{.fd = receiver, .events = POLLIN, .revents = 0};
    if (::poll(&ready, 1, 2000) != 1 || (ready.revents & POLLIN) == 0)
    {
        neo_admin::transport::Stop();
        return Fail("transport worker did not deliver a packet");
    }

    std::array<std::uint8_t, voicebridge::kMaxUdpDatagram> received{};
    const ssize_t received_size =
        ::recv(receiver, received.data(), received.size(), 0);
    neo_admin::transport::Stop();
    ::close(sender);
    ::close(receiver);

    if (received_size != static_cast<ssize_t>(expected.size()) ||
        !std::equal(
            expected.begin(),
            expected.end(),
            received.begin()))
    {
        std::cerr << "worker packet differs from protocol builder output\n";
        return 1;
    }

    const neo_admin::transport::Stats stats = neo_admin::transport::GetStats();
    if (stats.queued != 1 || stats.sent != 1 || stats.dropped != 0)
    {
        std::cerr << "unexpected transport counters\n";
        return 1;
    }

    std::cout << "Outbound transport queue test passed\n";
    return 0;
}
