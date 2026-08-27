using NeoAdmin;

if (args.Length != 4 ||
    !int.TryParse(args[1], out int serverPort) ||
    serverPort is < 1 or > 65535)
{
    Console.Error.WriteLine(
        "Usage: LiveAdminConnectionProbe <server> <port> <admin-id> <access-key>");
    return 64;
}

var config = new AppConfig
{
    BindAddress = "0.0.0.0",
    Port = 0,
    AdminId = args[2],
    AdminDisplayName = "Phone Probe",
    SharedSecret = args[3],
};

var sessionResult = new TaskCompletionSource<AdminSession>(
    TaskCreationOptions.RunContinuationsAsynchronously);
await using var receiver = new UdpVoiceReceiver(config);
receiver.StatusChanged += status => Console.WriteLine($"STATUS {status}");
receiver.AdminSessionChanged += session =>
{
    if (session is not null)
        sessionResult.TrySetResult(session);
};

receiver.Start();
await receiver.ConfigureServerAsync(args[0], serverPort);

Task completed = await Task.WhenAny(
    sessionResult.Task,
    Task.Delay(TimeSpan.FromSeconds(8)));
if (completed != sessionResult.Task)
{
    Console.Error.WriteLine("RESULT No authenticated response within 8 seconds.");
    return 2;
}

AdminSession session = await sessionResult.Task;
Console.WriteLine(
    $"RESULT authenticated={session.Authenticated} " +
    $"account={session.AccountId} display={session.DisplayName} " +
    $"role={session.Role} permissions=0x{(ulong)session.Permissions:X} " +
    $"canTeleport={session.Can(AdminPermission.TeleportPlayers)} " +
    $"message={session.Message}");
return session.Authenticated ? 0 : 3;
