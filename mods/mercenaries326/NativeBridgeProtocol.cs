using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace LocalExile.Mods.Mercenaries326;

/// <summary>
/// Minimal zero-dependency NativeMods <c>HostRuleset</c> (<c>0xA0</c>) <c>NMOD</c> envelope transport
/// over <c>S→C 0x02C9</c> / <c>C→S 0x02CA</c>. Stock unmodded PoE 3.29 clients silently drop unknown
/// ruleset <c>0xA0</c> frames, and when an external <c>GatewayBridgeBus</c> is present in the process
/// this bridge coexists without duplicating <c>ServerHandshake</c> or swallowing other mods' traffic.
/// </summary>
internal sealed class NativeBridgeProtocol
{
    public const byte HostRulesetId = 0xA0;
    public const uint EnvelopeMagic = 0x4E4D4F44; // "NMOD"
    public const ushort ProtocolVersion = 1;
    public const string SystemChannel = "__host";
    public const string ModChannel = "mercenaries326";
    public const string StateSyncMessageType = "Mercenaries326StateSync";
    public const string ClientHandshakeType = "ClientHandshake";
    public const string ServerHandshakeType = "ServerHandshake";
    const string GatewayBusDomainKey = "LocalExile.NativeMods.GatewayBus.v1";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public sealed class ClientStatus
    {
        public bool HasNativeHost;
        public bool HasMercenaries326Plugin;
    }

    public readonly record struct StateSync(bool Enabled, bool AllowUniqueBodyArmour);

    sealed record ServerHandshakePayload(
        string ServerVersion,
        string BridgeVersion,
        int ProtocolVersion,
        byte HostRulesetId,
        int PreferredTransport,
        string[] RequiredMods,
        string[] ServerCapabilities,
        string? ExpectedContentRowMapDigest = null);

    readonly ConditionalWeakTable<Player, ClientStatus> _clients = new();
    long _sequence;

    public ClientStatus? StatusOf(Player player) =>
        _clients.TryGetValue(player, out var status) ? status : null;

    public void Remove(Player player) => _clients.Remove(player);

    public void Clear() => _clients.Clear();

    /// <summary>Handles incoming <c>C→S 0x02CA</c> packets for ruleset <c>0xA0</c>. Returns <c>true</c> only
    /// when consumed exclusively by this mod (no external NativeMods <c>GatewayBridgeBus</c> active).</summary>
    public async Task<bool> TryHandleRulesetActionAsync(Player player, byte[] message, Func<StateSync> currentState)
    {
        byte row;
        byte[] payload;
        try { (row, payload) = InstanceRulesets.ReadAction(message); }
        catch (InvalidDataException) { return false; }

        if (row != HostRulesetId) return false;
        if (!TryDecodeEnvelope(payload, out string channel, out string messageType, out string payloadJson))
            return false;

        if (!string.Equals(channel, SystemChannel, StringComparison.Ordinal)
            || !string.Equals(messageType, ClientHandshakeType, StringComparison.Ordinal))
            return false;

        bool hasPlugin = ParseHasMercenaries326Plugin(payloadJson);
        var status = _clients.GetOrCreateValue(player);
        status.HasNativeHost = true;
        status.HasMercenaries326Plugin = hasPlugin;

        bool externalBus = HasExternalGatewayBus();
        if (!externalBus)
        {
            var handshake = new ServerHandshakePayload(
                ServerVersion: "LocalExile-3.29.3d",
                BridgeVersion: "1.2.0",
                ProtocolVersion: ProtocolVersion,
                HostRulesetId: HostRulesetId,
                PreferredTransport: 1, // NetTransportKind.HostRuleset
                RequiredMods: [],
                ServerCapabilities: [ModChannel]);
            await SendEnvelopeAsync(player, SystemChannel, ServerHandshakeType,
                JsonSerializer.Serialize(handshake, JsonOptions),
                "0x02c9 NativeMods ServerHandshake (0xA0)").ConfigureAwait(false);
        }

        await SendStateAsync(player, currentState()).ConfigureAwait(false);

        // If another NativeMods server mod owns GatewayBridgeBus, return false so the bus also receives ClientHandshake.
        return !externalBus;
    }

