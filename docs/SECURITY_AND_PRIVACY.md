# Security and privacy

This software exposes live player microphone traffic to a remote administrator.
Use it only where monitoring is lawful and clearly disclosed to players.

Recommended deployment controls:

- Put the game server and Windows listener on WireGuard, Tailscale, or another encrypted private network.
- Give every administrator a separate account and access key, and revoke an
  account when that person's access ends.
- For direct Internet access, expose only `UDP 27122` on the game server and
  forward it to the CS2 host. Never expose SQLite files or SSH through this rule.
- Do not port-forward client UDP port `27120`; client traffic is initiated
  outbound and replies return to that connection.
- Use a DNS/DDNS hostname rather than repeatedly distributing a changing server
  public IP address.
- Keep recording disabled. This prototype performs live playback and does not save audio.
- Restrict physical access to administrator computers and phones.

Every command and server packet is authenticated with the administrator's
access key. Unknown source IP addresses do not receive an administrator session.
Authentication prevents unauthenticated command injection and detects packet
modification; it does not hide voice, chat, or command contents from someone
able to observe the network path.
