#include "voicebridge_protocol.h"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>

namespace voicebridge
{
namespace
{
constexpr std::array<std::uint32_t, 64> kSha256Constants = {
    0x428a2f98U, 0x71374491U, 0xb5c0fbcfU, 0xe9b5dba5U, 0x3956c25bU, 0x59f111f1U, 0x923f82a4U, 0xab1c5ed5U,
    0xd807aa98U, 0x12835b01U, 0x243185beU, 0x550c7dc3U, 0x72be5d74U, 0x80deb1feU, 0x9bdc06a7U, 0xc19bf174U,
    0xe49b69c1U, 0xefbe4786U, 0x0fc19dc6U, 0x240ca1ccU, 0x2de92c6fU, 0x4a7484aaU, 0x5cb0a9dcU, 0x76f988daU,
    0x983e5152U, 0xa831c66dU, 0xb00327c8U, 0xbf597fc7U, 0xc6e00bf3U, 0xd5a79147U, 0x06ca6351U, 0x14292967U,
    0x27b70a85U, 0x2e1b2138U, 0x4d2c6dfcU, 0x53380d13U, 0x650a7354U, 0x766a0abbU, 0x81c2c92eU, 0x92722c85U,
    0xa2bfe8a1U, 0xa81a664bU, 0xc24b8b70U, 0xc76c51a3U, 0xd192e819U, 0xd6990624U, 0xf40e3585U, 0x106aa070U,
    0x19a4c116U, 0x1e376c08U, 0x2748774cU, 0x34b0bcb5U, 0x391c0cb3U, 0x4ed8aa4aU, 0x5b9cca4fU, 0x682e6ff3U,
    0x748f82eeU, 0x78a5636fU, 0x84c87814U, 0x8cc70208U, 0x90befffaU, 0xa4506cebU, 0xbef9a3f7U, 0xc67178f2U,
};

constexpr std::uint32_t RotateRight(std::uint32_t value, unsigned count)
{
    return (value >> count) | (value << (32U - count));
}

class Sha256
{
public:
    Sha256()
    {
        state_ = {
            0x6a09e667U, 0xbb67ae85U, 0x3c6ef372U, 0xa54ff53aU,
            0x510e527fU, 0x9b05688cU, 0x1f83d9abU, 0x5be0cd19U,
        };
    }

    void Update(std::span<const std::uint8_t> bytes)
    {
        for (std::uint8_t byte : bytes)
        {
            buffer_[buffer_size_++] = byte;
            if (buffer_size_ == buffer_.size())
            {
                Transform(buffer_.data());
                bit_length_ += 512U;
                buffer_size_ = 0;
            }
        }
    }

