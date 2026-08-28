# NEO ADMIN

NEO ADMIN is a remote administration system for Counter-Strike 2. It combines
a native Linux/Metamod server plugin with Windows and Android clients for
monitoring players and controlling a server from a desktop or phone.

## Features

- **Live player monitoring:** See the players and bots actually connected to
  the server, including team, health, slot, alive state, voice activity, and
  Steam identity when available. SourceTV remains visible as the voice conduit.
- **Live map overview:** Track player positions on the current radar from the
  Windows or Android client. Mobile overviews are downloaded from the server,
  and authorized administrators can drag an alive player to a new position.
- **Remote voice and chat:** Hear in-game voice, use push-to-talk from Windows
  or Android, mute player playback locally, and send clearly identified
  administrator chat messages.
- **Player moderation:** Kick, slay, respawn, change teams, move to spectator,
  give approved weapons or items, and inspect detailed Steam identifiers.
- **Ban, mute, and gag management:** Create temporary or permanent actions with
  reasons and expiration dates, search current restrictions, unban players,
  and review discipline history by SteamID64.
- **Match and bot controls:** Restart rounds or matches, end warmup, pause or
  unpause, swap teams, and add or remove bots without opening the game console.
- **Maps and announcements:** Select maps, maintain rotations, schedule map
  changes, install or switch Workshop maps, and send immediate or scheduled
  server announcements.
- **Authenticated server console:** Run permitted server console commands from
  the dedicated Windows or Android console view.
- **Independent permissions:** Manage Windows/mobile administrator accounts
  separately from in-game administrators, with per-action permission levels
  and first-owner setup for a fresh installation.
- **Concurrent remote sessions:** Multiple phones and Windows clients can stay
  connected and operate together with separate administrator identities.
- **Audit and persistence:** Administrative actions, accounts, bans,
  restrictions, and operations are stored in a server-side SQLite database and
  exposed only through the authenticated NEO ADMIN protocol.
- **CounterStrikeSharp coexistence:** Designed to run beside
  CounterStrikeSharp on a Metamod-enabled CS2 server.

## Build From Source

### Server plugin

Install Docker Desktop or Docker Engine, enter the `server` directory, and run:

```bash
docker compose up --build --abort-on-container-exit --exit-code-from cs2fixes-build
```

The CS2 payload is written below `server/dockerbuild/package/cs2`.
The Docker build obtains its own SDK and Metamod build dependencies. They are
not included in the NEO ADMIN drag-and-drop binary release; server owners must
install Metamod:Source separately.

### Windows application

Install the .NET 8 SDK and run from the source root:

```powershell
dotnet publish windows/NEO.Admin/NEO.Admin.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o dist/windows-x64
```

Create `appsettings.json` from `windows/NEO.Admin/appsettings.example.json`.
Do not place real access profiles, shared secrets, or server addresses in a
redistributable build.

### Android application

Install the .NET 9 SDK with the Android workload, a compatible Android SDK, and
a JDK. Then run from the source root:

```powershell
.\scripts\build_android.ps1
```

The signed APK is written to `dist/android/NEO-ADMIN-Android.apk`. On the first
build, the script creates a private signing key under
`%LOCALAPPDATA%/NEO ADMIN/android-signing`. Keep that key private and backed up;
Android requires future updates to use the same signing certificate.

## Downloads And Installation

Prebuilt server, Windows, and Android packages are available on the
[Releases](https://github.com/NEOCONFORMIST/neo-admin/releases) page. For a new
server, follow [First-install instructions](docs/RELEASE_FIRST_INSTALL.md).

Metamod:Source must be installed separately. Stop the CS2 server before
replacing the plugin binary.

## Security And Privacy

NEO ADMIN can carry live player voice and administrative commands. Use it only
where monitoring is lawful and disclosed to players. Review
[Security and privacy](docs/SECURITY_AND_PRIVACY.md) before exposing the admin
port to the Internet.
