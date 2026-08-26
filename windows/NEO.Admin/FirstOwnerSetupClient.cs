using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NeoAdmin;

internal sealed record FirstOwnerSetupResult(
    string ServerAddress,
    int ServerPort,
    string DisplayName,
    string AccountId,
    string AccessKey);

internal static class FirstOwnerSetupClient
{
    public static async Task<FirstOwnerSetupResult> ClaimAsync(
        string serverAddress,
        int serverPort,
        string displayName,
        string accountId,
        string setupCode,
        CancellationToken cancellationToken = default)
    {
        serverAddress = serverAddress.Trim();
        displayName = displayName.Trim();
        accountId = accountId.Trim();
        if (serverAddress.Length == 0)
            throw new InvalidDataException("Enter the CS2 server address.");
        if (serverPort is < 1 or > 65535)
            throw new InvalidDataException("Server port must be between 1 and 65535.");

        IPAddress[] addresses = await ResolveIpv4Async(
            serverAddress,
            cancellationToken);
        IPEndPoint endpoint = new(addresses[0], serverPort);
        string accessKey = GenerateAccessKey();
        byte[] accessKeyBytes = System.Text.Encoding.UTF8.GetBytes(accessKey);

        try
        {
            byte[] claim = BridgeCommandPacket.BuildFirstOwnerClaim(
                unchecked((uint)Environment.TickCount),
                displayName,
                accountId,
                accessKey,
                setupCode);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            for (int attempt = 1; attempt <= 4; ++attempt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await udp.SendAsync(claim, endpoint, cancellationToken);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                wait.CancelAfter(TimeSpan.FromMilliseconds(1500));
                try
                {
                    while (true)
                    {
                        UdpReceiveResult response = await udp.ReceiveAsync(wait.Token);
                        if (!addresses.Contains(response.RemoteEndPoint.Address) ||
                            response.RemoteEndPoint.Port != serverPort)
                        {
                            continue;
                        }

                        if (!VoicePacket.TryParse(
                                response.Buffer,
                                accessKeyBytes,
                                out VoicePacket? packet,
                                out _))
                        {
                            continue;
                        }

                        if (packet!.MessageType != BridgeMessageType.AdminSession ||
                            !packet.AdminSessionAuthenticated ||
                            packet.AdminSessionRole != "Owner" ||
                            packet.AdminSessionMessage != accountId ||
                            (packet.AdminSessionPermissions &
                                AdminPermission.ManageAccounts) == 0)
                        {
                            throw new InvalidDataException(
                                "The server did not confirm a valid Owner account.");
                        }

                        return new FirstOwnerSetupResult(
                            serverAddress,
                            serverPort,
                            displayName,
                            accountId,
                            accessKey);
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt == 4)
                    {
                        throw new TimeoutException(
                            "The server did not answer the setup request. " +
                            "Check the address, UDP port, firewall, and setup code. " +
                            "If an Owner already exists, initial setup is permanently disabled.");
                    }
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accessKeyBytes);
        }

        throw new TimeoutException("The server did not answer the setup request.");
    }

    private static async Task<IPAddress[]> ResolveIpv4Async(
        string serverAddress,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(serverAddress, out IPAddress? literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidDataException("Enter an IPv4 address or IPv4 hostname.");
            return new[] { literal };
        }

        IPAddress[] addresses = (await Dns.GetHostAddressesAsync(
                serverAddress,
                cancellationToken))
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToArray();
        if (addresses.Length == 0)
            throw new InvalidDataException($"No IPv4 address was found for {serverAddress}.");
        return addresses;
    }

    private static string GenerateAccessKey()
    {
        string value = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