    std::array<std::uint8_t, 32> Final()
    {
        const std::uint64_t total_bits = bit_length_ + static_cast<std::uint64_t>(buffer_size_) * 8U;

        buffer_[buffer_size_++] = 0x80U;
        if (buffer_size_ > 56U)
        {
            while (buffer_size_ < 64U)
                buffer_[buffer_size_++] = 0;
            Transform(buffer_.data());
            buffer_size_ = 0;
        }

        while (buffer_size_ < 56U)
            buffer_[buffer_size_++] = 0;

        for (int index = 7; index >= 0; --index)
            buffer_[buffer_size_++] = static_cast<std::uint8_t>((total_bits >> (index * 8)) & 0xffU);

        Transform(buffer_.data());

        std::array<std::uint8_t, 32> digest{};
        for (std::size_t word = 0; word < state_.size(); ++word)
        {
            digest[word * 4 + 0] = static_cast<std::uint8_t>((state_[word] >> 24U) & 0xffU);
            digest[word * 4 + 1] = static_cast<std::uint8_t>((state_[word] >> 16U) & 0xffU);
            digest[word * 4 + 2] = static_cast<std::uint8_t>((state_[word] >> 8U) & 0xffU);
            digest[word * 4 + 3] = static_cast<std::uint8_t>(state_[word] & 0xffU);
        }
        return digest;
    }

private:
    void Transform(const std::uint8_t* block)
    {
        std::array<std::uint32_t, 64> schedule{};
        for (std::size_t index = 0; index < 16; ++index)
        {
            const std::size_t offset = index * 4;
            schedule[index] =
                (static_cast<std::uint32_t>(block[offset]) << 24U) |
                (static_cast<std::uint32_t>(block[offset + 1]) << 16U) |
                (static_cast<std::uint32_t>(block[offset + 2]) << 8U) |
                static_cast<std::uint32_t>(block[offset + 3]);
        }

        for (std::size_t index = 16; index < schedule.size(); ++index)
        {
            const std::uint32_t s0 = RotateRight(schedule[index - 15], 7U) ^ RotateRight(schedule[index - 15], 18U) ^ (schedule[index - 15] >> 3U);
            const std::uint32_t s1 = RotateRight(schedule[index - 2], 17U) ^ RotateRight(schedule[index - 2], 19U) ^ (schedule[index - 2] >> 10U);
            schedule[index] = schedule[index - 16] + s0 + schedule[index - 7] + s1;
        }

        std::uint32_t a = state_[0];
        std::uint32_t b = state_[1];
        std::uint32_t c = state_[2];
        std::uint32_t d = state_[3];
        std::uint32_t e = state_[4];
        std::uint32_t f = state_[5];
        std::uint32_t g = state_[6];
        std::uint32_t h = state_[7];

        for (std::size_t index = 0; index < schedule.size(); ++index)
        {
            const std::uint32_t sigma1 = RotateRight(e, 6U) ^ RotateRight(e, 11U) ^ RotateRight(e, 25U);
            const std::uint32_t choose = (e & f) ^ ((~e) & g);
            const std::uint32_t temp1 = h + sigma1 + choose + kSha256Constants[index] + schedule[index];
            const std::uint32_t sigma0 = RotateRight(a, 2U) ^ RotateRight(a, 13U) ^ RotateRight(a, 22U);
            const std::uint32_t majority = (a & b) ^ (a & c) ^ (b & c);
            const std::uint32_t temp2 = sigma0 + majority;

            h = g;
            g = f;
            f = e;
            e = d + temp1;
            d = c;
            c = b;
            b = a;
            a = temp1 + temp2;
        }

        state_[0] += a;
        state_[1] += b;
        state_[2] += c;
        state_[3] += d;
        state_[4] += e;
        state_[5] += f;
        state_[6] += g;
        state_[7] += h;
    }

