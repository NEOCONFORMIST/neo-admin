using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using NeoAdmin;

if (args.Length != 4 ||
    !int.TryParse(args[1], out int serverPort) ||
    serverPort is < 1 or > 65535)
{
    Console.Error.WriteLine(
        "Usage: LiveMapOverviewProbe <server> <port> <admin-id> <access-key>");
    return 64;
}

var config = new AppConfig
{
    BindAddress = "0.0.0.0",
    Port = 0,
    AdminId = args[2],
    AdminDisplayName = "Map Overview Probe",
    SharedSecret = args[3],
};

var sessionResult = new TaskCompletionSource<AdminSession>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var mapResult = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);
Channel<VoicePacket> chunks = Channel.CreateUnbounded<VoicePacket>();

await using var receiver = new UdpVoiceReceiver(config);
receiver.AdminSessionChanged += session =>
{
    if (session?.Authenticated == true)
        sessionResult.TrySetResult(session);
};
receiver.PacketReceived += (packet, _) =>
{
    if (packet.MessageType == BridgeMessageType.MapChanged &&
        !string.IsNullOrWhiteSpace(packet.MapName))
    {
        mapResult.TrySetResult(packet.MapName);
    }
    else if (packet.MessageType == BridgeMessageType.MapOverviewChunk)
    {
        chunks.Writer.TryWrite(packet);
    }
};

receiver.Start();
await receiver.ConfigureServerAsync(args[0], serverPort);

Task ready = Task.WhenAll(sessionResult.Task, mapResult.Task);
if (await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(10))) != ready)
{
    Console.Error.WriteLine("RESULT Authentication or current map was not received.");
    return 2;
}

string mapName = await mapResult.Task;
VoicePacket first = await RequestChunkAsync(0);
int packageLength = checked((int)first.MapOverviewPackageLength);
int chunkCount = checked((int)first.MapOverviewChunkCount);
if (packageLength is < 12 or > 2 * 1024 * 1024 ||
    chunkCount is < 1 or > 2048)
{
    Console.Error.WriteLine("RESULT Server returned invalid overview metadata.");
    return 3;
}

byte[] package = new byte[packageLength];
int written = 0;
for (int index = 0; index < chunkCount; index++)
{
    VoicePacket chunk = index == 0 ? first : await RequestChunkAsync(index);
    if (chunk.MapOverviewChunkIndex != index ||
        chunk.MapOverviewChunkCount != first.MapOverviewChunkCount ||
        chunk.MapOverviewPackageLength != first.MapOverviewPackageLength ||
        chunk.MapOverviewPackageHash != first.MapOverviewPackageHash ||
        chunk.MapOverviewDefinitionLength != first.MapOverviewDefinitionLength ||
        written + chunk.Payload.Length > package.Length)
    {
        Console.Error.WriteLine($"RESULT Overview chunk {index} is inconsistent.");
        return 4;
    }

    chunk.Payload.CopyTo(package, written);
    written += chunk.Payload.Length;
}

int definitionLength = checked((int)first.MapOverviewDefinitionLength);
uint storedDefinitionLength = BinaryPrimitives.ReadUInt32LittleEndian(package);
int imageOffset = 4 + definitionLength;
byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
if (written != package.Length ||
    ComputeFnv1a(package) != first.MapOverviewPackageHash ||
    storedDefinitionLength != first.MapOverviewDefinitionLength ||
    imageOffset + pngSignature.Length > package.Length ||
    !package.AsSpan(imageOffset, pngSignature.Length).SequenceEqual(pngSignature))
{
    Console.Error.WriteLine("RESULT Downloaded overview package failed integrity checks.");
    return 5;
}

try
{
    using JsonDocument definition = JsonDocument.Parse(
        package.AsMemory(4, definitionLength));
    string? packagedMap = definition.RootElement
        .GetProperty("MapName")
        .GetString();
    if (!mapName.Equals(packagedMap, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The definition names a different map.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        $"RESULT Downloaded overview definition is invalid JSON: {exception.Message}");
    return 6;
}

Console.WriteLine(
    $"RESULT map={mapName} bytes={packageLength} chunks={chunkCount} " +
    "integrity=passed");
return 0;

async Task<VoicePacket> RequestChunkAsync(int index)
{
    bool sent = await receiver.SendAdminActionAsync(
        AdminActionCode.RequestMapOverview,
        -1,
        index,
        mapName);
    if (!sent)
        throw new IOException($"Could not request overview chunk {index}.");

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (true)
    {
        VoicePacket packet = await chunks.Reader.ReadAsync(timeout.Token);
        if (packet.MapOverviewChunkIndex == index &&
            packet.MapOverviewName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
        {
            return packet;
        }
    }
}

static uint ComputeFnv1a(ReadOnlySpan<byte> bytes)
{
    uint hash = 2166136261U;
    foreach (byte value in bytes)
    {
        hash ^= value;
        hash *= 16777619U;
    }
    return hash;
}
