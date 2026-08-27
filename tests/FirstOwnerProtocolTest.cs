using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NeoAdmin;

const string displayName = "First Owner";
const string accountId = "first.owner";
const string accessKey = "first-owner-access-key-0123456789-ABCDEFG";
const string compactCode = "ABCDEFGHJKLMNPQRSTUVWXYZ";
const string canonicalCode = "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ";

byte[] packet = BridgeCommandPacket.BuildFirstOwnerClaim(
    1234,
    displayName,
    accountId,
    accessKey,
    compactCode);

ReadOnlySpan<byte> header = packet.AsSpan(0, 60);
ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[48..50]);
uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[52..56]);
int authenticatedLength = packet.Length - 32;
using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(canonicalCode));
byte[] expectedTag = hmac.ComputeHash(packet, 0, authenticatedLength);

if (!header[..4].SequenceEqual("CVB1"u8) ||
    header[4] != 1 || header[5] != 18 ||
    BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]) != 1234 ||
    BinaryPrimitives.ReadInt32LittleEndian(header[24..28]) != -1 ||
    nameLength != Encoding.UTF8.GetByteCount(displayName) ||
    payloadLength != Encoding.UTF8.GetByteCount(accountId + "\n" + accessKey) ||
    Encoding.UTF8.GetString(packet.AsSpan(60, nameLength)) != displayName ||
    Encoding.UTF8.GetString(packet.AsSpan(60 + nameLength, (int)payloadLength)) !=
        accountId + "\n" + accessKey ||
    !CryptographicOperations.FixedTimeEquals(
        expectedTag,
        packet.AsSpan(authenticatedLength, 32)))
{
    return 1;
}

packet[60] ^= 1;
byte[] tamperedTag = hmac.ComputeHash(packet, 0, authenticatedLength);
if (CryptographicOperations.FixedTimeEquals(
        tamperedTag,
        packet.AsSpan(authenticatedLength, 32)))
{
    return 2;
}

byte[] adminSecret = Encoding.UTF8.GetBytes(
    "admin-action-secret-0123456789-ABCDEFG");
string selector = BridgeCommandPacket.BuildAdminAccessSelector(adminSecret);
if (selector.Length != 32 || !selector.StartsWith("key_", StringComparison.Ordinal))
    return 4;
const string operatorName = "Neo Conform";
byte[] loginPacket = BridgeCommandPacket.BuildAdminLogin(
    4567,
    selector,
    operatorName,
    adminSecret);
ReadOnlySpan<byte> loginHeader = loginPacket.AsSpan(0, 60);
int loginAuthenticatedLength = loginPacket.Length - 32;
byte[] expectedLoginTag = HMACSHA256.HashData(
    adminSecret,
    loginPacket.AsSpan(0, loginAuthenticatedLength));
ushort loginNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
    loginHeader[48..50]);
uint loginPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
    loginHeader[52..56]);
if (loginHeader[5] != 15 ||
    loginNameLength != Encoding.UTF8.GetByteCount(operatorName) ||
    loginPayloadLength != Encoding.UTF8.GetByteCount(selector) ||
    Encoding.UTF8.GetString(loginPacket.AsSpan(60, loginNameLength)) != operatorName ||
    Encoding.UTF8.GetString(loginPacket.AsSpan(60 + loginNameLength, (int)loginPayloadLength)) != selector ||
    !CryptographicOperations.FixedTimeEquals(
        expectedLoginTag,
        loginPacket.AsSpan(loginAuthenticatedLength, 32)))
{
    return 5;
}
byte[] legacyLoginPacket = BridgeCommandPacket.BuildAdminLogin(
    4566,
    selector,
    adminSecret);
if (BinaryPrimitives.ReadUInt16LittleEndian(
        legacyLoginPacket.AsSpan(48, 2)) != 0)
{
    return 6;
}
byte[] givePacket = BridgeCommandPacket.BuildAdminAction(
    5678,
    AdminActionCode.GiveItem,
    7,
    0,
    "weapon_ak47",
    adminSecret);

ReadOnlySpan<byte> giveHeader = givePacket.AsSpan(0, 60);
int giveAuthenticatedLength = givePacket.Length - 32;
using var giveHmac = new HMACSHA256(adminSecret);
byte[] expectedGiveTag = giveHmac.ComputeHash(
    givePacket,
    0,
    giveAuthenticatedLength);

if (giveHeader[5] != 11 ||
    BinaryPrimitives.ReadUInt32LittleEndian(giveHeader[8..12]) != 5678 ||
    BinaryPrimitives.ReadInt32LittleEndian(giveHeader[24..28]) != 7 ||
    BinaryPrimitives.ReadUInt32LittleEndian(giveHeader[28..32]) != 23 ||
    BinaryPrimitives.ReadUInt32LittleEndian(giveHeader[52..56]) != 11 ||
    Encoding.UTF8.GetString(givePacket.AsSpan(60, 11)) != "weapon_ak47" ||
    !CryptographicOperations.FixedTimeEquals(
        expectedGiveTag,
        givePacket.AsSpan(giveAuthenticatedLength, 32)))
{
    return 3;
}

Console.WriteLine("Windows administrator protocol self-test passed.");
return 0;