    std::array<std::uint32_t, 8> state_{};
    std::array<std::uint8_t, 64> buffer_{};
    std::size_t buffer_size_ = 0;
    std::uint64_t bit_length_ = 0;
};

std::array<std::uint8_t, 32> Sha256Digest(std::span<const std::uint8_t> input)
{
    Sha256 hash;
    hash.Update(input);
    return hash.Final();
}

std::array<std::uint8_t, 32> HmacSha256(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> message)
{
    constexpr std::size_t kBlockSize = 64;
    std::array<std::uint8_t, kBlockSize> normalized_key{};

    if (key.size() > kBlockSize)
    {
        const auto digest = Sha256Digest(key);
        std::copy(digest.begin(), digest.end(), normalized_key.begin());
    }
    else
    {
        std::copy(key.begin(), key.end(), normalized_key.begin());
    }

    std::array<std::uint8_t, kBlockSize> inner_pad{};
    std::array<std::uint8_t, kBlockSize> outer_pad{};
    for (std::size_t index = 0; index < kBlockSize; ++index)
    {
        inner_pad[index] = static_cast<std::uint8_t>(normalized_key[index] ^ 0x36U);
        outer_pad[index] = static_cast<std::uint8_t>(normalized_key[index] ^ 0x5cU);
    }

    Sha256 inner;
    inner.Update(inner_pad);
    inner.Update(message);
    const auto inner_digest = inner.Final();

    Sha256 outer;
    outer.Update(outer_pad);
    outer.Update(inner_digest);
    return outer.Final();
}

void AppendU8(std::vector<std::uint8_t>& output, std::uint8_t value)
{
    output.push_back(value);
}

void AppendU16(std::vector<std::uint8_t>& output, std::uint16_t value)
{
    output.push_back(static_cast<std::uint8_t>(value & 0xffU));
    output.push_back(static_cast<std::uint8_t>((value >> 8U) & 0xffU));
}

void AppendU32(std::vector<std::uint8_t>& output, std::uint32_t value)
{
    for (unsigned shift = 0; shift < 32U; shift += 8U)
        output.push_back(static_cast<std::uint8_t>((value >> shift) & 0xffU));
}

void AppendU64(std::vector<std::uint8_t>& output, std::uint64_t value)
{
    for (unsigned shift = 0; shift < 64U; shift += 8U)
        output.push_back(static_cast<std::uint8_t>((value >> shift) & 0xffU));
}

void AppendI32(std::vector<std::uint8_t>& output, std::int32_t value)
{
    AppendU32(output, static_cast<std::uint32_t>(value));
}

void AppendFloat(std::vector<std::uint8_t>& output, float value)
{
    static_assert(sizeof(float) == sizeof(std::uint32_t));

    std::uint32_t encoded = 0;
    std::memcpy(&encoded, &value, sizeof(encoded));
    AppendU32(output, encoded);
}
std::uint16_t ReadU16(
    std::span<const std::uint8_t> input,
    std::size_t offset)
{
    return static_cast<std::uint16_t>(input[offset]) |
        (static_cast<std::uint16_t>(input[offset + 1]) << 8U);
}

std::uint32_t ReadU32(
    std::span<const std::uint8_t> input,
    std::size_t offset)
{
    std::uint32_t value = 0;
    for (unsigned shift = 0; shift < 32U; shift += 8U)
    {
        value |= static_cast<std::uint32_t>(
            input[offset + shift / 8U]) << shift;
    }
    return value;
}

std::uint64_t ReadU64(
    std::span<const std::uint8_t> input,
    std::size_t offset)
{
    std::uint64_t value = 0;
    for (unsigned shift = 0; shift < 64U; shift += 8U)
    {
        value |= static_cast<std::uint64_t>(
            input[offset + shift / 8U]) << shift;
    }
    return value;
}

std::int32_t ReadI32(
    std::span<const std::uint8_t> input,
    std::size_t offset)
{
    return static_cast<std::int32_t>(ReadU32(input, offset));
}

float ReadFloat(
    std::span<const std::uint8_t> input,
    std::size_t offset)
{
    const std::uint32_t encoded = ReadU32(input, offset);
    float value = 0.0f;
    static_assert(sizeof(value) == sizeof(encoded));
    std::memcpy(&value, &encoded, sizeof(value));
    return value;
}

bool ConstantTimeEqual(
    std::span<const std::uint8_t> left,
    std::span<const std::uint8_t> right)
{
    if (left.size() != right.size())
        return false;

    std::uint8_t difference = 0;
    for (std::size_t index = 0; index < left.size(); ++index)
        difference |= static_cast<std::uint8_t>(left[index] ^ right[index]);

    return difference == 0;
}

} // namespace

std::vector<std::uint8_t> BuildAuthenticatedVoicePacket(
    const VoicePacketData& data,
    std::span<const std::uint8_t> shared_secret)
{
    if (shared_secret.empty() ||
        data.player_name.size() > std::numeric_limits<std::uint16_t>::max() ||
        data.packet_offsets.size() > std::numeric_limits<std::uint16_t>::max() ||
        data.payload.size() > std::numeric_limits<std::uint32_t>::max())
    {
        return {};
    }

    const std::size_t total_size = kHeaderSize + data.player_name.size() +
        data.packet_offsets.size() * sizeof(std::uint32_t) + data.payload.size() + kAuthTagSize;
    if (total_size > kMaxUdpDatagram)
        return {};

    std::vector<std::uint8_t> packet;
    packet.reserve(total_size);

    packet.insert(packet.end(), {'C', 'V', 'B', '1'});
    AppendU8(packet, kProtocolVersion);
    AppendU8(packet, data.message_type);
    AppendU8(packet, data.audio_format);
    AppendU8(packet, data.flags);
    AppendU32(packet, data.sequence);
    AppendU32(packet, data.tick);
    AppendU64(packet, data.steam_id);
    AppendI32(packet, data.player_slot);
    AppendU32(packet, data.sample_rate);
    AppendI32(packet, data.sequence_bytes);
    AppendU32(packet, data.section_number);
    AppendU32(packet, data.uncompressed_sample_offset);
    AppendU32(packet, data.num_packets);
    AppendU16(packet, static_cast<std::uint16_t>(data.player_name.size()));
    AppendU16(packet, static_cast<std::uint16_t>(data.packet_offsets.size()));
    AppendU32(packet, static_cast<std::uint32_t>(data.payload.size()));
    AppendFloat(packet, data.voice_level);

    if (packet.size() != kHeaderSize)
        return {};

    packet.insert(packet.end(), data.player_name.begin(), data.player_name.end());
    for (std::uint32_t offset : data.packet_offsets)
        AppendU32(packet, offset);
    packet.insert(packet.end(), data.payload.begin(), data.payload.end());

    const auto tag = HmacSha256(shared_secret, packet);
    packet.insert(packet.end(), tag.begin(), tag.end());
    return packet;
}

bool TryParseAuthenticatedTeleportCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    TeleportCommandData& command)
{
    constexpr std::size_t kCommandSize = kHeaderSize + kAuthTagSize;

    if (shared_secret.empty() || datagram.size() != kCommandSize)
        return false;

    if (datagram[0] != 'C' ||
        datagram[1] != 'V' ||
        datagram[2] != 'B' ||
        datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessageTeleportCommand)
    {
        return false;
    }

    // Teleport commands do not carry audio, names, offsets, or payloads.
    for (std::size_t index = 40; index < kHeaderSize; ++index)
    {
        if (datagram[index] != 0)
            return false;
    }

    const auto expected_tag = HmacSha256(
        shared_secret,
        datagram.first(kHeaderSize));

    if (!ConstantTimeEqual(
            expected_tag,
            datagram.subspan(kHeaderSize, kAuthTagSize)))
    {
        return false;
    }

    TeleportCommandData parsed{};
    parsed.sequence = ReadU32(datagram, 8);
    parsed.unix_time = ReadU32(datagram, 12);
    parsed.steam_id = ReadU64(datagram, 16);
    parsed.player_slot = ReadI32(datagram, 24);
    parsed.x = ReadFloat(datagram, 28);
    parsed.y = ReadFloat(datagram, 32);
    parsed.z = ReadFloat(datagram, 36);

    if (!std::isfinite(parsed.x) ||
        !std::isfinite(parsed.y) ||
        !std::isfinite(parsed.z))
    {
        return false;
    }

    command = parsed;
    return true;
}


bool TryParseAuthenticatedConnectCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    ConnectCommandData& command)
{
    constexpr std::size_t kCommandSize =
        kHeaderSize + kAuthTagSize;

    if (shared_secret.empty() ||
        datagram.size() != kCommandSize)
    {
        return false;
    }

    if (datagram[0] != 'C' ||
        datagram[1] != 'V' ||
        datagram[2] != 'B' ||
        datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessageConnectCommand ||
        datagram[6] != 0 ||
        datagram[7] != 0)
    {
        return false;
    }

    // CONNECT carries only sequence + timestamp.
    // All normal voice/player fields must be empty.
    if (ReadU64(datagram, 16) != 0 ||
        ReadI32(datagram, 24) != -1 ||
        ReadU32(datagram, 28) != 0 ||
        ReadI32(datagram, 32) != 0 ||
        ReadU32(datagram, 36) != 0 ||
        ReadU32(datagram, 40) != 0 ||
        ReadU32(datagram, 44) != 0 ||
        ReadU16(datagram, 48) != 0 ||
        ReadU16(datagram, 50) != 0 ||
        ReadU32(datagram, 52) != 0 ||
        ReadU32(datagram, 56) != 0)
    {
        return false;
    }

    const auto authenticated =
        datagram.first(kHeaderSize);

    const auto supplied_tag =
        datagram.subspan(
            kHeaderSize,
            kAuthTagSize);

    const auto expected_tag =
        HmacSha256(
            shared_secret,
            authenticated);

    if (!ConstantTimeEqual(
            expected_tag,
            supplied_tag))
    {
        return false;
    }

    ConnectCommandData parsed{};
    parsed.sequence = ReadU32(datagram, 8);
    parsed.unix_time = ReadU32(datagram, 12);

    command = parsed;
    return true;
}

