using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace NeoAdmin;

internal sealed class UdpVoiceReceiver : IAsyncDisposable
{
    private readonly AppConfig _config;
    private volatile byte[] _secret;
    private string _adminId;
    private readonly UdpClient _udp;

    // User-selected server target.
    // Supports a LAN IP, public IPv4 address, or DNS hostname.
    private volatile IPAddress[] _allowedServers =
        Array.Empty<IPAddress>();

    private volatile IPEndPoint? _pttEndpoint;

    // NEO ADMIN bidirectional authenticated command sender.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IPEndPoint? _serverEndpoint;
    private int _commandSequence = Environment.TickCount;
    private readonly object _healthSync = new();
    private readonly Dictionary<uint, long> _healthProbes = new();
    private bool _hasServerSequence;
    private uint _lastServerSequence;
    private ulong _receivedServerPackets;
    private ulong _missingServerPackets;
    private readonly CancellationTokenSource _stop = new();
    private Task? _receiveTask;
    private volatile AdminSession? _currentSession;
    private volatile VoicePacket? _serverCapabilities;

    public event Action<VoicePacket, IPEndPoint>? PacketReceived;
    public event Action<string>? StatusChanged;
    public event Action<AdminSession?>? AdminSessionChanged;

    public UdpVoiceReceiver(AppConfig config)
    {
        _config = config;
        _secret = Encoding.UTF8.GetBytes(config.SharedSecret);
        _adminId = config.AdminId;

        _udp = new UdpClient(
            new IPEndPoint(config.GetBindAddress(), config.Port));
    }

    public bool HasServerTarget =>
        _pttEndpoint is not null;

    public AdminSession? CurrentSession => _currentSession;
    public VoicePacket? ServerCapabilities => _serverCapabilities;
    public string CurrentAdminId => _adminId;
    public bool HasAdminCredentials =>
        _secret.Length >= 16 &&
        _adminId.Length is >= 3 and <= 32;

    public bool Can(AdminPermission permission) =>
        _currentSession?.Can(permission) == true;

