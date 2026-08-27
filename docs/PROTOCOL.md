# NEO ADMIN protocol 1.1

Transport is one authenticated UDP datagram per captured CS2 voice message.
All integer and floating-point fields are little-endian.

## Packet layout

| Offset | Type | Field |
|---:|---|---|
| 0 | 4 bytes | ASCII magic `CVB1` |
| 4 | u8 | protocol version (`1`) |
| 5 | u8 | message type (`1` = voice) |
| 6 | u8 | CS2 audio format (`0` Steam, `1` Engine, `2` Opus, `10` PCM test) |
| 7 | u8 | flags |
| 8 | u32 | bridge sequence |
| 12 | u32 | CS2 tick |
| 16 | u64 | SteamID64 |
| 24 | i32 | player slot |
| 28 | u32 | sample rate |
| 32 | i32 | CS2 sequence bytes |
| 36 | u32 | section number |
| 40 | u32 | uncompressed sample offset |
| 44 | u32 | number of encoded packets |
| 48 | u16 | UTF-8 player-name length |
| 50 | u16 | packet-offset count |
| 52 | u32 | encoded payload length |
| 56 | f32 | voice level |
| 60 | bytes | UTF-8 player name |
| variable | u32[] | packet offsets |
| variable | bytes | encoded voice payload |
| final 32 | bytes | HMAC-SHA256 over every preceding byte |

The HMAC key is the UTF-8 value of `VOICEBRIDGE_SECRET` / `SharedSecret`.
HMAC authenticates packets but does not encrypt their contents. Use a VPN such as
WireGuard or Tailscale when the server and listener communicate over the public Internet.

## Compatibility negotiation

The fixed wire-header version remains `1`. After an administrator session is
authenticated, current clients send admin action `52` (`RequestCapabilities`).
Current servers reply with message type `27` (`ServerCapabilities`):

| Packet field | Meaning |
|---|---|
| `tick` | protocol major version |
| `sample_rate` | protocol minor version |
| `steam_id` | 64-bit capability flags |
| `player_name` | server build ID |
| `payload` | comma-separated capability names |

The currently advertised flags are multi-session support, player-state deltas,
asynchronous outbound transport, server health, map overviews, voice relay,
SQLite persistence, and fail-soft engine compatibility. Older clients never
request the message, so they continue to receive only protocol types they know.

New wire behavior must be additive, must have a capability flag, and must remain
optional until the client confirms that capability.
