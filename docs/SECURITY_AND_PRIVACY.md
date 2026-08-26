# Security and privacy

This software exposes live player microphone traffic to a remote administrator.
Use it only where monitoring is lawful and clearly disclosed to players.

Recommended deployment controls:

- Put the game server and Windows listener on WireGuard, Tailscale, or another encrypted private network.
- Set `AllowedServerIp` in the Windows configuration.
- Restrict the Windows UDP firewall rule to the CS2 server's IP address.
- Use a random shared secret of at least 32 characters.
- Do not expose UDP port 27120 broadly to the Internet.
- Keep recording disabled. This prototype performs live playback and does not save audio.
- Restrict access to the Windows machine and rotate the shared secret after staff changes.

The protocol's HMAC prevents unauthenticated packet injection and detects modification.
It does not hide voice contents from someone able to observe the network path.