    public void SetAdminCredentials(string accountId, string accessKey)
    {
        accountId = accountId.Trim();
        accessKey = accessKey.Trim();
        if (accountId.Length is < 3 or > 32 ||
            accountId.Any(ch =>
                !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException("Administrator account ID is invalid.");
        }
        if (accessKey.Length < 16)
            throw new InvalidDataException("Administrator access key is too short.");

        byte[] replacement = Encoding.UTF8.GetBytes(accessKey);
        byte[] previous = _secret;
        _secret = replacement;
        CryptographicOperations.ZeroMemory(previous);
        _adminId = accountId;
    }

    public string ServerTargetDisplay
    {
        get
        {
            IPEndPoint? endpoint = _pttEndpoint;

            return endpoint is null
                ? "not configured"
                : $"{endpoint.Address}:{endpoint.Port}";
        }
    }

    public void DisconnectServer()
    {
        _allowedServers =
            Array.Empty<IPAddress>();

        _pttEndpoint = null;

        // Require a fresh authenticated packet after
        // the next CONNECT before teleport is allowed.
        _serverEndpoint = null;
        _currentSession = null;
        _serverCapabilities = null;
        ResetHealthTracking();

        AdminSessionChanged?.Invoke(null);

        StatusChanged?.Invoke(
            "Disconnected from server.");
    }

    public async Task<IPEndPoint> ConfigureServerAsync(
        string serverAddress,
        int pttPort)
    {
        if (!HasAdminCredentials)
        {
            throw new InvalidOperationException(
                "No administrator access profile is configured. " +
                "Choose Settings > Initial Server Setup for a fresh server, " +
                "or Settings > Import Access Profile.");
        }

        serverAddress = serverAddress.Trim();

        if (serverAddress.Length == 0)
            throw new InvalidDataException(
                "Enter a server IP address or hostname.");

        if (pttPort is < 1 or > 65535)
            throw new InvalidDataException(
                "PTT port must be between 1 and 65535.");

        IPAddress[] addresses;

        if (IPAddress.TryParse(
                serverAddress,
                out IPAddress? literal))
        {
            if (literal.AddressFamily !=
                AddressFamily.InterNetwork)
            {
                throw new InvalidDataException(
                    "The current server bridge uses IPv4. " +
                    "Enter an IPv4 address or a hostname with an IPv4 address.");
            }

            addresses = new[] { literal };
        }
        else
        {
            IPAddress[] resolved =
                await Dns.GetHostAddressesAsync(
                    serverAddress);

            addresses = resolved
                .Where(address =>
                    address.AddressFamily ==
                    AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();

            if (addresses.Length == 0)
            {
                throw new InvalidDataException(
                    $"No IPv4 address was found for {serverAddress}.");
            }
        }

        IPEndPoint endpoint =
            new(addresses[0], pttPort);

        // Only authenticated traffic from the selected
        // server address will be accepted.
        _allowedServers = addresses;
        _pttEndpoint = endpoint;

        // Force teleport to wait for a fresh authenticated
        // packet from this newly selected server.
        _serverEndpoint = null;
        _currentSession = null;
        ResetHealthTracking();
        AdminSessionChanged?.Invoke(null);

        // Permission-aware login is sent from the SAME UdpClient
        // that receives server traffic. Therefore the CS2
        // server learns the real source IP + UDP port seen
        // through LAN routing or NAT.
        byte[] connectDatagram =
            BridgeCommandPacket.BuildAdminLogin(
                NextCommandSequence(),
                _adminId,
                _config.AdminDisplayName,
                _secret);

        bool connectSent =
            await SendCommandDatagramAsync(
                connectDatagram,
                endpoint,
                "Administrator login")
            .ConfigureAwait(false);

        if (!connectSent)
        {
            throw new IOException(
                $"Failed to send administrator login to " +
                $"{endpoint.Address}:{endpoint.Port}/UDP.");
        }

        StatusChanged?.Invoke(
            $"Administrator login sent: {serverAddress} -> " +
            $"{endpoint.Address}:{endpoint.Port}/UDP; " +
            "waiting for authenticated server traffic.");

        return endpoint;
    }

    public void Start()
    {
        _receiveTask ??= Task.Run(ReceiveLoopAsync);
        StatusChanged?.Invoke(
            $"Listening on {_config.BindAddress}:{_config.Port}/UDP");
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp
                    .ReceiveAsync(_stop.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException exception)
            {
                StatusChanged?.Invoke(
                    $"Socket error: {exception.SocketErrorCode}");

                try
                {
                    await Task
                        .Delay(500, _stop.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            IPAddress[] allowedServers =
                _allowedServers;

            // Do not trust incoming bridge traffic until
            // the user has selected a server.
            if (allowedServers.Length == 0)
                continue;

            if (!allowedServers.Any(address =>
                    received.RemoteEndPoint.Address.Equals(address)))
            {
                StatusChanged?.Invoke(
                    $"Ignored packet from {received.RemoteEndPoint.Address}");
                continue;
            }

            if (!VoicePacket.TryParse(
                    received.Buffer,
                    _secret,
                    out VoicePacket? packet,
                    out string error))
            {
                StatusChanged?.Invoke(
                    $"Rejected packet from {received.RemoteEndPoint}: {error}");
                continue;
            }

            bool requestCapabilities = false;
            if (packet!.MessageType == BridgeMessageType.AdminSession)
            {
                var session = new AdminSession(
                    packet.AdminSessionAuthenticated
                        ? packet.AdminSessionMessage
                        : _adminId,
                    packet.PlayerName,
                    packet.AdminSessionRole,
                    packet.AdminSessionPermissions,
                    packet.AdminSessionAuthenticated,
                    packet.AdminSessionMessage);

                _currentSession = session;
                AdminSessionChanged?.Invoke(session);

                if (!session.Authenticated)
                {
                    StatusChanged?.Invoke(session.Message);
                    continue;
                }
                requestCapabilities = true;
            }

            if (packet.MessageType == BridgeMessageType.ServerCapabilities)
                _serverCapabilities = packet;

            // The CS2 server uses the same UDP socket for outbound status/voice
            // and inbound authenticated admin commands. Reply to the exact
            // authenticated endpoint we just heard from.
            _serverEndpoint = new IPEndPoint(
                received.RemoteEndPoint.Address,
                received.RemoteEndPoint.Port);

            if (requestCapabilities)
            {
                _ = SendAdminActionAsync(
                    AdminActionCode.RequestCapabilities,
                    -1);
            }

            VoicePacket acceptedPacket =
                AddTransportMetrics(packet);

            PacketReceived?.Invoke(
                acceptedPacket,
                received.RemoteEndPoint);
        }
    }

    public async Task<bool> SendTeleportAsync(
        ulong steamId,
        int playerSlot,
        float x,
        float y,
        float z)
    {
        IPEndPoint? endpoint = _serverEndpoint;
        if (endpoint is null)
        {
            StatusChanged?.Invoke(
                "Teleport not sent: no authenticated server packet has been received yet.");
            return false;
        }

        byte[] datagram = BridgeCommandPacket.BuildTeleport(
            NextCommandSequence(),
            steamId,
            playerSlot,
            x,
            y,
            z,
            _secret);

        return await SendCommandDatagramAsync(
            datagram,
            endpoint,
            "Teleport").ConfigureAwait(false);
    }

    public async Task<bool> SendPushToTalkAsync(
        byte[] opusPayload,
        int sequenceBytes,
        uint sectionNumber,
        uint uncompressedSampleOffset,
        float voiceLevel)
    {
        // PTT goes directly to the server target selected
        // in the NEO ADMIN UI.
        IPEndPoint? endpoint = _pttEndpoint;

        if (endpoint is null)
        {
            StatusChanged?.Invoke(
                "Push-to-talk not sent: no server target is configured.");
            return false;
        }

        byte[] datagram = BridgeCommandPacket.BuildPushToTalk(
            NextCommandSequence(),
            opusPayload,
            sequenceBytes,
            sectionNumber,
            uncompressedSampleOffset,
            voiceLevel,
            _secret);

        return await SendCommandDatagramAsync(
            datagram,
            endpoint,
            "Dedicated PTT").ConfigureAwait(false);
    }

    public async Task<bool> SendAdminChatAsync(
        string message)
    {
        IPEndPoint? endpoint = _serverEndpoint;

        if (endpoint is null)
        {
            StatusChanged?.Invoke(
                "Chat not sent: waiting for an authenticated server reply.");
            return false;
        }

        byte[] datagram =
            BridgeCommandPacket.BuildAdminChat(
                NextCommandSequence(),
                message,
                _secret);

        return await SendCommandDatagramAsync(
            datagram,
            endpoint,
            "Server chat").ConfigureAwait(false);
    }

    public async Task<bool> SendAdminActionAsync(
        AdminActionCode action,
        int playerSlot,
        int value = 0,
        string? text = null)
    {
        IPEndPoint? endpoint = _serverEndpoint;

        if (endpoint is null)
        {
            StatusChanged?.Invoke(
                "Admin action not sent: waiting for an authenticated server reply.");
            return false;
        }

        byte[] datagram =
            BridgeCommandPacket.BuildAdminAction(
                NextCommandSequence(),
                action,
                playerSlot,
                value,
                text,
                _secret);

        return await SendCommandDatagramAsync(
            datagram,
            endpoint,
            "Admin action").ConfigureAwait(false);
    }

    public async Task<bool> SendHealthProbeAsync()
    {
        IPEndPoint? endpoint = _serverEndpoint;
        if (endpoint is null)
            return false;

        uint requestSequence = NextCommandSequence();
        long sentAt = Stopwatch.GetTimestamp();

        lock (_healthSync)
        {
            foreach (uint staleSequence in _healthProbes
                .Where(pair =>
                    Stopwatch.GetElapsedTime(pair.Value, sentAt) >
                    TimeSpan.FromSeconds(30))
                .Select(pair => pair.Key)
                .ToArray())
            {
                _healthProbes.Remove(staleSequence);
            }

            _healthProbes[requestSequence] = sentAt;
        }

        byte[] datagram =
            BridgeCommandPacket.BuildAdminAction(
                requestSequence,
                AdminActionCode.RequestServerHealth,
                -1,
                0,
                null,
                _secret);

        bool sent = await SendCommandDatagramAsync(
            datagram,
            endpoint,
            "Health probe").ConfigureAwait(false);

        if (!sent)
        {
            lock (_healthSync)
                _healthProbes.Remove(requestSequence);
        }

        return sent;
    }

    private uint NextCommandSequence() =>
        unchecked((uint)Interlocked.Increment(ref _commandSequence));

    private void ResetHealthTracking()
    {
        lock (_healthSync)
        {
            _healthProbes.Clear();
            _hasServerSequence = false;
            _lastServerSequence = 0;
            _receivedServerPackets = 0;
            _missingServerPackets = 0;
        }
    }

    private VoicePacket AddTransportMetrics(VoicePacket packet)
    {
        double roundTripMilliseconds = double.NaN;
        double packetLossPercent;

        lock (_healthSync)
        {
            if (!_hasServerSequence)
            {
                _hasServerSequence = true;
                _lastServerSequence = packet.Sequence;
                _receivedServerPackets = 1;
            }
            else
            {
                uint delta = unchecked(
                    packet.Sequence - _lastServerSequence);

                if (delta > 0 && delta < 0x80000000U)
                {
                    _missingServerPackets += delta - 1U;
                    ++_receivedServerPackets;
                    _lastServerSequence = packet.Sequence;
                }
            }

            ulong expected =
                _receivedServerPackets + _missingServerPackets;

            packetLossPercent = expected == 0
                ? 0.0
                : 100.0 * _missingServerPackets / expected;

            if (packet.MessageType == BridgeMessageType.ServerHealth &&
                _healthProbes.Remove(
                    packet.HealthProbeSequence,
                    out long sentAt))
            {
                roundTripMilliseconds =
                    Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
            }
        }

        return packet with
        {
            RoundTripMilliseconds = roundTripMilliseconds,
            PacketLossPercent = packetLossPercent,
        };
    }

    private async Task<bool> SendCommandDatagramAsync(
        byte[] datagram,
        IPEndPoint endpoint,
        string operation)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            int sent = await _udp
                .SendAsync(datagram, datagram.Length, endpoint)
                .ConfigureAwait(false);

            return sent == datagram.Length;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SocketException exception)
        {
            StatusChanged?.Invoke(
                $"{operation} send error: {exception.SocketErrorCode}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        CryptographicOperations.ZeroMemory(_secret);
        _sendLock.Dispose();
        _stop.Dispose();
    }
}
