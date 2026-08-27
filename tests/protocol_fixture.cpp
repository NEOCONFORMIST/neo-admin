#include "voicebridge_protocol.h"

#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

int main(int argc, char** argv)
{
    if (argc != 3)
    {
        std::cerr <<
            "usage: protocol_fixture VOICE_OUTPUT HEALTH_OUTPUT\n";
        return 2;
    }

    const std::string secret = "0123456789abcdef0123456789abcdef";
    const std::string name = "Fixture Player";
    const std::vector<std::uint32_t> offsets = {0, 3};
    const std::vector<std::uint8_t> payload = {0x01, 0x02, 0x03, 0x11, 0x12};

    const voicebridge::VoicePacketData data{
        .audio_format = 2,
        .flags = 7,
        .sequence = 1234,
        .tick = 5678,
        .steam_id = 76561198012345678ULL,
        .player_slot = 9,
        .sample_rate = 48000,
        .sequence_bytes = 44,
        .section_number = 3,
        .uncompressed_sample_offset = 960,
        .num_packets = 2,
        .voice_level = 0.75f,
        .player_name = name,
        .packet_offsets = offsets,
        .payload = payload,
    };

    const auto packet = voicebridge::BuildAuthenticatedVoicePacket(
        data,
        std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(secret.data()), secret.size()));
    if (packet.empty())
        return 3;

    std::ofstream output(argv[1], std::ios::binary);
    output.write(
        reinterpret_cast<const char*>(packet.data()),
        static_cast<std::streamsize>(packet.size()));
    if (!output.good())
        return 4;

    auto FloatBits = [](float value) -> std::uint32_t {
        std::uint32_t bits = 0;
        std::memcpy(&bits, &value, sizeof(bits));
        return bits;
    };

    const std::string version = "DEV-health-test";
    const voicebridge::VoicePacketData health_data{
        .message_type = voicebridge::kMessageServerHealth,
        .audio_format = 0,
        .flags = 0,
        .sequence = 4321,
        .tick = 9876,
        .steam_id = 3661,
        .player_slot = 3,
        .sample_rate = 64,
        .sequence_bytes = static_cast<std::int32_t>(FloatBits(64.0F)),
        .section_number = FloatBits(23.5F),
        .uncompressed_sample_offset = FloatBits(47.25F),
        .num_packets = 2,
        .voice_level = 0.0F,
        .player_name = version,
        .packet_offsets = {},
        .payload = {},
    };

    const auto health_packet =
        voicebridge::BuildAuthenticatedVoicePacket(
            health_data,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(secret.data()),
                secret.size()));
    if (health_packet.empty())
        return 5;

    std::ofstream health_output(argv[2], std::ios::binary);
    health_output.write(
        reinterpret_cast<const char*>(health_packet.data()),
        static_cast<std::streamsize>(health_packet.size()));
    if (!health_output.good())
        return 6;

    const std::string account_id = "owner";
    const std::string operator_name = "Neo Conform";
    const std::string access_selector =
        voicebridge::BuildAdminAccessSelector(
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(secret.data()),
                secret.size()));
    if (access_selector.size() != 32 ||
        !access_selector.starts_with("key_"))
    {
        return 7;
    }
    const voicebridge::VoicePacketData login_data{
        .message_type = voicebridge::kMessageAdminLoginCommand,
        .sequence = 777,
        .tick = 1787529600,
        .player_slot = -1,
        .player_name = operator_name,
        .packet_offsets = {},
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(account_id.data()),
            account_id.size()),
    };
    const auto login_packet = voicebridge::BuildAuthenticatedVoicePacket(
        login_data,
        std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(secret.data()),
            secret.size()));

    std::string claimed_id;
    voicebridge::AdminLoginCommandData parsed_login{};
    if (!voicebridge::TryReadAdminLoginAccountId(login_packet, claimed_id) ||
        claimed_id != account_id ||
        !voicebridge::TryParseAuthenticatedAdminLoginCommand(
            login_packet,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(secret.data()),
                secret.size()),
            parsed_login) ||
        parsed_login.account_id != account_id ||
        parsed_login.display_name != operator_name ||
        parsed_login.sequence != 777)
    {
        return 9;
    }

    const voicebridge::VoicePacketData legacy_login_data{
        .message_type = voicebridge::kMessageAdminLoginCommand,
        .sequence = 776,
        .tick = 1787529599,
        .player_slot = -1,
        .player_name = {},
        .packet_offsets = {},
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(account_id.data()),
            account_id.size()),
    };
    const auto legacy_login_packet =
        voicebridge::BuildAuthenticatedVoicePacket(
            legacy_login_data,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(secret.data()),
                secret.size()));
    if (!voicebridge::TryParseAuthenticatedAdminLoginCommand(
            legacy_login_packet,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(secret.data()),
                secret.size()),
            parsed_login) ||
        !parsed_login.display_name.empty())
    {
        return 11;
    }

    auto tampered_login = login_packet;
    tampered_login[voicebridge::kHeaderSize] ^= 0x01U;
    if (voicebridge::TryParseAuthenticatedAdminLoginCommand(
            tampered_login,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(secret.data()),
                secret.size()),
            parsed_login))
    {
        return 10;
    }

    const std::string setup_code =
        "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ";
    const std::string first_owner_name = "First Owner";
    const std::string first_owner_id = "first.owner";
    const std::string first_owner_key =
        "first-owner-access-key-0123456789-ABCDEFG";
    const std::string first_owner_payload =
        first_owner_id + "\n" + first_owner_key;
    const voicebridge::VoicePacketData claim_data{
        .message_type = voicebridge::kMessageFirstOwnerClaim,
        .sequence = 778,
        .tick = 1787529601,
        .player_slot = -1,
        .player_name = first_owner_name,
        .packet_offsets = {},
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(
                first_owner_payload.data()),
            first_owner_payload.size()),
    };
    const auto claim_packet = voicebridge::BuildAuthenticatedVoicePacket(
        claim_data,
        std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(setup_code.data()),
            setup_code.size()));

    voicebridge::FirstOwnerClaimData parsed_claim{};
    if (!voicebridge::TryReadFirstOwnerClaim(
            claim_packet,
            parsed_claim) ||
        parsed_claim.display_name != first_owner_name ||
        parsed_claim.account_id != first_owner_id ||
        parsed_claim.access_key != first_owner_key ||
        !voicebridge::TryParseAuthenticatedFirstOwnerClaim(
            claim_packet,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(setup_code.data()),
                setup_code.size()),
            parsed_claim) ||
        parsed_claim.sequence != 778)
    {
        return 9;
    }

    auto tampered_claim = claim_packet;
    tampered_claim[voicebridge::kHeaderSize] ^= 0x01U;
    if (voicebridge::TryParseAuthenticatedFirstOwnerClaim(
            tampered_claim,
            std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(setup_code.data()),
                setup_code.size()),
            parsed_claim))
    {
        return 10;
    }

    return 0;
}