bool TryReadAdminLoginAccountId(
    std::span<const std::uint8_t> datagram,
    std::string& account_id)
{
    constexpr std::size_t kMaxAccountIdBytes = 32;

    if (datagram.size() <= kHeaderSize + kAuthTagSize ||
        datagram[0] != 'C' || datagram[1] != 'V' ||
        datagram[2] != 'B' || datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessageAdminLoginCommand ||
        datagram[6] != 0 || datagram[7] != 0)
    {
        return false;
    }

    const std::uint32_t payload_length = ReadU32(datagram, 52);
    if (payload_length < 3 || payload_length > kMaxAccountIdBytes ||
        ReadU16(datagram, 48) != 0 || ReadU16(datagram, 50) != 0 ||
        datagram.size() != kHeaderSize + payload_length + kAuthTagSize ||
        ReadU64(datagram, 16) != 0 || ReadI32(datagram, 24) != -1 ||
        ReadU32(datagram, 28) != 0 || ReadI32(datagram, 32) != 0 ||
        ReadU32(datagram, 36) != 0 || ReadU32(datagram, 40) != 0 ||
        ReadU32(datagram, 44) != 0 || ReadU32(datagram, 56) != 0)
    {
        return false;
    }

    const auto payload = datagram.subspan(kHeaderSize, payload_length);
    for (const std::uint8_t byte : payload)
    {
        if (!((byte >= 'a' && byte <= 'z') ||
              (byte >= 'A' && byte <= 'Z') ||
              (byte >= '0' && byte <= '9') ||
              byte == '.' || byte == '_' || byte == '-'))
        {
            return false;
        }
    }

    account_id.assign(
        reinterpret_cast<const char*>(payload.data()),
        payload.size());
    return true;
}

bool TryParseAuthenticatedAdminLoginCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> account_secret,
    AdminLoginCommandData& command)
{
    std::string account_id;
    if (account_secret.empty() ||
        !TryReadAdminLoginAccountId(datagram, account_id))
    {
        return false;
    }

    const std::size_t authenticated_size = datagram.size() - kAuthTagSize;
    const auto expected_tag = HmacSha256(
        account_secret,
        datagram.first(authenticated_size));
    if (!ConstantTimeEqual(
            expected_tag,
            datagram.subspan(authenticated_size, kAuthTagSize)))
    {
        return false;
    }

    command.sequence = ReadU32(datagram, 8);
    command.unix_time = ReadU32(datagram, 12);
    command.account_id = std::move(account_id);
    return true;
}

