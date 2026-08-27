using NeoAdmin;

if (args.Length != 4 ||
    !int.TryParse(args[1], out int serverPort) ||
    serverPort is < 1 or > 65535)
{
    Console.Error.WriteLine(
        "Usage: LiveMultiAdminConnectionProbe <server> <port> <admin-id> <access-key>");
    return 64;
}

static UdpVoiceReceiver CreateReceiver(
    int localPort,
    string accountId,
    string accessKey,
    string displayName)
{
    return new UdpVoiceReceiver(new AppConfig
    {
        BindAddress = "0.0.0.0",
        Port = localPort,
        AdminId = accountId,
        AdminDisplayName = displayName,
        SharedSecret = accessKey,
    });
}

var firstSession = new TaskCompletionSource<AdminSession>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var secondSession = new TaskCompletionSource<AdminSession>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var firstHealth = new TaskCompletionSource<VoicePacket>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var secondHealth = new TaskCompletionSource<VoicePacket>(
    TaskCreationOptions.RunContinuationsAsynchronously);

await using var first = CreateReceiver(27123, args[2], args[3], "Neo Conform");
await using var second = CreateReceiver(27124, args[2], args[3], "Tay Loan");

first.AdminSessionChanged += session =>
{
    if (session?.Authenticated == true)
        firstSession.TrySetResult(session);
};
second.AdminSessionChanged += session =>
{
    if (session?.Authenticated == true)
        secondSession.TrySetResult(session);
};
first.PacketReceived += (packet, _) =>
{
    if (packet.MessageType == BridgeMessageType.ServerHealth)
        firstHealth.TrySetResult(packet);
};
second.PacketReceived += (packet, _) =>
{
    if (packet.MessageType == BridgeMessageType.ServerHealth)
        secondHealth.TrySetResult(packet);
};

first.Start();
second.Start();
await first.ConfigureServerAsync(args[0], serverPort);
await second.ConfigureServerAsync(args[0], serverPort);

Task authentication = Task.WhenAll(firstSession.Task, secondSession.Task);
if (await Task.WhenAny(authentication, Task.Delay(TimeSpan.FromSeconds(8))) !=
    authentication)
{
    Console.Error.WriteLine("RESULT Both administrator sessions did not authenticate.");
    return 2;
}

if ((await firstSession.Task).DisplayName != "Neo Conform" ||
    (await secondSession.Task).DisplayName != "Tay Loan")
{
    Console.Error.WriteLine("RESULT Concurrent session names were not preserved.");
    return 5;
}

// The second login used to replace the first global peer. Probe the first
// client after both logins, then the second, to guard that regression.
if (!await first.SendHealthProbeAsync() ||
    !await second.SendHealthProbeAsync())
{
    Console.Error.WriteLine("RESULT A health probe could not be sent.");
    return 3;
}

Task healthReplies = Task.WhenAll(firstHealth.Task, secondHealth.Task);
if (await Task.WhenAny(healthReplies, Task.Delay(TimeSpan.FromSeconds(8))) !=
    healthReplies)
{
    Console.Error.WriteLine(
        $"RESULT firstHealth={firstHealth.Task.IsCompletedSuccessfully} " +
        $"secondHealth={secondHealth.Task.IsCompletedSuccessfully}");
    return 4;
}

Console.WriteLine(
    "RESULT Two concurrent administrator sessions authenticated and " +
    "received independent server-health replies.");
return 0;
