# NEO ADMIN fresh installation

This release contains no administrator accounts, access keys, SQLite database,
audit history, bans, discipline history, server logs, SSH keys, IP addresses, or
deployment settings from the development server.

## Linux CS2 server

### Prerequisite

Install and verify a current Metamod:Source release separately. This package
does not contain Metamod binaries, source code, a downloader, or an installer,
and it does not modify `gameinfo.gi`.

### Drag-and-drop install

1. Stop the CS2 dedicated server.
2. Open `server/DRAG-INTO-game-csgo` from this release.
3. Drag everything inside that folder into the server's existing `game/csgo`
   folder. Allow folders to merge and replace the old NEO ADMIN plugin files
   when updating.
4. Allow inbound UDP port `27122` on the Linux host. For direct Internet
   access, forward router/firewall UDP port `27122` to this CS2 server.
5. Restart CS2 and run `meta list` in its console. NEO ADMIN should appear.
6. Find the one-time first-owner setup code in the startup console output.

The included `addons/metamod/cs2fixes.vdf` file registers NEO ADMIN with an
already installed Metamod. It is part of NEO ADMIN, not a bundled Metamod
runtime.

### CounterStrikeSharp compatibility

NEO ADMIN and CounterStrikeSharp are separate Metamod plugins and may be loaded
together. When CounterStrikeSharp is already installed, `meta list` should show
both plugins after startup.

Some CounterStrikeSharp Linux releases mark their loader as requiring an
executable stack. Newer Linux hosts can reject that loader before either plugin
has a chance to interact. The preferred server configuration is Steam Linux
Runtime 3 (sniper). This release also includes
`server/prepare-counterstrikesharp.sh` for hosts that launch the dedicated
server directly:

```bash
sudo bash ./server/prepare-counterstrikesharp.sh --cs2-root /path/to/cs2
```

The tool requires `patchelf` 0.18 or newer, refuses to modify a running server,
creates a timestamped backup, clears only CounterStrikeSharp's executable-stack
ELF flag, and verifies the result. Run it again after updating
CounterStrikeSharp because its loader may have been replaced.

## Windows application

1. Extract the `windows` directory on the administrator computer.
2. Run `NEO ADMIN.exe`.
3. Select **File > Initial Server Setup**.
4. Enter the CS2 server address, UDP port `27122`, and the one-time setup code.
5. Choose the first Owner account name and save the generated access profile.

The first successful claim permanently disables initial setup. Additional
Windows and in-game administrators can then be created from their separate
account-management screens.

No fixed Windows IP or shared secret is required. After authentication, map,
player, chat, voice, and administration traffic returns through the same UDP
connection initiated by the Windows app.

## Remote and mobile administrators

Install `android/NEO-ADMIN-Android-<version>.apk` on Android 8.0 or newer. The
same APK is also published separately beside the release archives for easier
phone installation.

- In each client, save the server's public IPv4 address or DNS/DDNS name, not
  the administrator device's address. Keep the NEO ADMIN UDP port at `27122`.
- The server accepts authenticated clients from changing public IP addresses.
  Each phone or computer gets an independent session, and several clients can
  be connected at the same time.
- Forward only `UDP 27122` to the CS2 server's private address. Do not forward
  the client receive port `27120`.
- A router without NAT loopback may require a separate LAN profile while the
  device is at home. Outside the LAN, use the public address or DNS name.
- A VPN such as WireGuard or Tailscale is preferred because protocol
  authentication prevents forged commands but does not encrypt voice or chat.

## Updates and backups

- Stop the server, then repeat the drag-and-drop copy to update NEO ADMIN.
- Existing `neo_admin.sqlite3` data is preserved.
- Back up the database with SQLite's backup API or while CS2 is stopped.
- CS2 updates may replace `gameinfo.gi`; repair or reinstall Metamod separately
  if `meta list` no longer works.
- The `SOURCE` archive beside this release contains the corresponding source
  and build instructions.