bool TryReadFirstOwnerClaim(
    std::span<const std::uint8_t> datagram,
    FirstOwnerClaimData& command)
{
    if (datagram.size() <= kHeaderSize + kAuthTagSize ||
        datagram[0] != 'C' || datagram[1] != 'V' ||
        datagram[2] != 'B' || datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessageFirstOwnerClaim ||
        datagram[6] != 0 || datagram[7] != 0)
    {
        return false;
    }

    const std::uint16_t name_length = ReadU16(datagram, 48);
    const std::uint16_t offset_count = ReadU16(datagram, 50);
    const std::uint32_t payload_length = ReadU32(datagram, 52);
    if (name_length < 1 || name_length > 64 || offset_count != 0 ||
        payload_length < 36 || payload_length > 161 ||
        datagram.size() !=
            kHeaderSize + name_length + payload_length + kAuthTagSize ||
        ReadU64(datagram, 16) != 0 || ReadI32(datagram, 24) != -1 ||
        ReadU32(datagram, 28) != 0 || ReadI32(datagram, 32) != 0 ||
        ReadU32(datagram, 36) != 0 || ReadU32(datagram, 40) != 0 ||
        ReadU32(datagram, 44) != 0 || ReadU32(datagram, 56) != 0)
    {
        return false;
    }

    const auto name = datagram.subspan(kHeaderSize, name_length);
    if (std::any_of(
            name.begin(),
            name.end(),
            [](std::uint8_t byte) { return byte == 0 || byte < 0x20U; }))
    {
        return false;
    }

    const auto payload =
        datagram.subspan(kHeaderSize + name_length, payload_length);
    const auto separator = std::find(payload.begin(), payload.end(), '\n');
    if (separator == payload.end() ||
        std::find(separator + 1, payload.end(), '\n') != payload.end())
    {
        return false;
    }

    const std::size_t account_length =
        static_cast<std::size_t>(separator - payload.begin());
    const std::size_t key_length = payload.size() - account_length - 1;
    if (account_length < 3 || account_length > 32 ||
        key_length < 32 || key_length > 128)
    {
        return false;
    }

    for (std::size_t index = 0; index < account_length; ++index)
    {
        const std::uint8_t byte = payload[index];
        if (!((byte >= 'a' && byte <= 'z') ||
              (byte >= 'A' && byte <= 'Z') ||
              (byte >= '0' && byte <= '9') ||
              byte == '.' || byte == '_' || byte == '-'))
        {
            return false;
        }
    }
    if (std::any_of(
            separator + 1,
            payload.end(),
            [](std::uint8_t byte) { return byte <= 0x20U || byte > 0x7eU; }))
    {
        return false;
    }

    FirstOwnerClaimData parsed{};
    parsed.sequence = ReadU32(datagram, 8);
    parsed.unix_time = ReadU32(datagram, 12);
    parsed.display_name.assign(
        reinterpret_cast<const char*>(name.data()),
        name.size());
    parsed.account_id.assign(
        reinterpret_cast<const char*>(payload.data()),
        account_length);
    parsed.access_key.assign(
        reinterpret_cast<const char*>(
            payload.data() + account_length + 1),
        key_length);
    command = std::move(parsed);
    return true;
}

bool TryParseAuthenticatedFirstOwnerClaim(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> setup_secret,
    FirstOwnerClaimData& command)
{
    FirstOwnerClaimData parsed{};
    if (setup_secret.empty() || !TryReadFirstOwnerClaim(datagram, parsed))
        return false;

    const std::size_t authenticated_size = datagram.size() - kAuthTagSize;
    const auto expected_tag = HmacSha256(
        setup_secret,
        datagram.first(authenticated_size));
    if (!ConstantTimeEqual(
            expected_tag,
            datagram.subspan(authenticated_size, kAuthTagSize)))
    {
        return false;
    }

    command = std::move(parsed);
    return true;
}

