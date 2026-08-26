# Third-party notices

## Source2ZE/CS2Fixes

The server integration patch targets Source2ZE/CS2Fixes and follows its current
`CServerSideClient::ProcessVoiceData` hook pattern. CS2Fixes is licensed under GPL-3.0.
The tested upstream commit is `b57591c884ac0d1f214d9710422e72b88354ca2e`.

## AlliedModders Metamod:Source and HL2SDK

The build uses Metamod:Source and the CS2 branch of AlliedModders HL2SDK.
Refer to those projects for their respective licenses.

## Concentus

The Windows listener references Concentus 2.2.2 for managed Opus decoding.

## NAudio

The Windows listener references NAudio 2.3.0 for Windows audio playback.

## zm_lila_panic_371

The optional Zombie Survival map is downloaded directly by the CS2 server from
Steam Workshop item `3484400725`; the map VPK is not redistributed in this
package. The packaged Windows overview was generated from that map's collision
geometry for administrative display and coordinate calibration.
## SQLite

NEO ADMIN embeds the official SQLite amalgamation, version 3.53.4, from
https://sqlite.org/2026/sqlite-amalgamation-3530400.zip. SQLite is dedicated to
the public domain. The downloaded archive SHA3-256 is
`628a44cfe82c66aed1ccbbe85a562d2e33ebe64b3288981ed76285612227934e`.
