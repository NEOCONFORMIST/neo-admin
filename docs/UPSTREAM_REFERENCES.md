# Upstream references used for the prototype

The server patch was designed against these upstream sources as they existed on
2026-08-02:

- Source2ZE/CS2Fixes tested commit:
  `https://github.com/Source2ZE/CS2Fixes/commit/b57591c884ac0d1f214d9710422e72b88354ca2e`
- Current CS2Fixes voice hook:
  `src/cs2fixes.cpp`, `Hook_ProcessVoiceData`
- Current CS2Fixes server-side client declaration:
  `src/cs2_sdk/serversideclient.h`
- AlliedModders HL2SDK CS2 network message wrapper:
  `https://github.com/alliedmodders/hl2sdk/blob/cs2/public/networksystem/netmessage.h`
- CS2 network protobuf definitions:
  `https://github.com/SteamDatabase/Protobufs/blob/master/csgo/netmessages.proto`
- Metamod:Source Source 2 sample plugin:
  `https://github.com/alliedmodders/metamod-source/tree/master/samples/s2_sample_mm`

Native CS2 interfaces are not stable APIs. Revalidate the hook after major CS2 or
Metamod updates before deploying a newly rebuilt server binary.