// NEO CHAT STAGE 3S ADMIN COMMAND PARSER BEGIN
bool TryParseAuthenticatedAdminChatCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    AdminChatCommandData& command)
{
    constexpr std::size_t kMaxAdminChatBytes = 220;

    if (shared_secret.empty() ||
        datagram.size() <=
            kHeaderSize + kAuthTagSize)
    {
        return false;
    }

    if (datagram[0] != 'C' ||
        datagram[1] != 'V' ||
        datagram[2] != 'B' ||
        datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessageAdminChatCommand ||
        datagram[6] != 0 ||
        datagram[7] != 0)
    {
        return false;
    }

    const std::uint16_t name_length =
        ReadU16(datagram, 48);

    const std::uint16_t offset_count =
        ReadU16(datagram, 50);

    const std::uint32_t payload_length =
        ReadU32(datagram, 52);

    if (name_length != 0 ||
        offset_count != 0 ||
        payload_length == 0 ||
        payload_length > kMaxAdminChatBytes)
    {
        return false;
    }

    // Admin chat uses only:
    //
    //   sequence
    //   unix timestamp
    //   payload
    //
    // All voice/player fields must remain empty.
    if (ReadU64(datagram, 16) != 0 ||
        ReadI32(datagram, 24) != -1 ||
        ReadU32(datagram, 28) != 0 ||
        ReadI32(datagram, 32) != 0 ||
        ReadU32(datagram, 36) != 0 ||
        ReadU32(datagram, 40) != 0 ||
        ReadU32(datagram, 44) != 0 ||
        ReadU32(datagram, 56) != 0)
    {
        return false;
    }

    const std::size_t authenticated_size =
        kHeaderSize +
        static_cast<std::size_t>(
            payload_length);

    const std::size_t expected_size =
        authenticated_size +
        kAuthTagSize;

    if (datagram.size() != expected_size)
        return false;

    const auto expected_tag =
        HmacSha256(
            shared_secret,
            datagram.first(
                authenticated_size));

    if (!ConstantTimeEqual(
            expected_tag,
            datagram.subspan(
                authenticated_size,
                kAuthTagSize)))
    {
        return false;
    }

    const auto payload =
        datagram.subspan(
            kHeaderSize,
            payload_length);

    // No embedded terminators/newlines/control codes.
    //
    // Bytes >= 0x20 are preserved, including UTF-8.
    for (std::uint8_t byte : payload)
    {
        if (byte == 0 ||
            byte == '\r' ||
            byte == '\n' ||
            (byte < 0x20U && byte != '\t'))
        {
            return false;
        }
    }

    AdminChatCommandData parsed{};

    parsed.sequence =
        ReadU32(datagram, 8);

    parsed.unix_time =
        ReadU32(datagram, 12);

    parsed.message.assign(
        reinterpret_cast<const char*>(
            payload.data()),
        payload.size());

    command = parsed;
    return true;
}
// NEO CHAT STAGE 3S ADMIN COMMAND PARSER END


// NEO ADMIN CONTROL STAGE 3T PARSER BEGIN
bool TryParseAuthenticatedAdminActionCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    AdminActionCommandData& command)
{
    constexpr std::size_t kMaxActionTextBytes = 2048;

    if (shared_secret.empty() ||
        datagram.size() <
            kHeaderSize + kAuthTagSize)
    {
        return false;
    }

    if (datagram[0] != 'C' ||
        datagram[1] != 'V' ||
        datagram[2] != 'B' ||
        datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessageAdminActionCommand ||
        datagram[6] != 0 ||
        datagram[7] != 0)
    {
        return false;
    }

    const std::uint16_t name_length =
        ReadU16(datagram, 48);

    const std::uint16_t offset_count =
        ReadU16(datagram, 50);

    const std::uint32_t payload_length =
        ReadU32(datagram, 52);

    if (name_length != 0 ||
        offset_count != 0 ||
        payload_length >
            kMaxActionTextBytes)
    {
        return false;
    }

    // Stage 3T uses only:
    //
    // sequence
    // unix timestamp
    // target player slot
    // action code
    // one integer value
    // optional payload text
    //
    // Everything else remains zero.
    if (ReadU64(datagram, 16) != 0 ||
        ReadU32(datagram, 36) != 0 ||
        ReadU32(datagram, 40) != 0 ||
        ReadU32(datagram, 44) != 0 ||
        ReadU32(datagram, 56) != 0)
    {
        return false;
    }

    const std::size_t authenticated_size =
        kHeaderSize +
        static_cast<std::size_t>(
            payload_length);

    const std::size_t expected_size =
        authenticated_size +
        kAuthTagSize;

    if (datagram.size() != expected_size)
        return false;

    const auto expected_tag =
        HmacSha256(
            shared_secret,
            datagram.first(
                authenticated_size));

    if (!ConstantTimeEqual(
            expected_tag,
            datagram.subspan(
                authenticated_size,
                kAuthTagSize)))
    {
        return false;
    }

    const auto payload =
        datagram.subspan(
            kHeaderSize,
            payload_length);

    for (std::uint8_t byte : payload)
    {
        if (byte == 0 ||
            byte == '\r' ||
            byte == '\n' ||
            (byte < 0x20U &&
             byte != '\t'))
        {
            return false;
        }
    }

    AdminActionCommandData parsed{};

    parsed.sequence =
        ReadU32(datagram, 8);

    parsed.unix_time =
        ReadU32(datagram, 12);

    parsed.player_slot =
        ReadI32(datagram, 24);

    parsed.action =
        ReadU32(datagram, 28);

    parsed.value =
        ReadI32(datagram, 32);

    if (parsed.action == 0 ||
        parsed.action > 1000)
    {
        return false;
    }

    if (!payload.empty())
    {
        parsed.text.assign(
            reinterpret_cast<const char*>(
                payload.data()),
            payload.size());
    }

    command = parsed;
    return true;
}
// NEO ADMIN CONTROL STAGE 3T PARSER END