    public Task SendStateAsync(Player player, StateSync state)
    {
        if (player.Closed || player.LeavingArea) return Task.CompletedTask;
        string json = JsonSerializer.Serialize(state, JsonOptions);
        return SendEnvelopeAsync(player, ModChannel, StateSyncMessageType, json,
            $"0x02c9 mercenaries326 state (enabled={state.Enabled}, bodyArmour={state.AllowUniqueBodyArmour})");
    }

    async Task SendEnvelopeAsync(Player player, string channel, string messageType, string payloadJson, string log)
    {
        if (player.Closed || player.LeavingArea) return;
        long seq = Interlocked.Increment(ref _sequence);
        byte[] frame = EncodeEnvelope(channel, messageType, payloadJson, seq);
        byte[] packet = InstanceRulesets.Data(HostRulesetId, frame);
        try
        {
            await Program.Send(player, packet, log).ConfigureAwait(false);
        }
        catch
        {
            // Ignore transient send errors during disconnect or unload.
        }
    }

    static bool HasExternalGatewayBus()
    {
        if (AppDomain.CurrentDomain.GetData(GatewayBusDomainKey) is not ConcurrentDictionary<string, object> state)
            return false;
        if (!state.TryGetValue("participants", out object? raw)
            || raw is not ConcurrentDictionary<string, object[]> participants)
            return false;
        return !participants.IsEmpty;
    }

    static bool ParseHasMercenaries326Plugin(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("installedMods", out var mods) || mods.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var entry in mods.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String
                    && string.Equals(idProp.GetString(), ModChannel, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    public static byte[] EncodeEnvelope(string channel, string messageType, string payloadJson, long sequence)
    {
        byte[] channelBytes = Encoding.UTF8.GetBytes(channel);
        byte[] typeBytes = Encoding.UTF8.GetBytes(messageType);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        int totalLength = 4 + 2 + 2 + channelBytes.Length + 2 + typeBytes.Length + 4 + payloadBytes.Length + 8;
        byte[] buffer = new byte[totalLength];
        Span<byte> span = buffer;
        int offset = 0;

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), EnvelopeMagic);
        offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), ProtocolVersion);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), (ushort)channelBytes.Length);
        offset += 2;
        channelBytes.CopyTo(span.Slice(offset, channelBytes.Length));
        offset += channelBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), (ushort)typeBytes.Length);
        offset += 2;
        typeBytes.CopyTo(span.Slice(offset, typeBytes.Length));
        offset += typeBytes.Length;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), (uint)payloadBytes.Length);
        offset += 4;
        payloadBytes.CopyTo(span.Slice(offset, payloadBytes.Length));
        offset += payloadBytes.Length;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(offset, 8), sequence);

        return buffer;
    }

    public static bool TryDecodeEnvelope(ReadOnlySpan<byte> frame, out string channel, out string messageType, out string payloadJson)
    {
        channel = string.Empty;
        messageType = string.Empty;
        payloadJson = string.Empty;

        if (frame.Length < 22) return false;
        int offset = 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(offset, 4)) != EnvelopeMagic)
            return false;
        offset += 4;

        // protocolVersion (u16 BE)
        offset += 2;

        ushort channelLen = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
        offset += 2;
        if (offset + channelLen + 2 > frame.Length) return false;
        channel = Encoding.UTF8.GetString(frame.Slice(offset, channelLen));
        offset += channelLen;

        ushort typeLen = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
        offset += 2;
        if (offset + typeLen + 4 > frame.Length) return false;
        messageType = Encoding.UTF8.GetString(frame.Slice(offset, typeLen));
        offset += typeLen;

        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(offset, 4));
        offset += 4;
        if (payloadLen > int.MaxValue || offset + (int)payloadLen + 8 > frame.Length) return false;
        payloadJson = Encoding.UTF8.GetString(frame.Slice(offset, (int)payloadLen));
        return true;
    }
}