bool TryParseAuthenticatedPushToTalkCommand(
    std::span<const std::uint8_t> datagram,
    std::span<const std::uint8_t> shared_secret,
    PushToTalkCommandData& command)
{
    constexpr std::size_t kMaxOpusPacketBytes = 1275;

    if (shared_secret.empty() ||
        datagram.size() <= kHeaderSize + kAuthTagSize)
    {
        return false;
    }

    if (datagram[0] != 'C' ||
        datagram[1] != 'V' ||
        datagram[2] != 'B' ||
        datagram[3] != '1' ||
        datagram[4] != kProtocolVersion ||
        datagram[5] != kMessagePushToTalkCommand ||
        datagram[6] != 2 ||
        datagram[7] != 0)
    {
        return false;
    }

    const std::uint16_t name_length =
        static_cast<std::uint16_t>(datagram[48]) |
        (static_cast<std::uint16_t>(datagram[49]) << 8U);

    const std::uint16_t offset_count =
        static_cast<std::uint16_t>(datagram[50]) |
        (static_cast<std::uint16_t>(datagram[51]) << 8U);

    const std::uint32_t payload_length =
        ReadU32(datagram, 52);

    if (name_length != 0 ||
        offset_count != 0 ||
        payload_length == 0 ||
        payload_length > kMaxOpusPacketBytes)
    {
        return false;
    }

    const std::size_t authenticated_size =
        kHeaderSize +
        static_cast<std::size_t>(payload_length);

    const std::size_t expected_size =
        authenticated_size + kAuthTagSize;

    if (datagram.size() != expected_size)
        return false;

    const auto expected_tag =
        HmacSha256(
            shared_secret,
            datagram.first(authenticated_size));

    if (!ConstantTimeEqual(
            expected_tag,
            datagram.subspan(
                authenticated_size,
                kAuthTagSize)))
    {
        return false;
    }

    PushToTalkCommandData parsed{};

    parsed.sequence =
        ReadU32(datagram, 8);

    parsed.unix_time =
        ReadU32(datagram, 12);

    parsed.steam_id =
        ReadU64(datagram, 16);

    parsed.player_slot =
        ReadI32(datagram, 24);

    parsed.sample_rate =
        ReadU32(datagram, 28);

    parsed.sequence_bytes =
        ReadI32(datagram, 32);

    parsed.section_number =
        ReadU32(datagram, 36);

    parsed.uncompressed_sample_offset =
        ReadU32(datagram, 40);

    parsed.num_packets =
        ReadU32(datagram, 44);

    parsed.voice_level =
        ReadFloat(datagram, 56);

    if (parsed.steam_id != 0 ||
        parsed.player_slot != -1 ||
        parsed.sample_rate != 48000 ||
        parsed.sequence_bytes < 0 ||
        parsed.num_packets != 1 ||
        !std::isfinite(parsed.voice_level))
    {
        return false;
    }

    parsed.payload.assign(
        datagram.begin() + kHeaderSize,
        datagram.begin() +
            kHeaderSize +
            static_cast<std::size_t>(payload_length));

    command = parsed;
    return true;
}

} // namespace voicebridge
