using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace LocalExile.Mods.SkipCampaign;

/// <summary>
/// Minimal zero-dependency NativeMods <c>HostRuleset</c> (<c>0xA0</c>) <c>NMOD</c> envelope transport
/// over <c>S→C 0x02C9</c> / <c>C→S 0x02CA</c>. Uses <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>
/// directly so <c>System.Text.Json</c> never caches mod types in a collectible <c>AssemblyLoadContext</c>,
/// and coexists cleanly when an external <c>GatewayBridgeBus</c> is present.
/// </summary>
internal sealed class NativeBridgeProtocol
{
    public const byte HostRulesetId = 0xA0;
    public const uint EnvelopeMagic = 0x4E4D4F44; // "NMOD"
    public const ushort ProtocolVersion = 1;
    public const string SystemChannel = "__host";
    public const string ModChannel = SkipCampaignProtocol.ChannelName;
    public const string ClientHandshakeType = "ClientHandshake";
    public const string ServerHandshakeType = "ServerHandshake";
    const string GatewayBusDomainKey = "LocalExile.NativeMods.GatewayBus.v1";

    public sealed class ClientStatus
    {
        public bool HasNativeHost;
        public bool HasSkipCampaignPlugin;
    }

    readonly ConditionalWeakTable<Player, ClientStatus> _clients = new();
    readonly ConcurrentDictionary<string, SkipCampaignAppliedNotice> _pendingNotices = new(StringComparer.OrdinalIgnoreCase);
    long _sequence;

    public ClientStatus? StatusOf(Player player) =>
        _clients.TryGetValue(player, out var status) ? status : null;

    public bool IsPluginReady(Player player) =>
        _clients.TryGetValue(player, out var status) && status.HasSkipCampaignPlugin;

    public void QueueNotice(string characterName, SkipCampaignAppliedNotice notice)
    {
        if (!string.IsNullOrWhiteSpace(characterName))
        {
            _pendingNotices[characterName.Trim()] = notice;
        }
    }

    public void Remove(Player player)
    {
        _clients.Remove(player);
        if (player.Character.Name is { Length: > 0 } name)
        {
            _pendingNotices.TryRemove(name.Trim(), out _);
        }
    }

    public void Clear()
    {
        _clients.Clear();
        _pendingNotices.Clear();
    }

    /// <summary>Handles incoming <c>C→S 0x02CA</c> packets for ruleset <c>0xA0</c>. Returns <c>true</c> only
    /// when consumed exclusively by this mod (no external NativeMods <c>GatewayBridgeBus</c> active).</summary>
    public async Task<bool> TryHandleRulesetActionAsync(Player player, byte[] message)
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

        bool hasPlugin = ParseHasSkipCampaignPlugin(payloadJson);
        var status = _clients.GetOrCreateValue(player);
        status.HasNativeHost = true;
        status.HasSkipCampaignPlugin = hasPlugin;

        bool externalBus = HasExternalGatewayBus();
        if (!externalBus)
        {
            string handshakeJson = SerializeServerHandshakeJson();
            await SendEnvelopeAsync(player, SystemChannel, ServerHandshakeType, handshakeJson,
                "0x02c9 NativeMods ServerHandshake (0xA0)").ConfigureAwait(false);
        }

        if (hasPlugin && player.Character.Name is { Length: > 0 } name &&
            _pendingNotices.TryRemove(name.Trim(), out SkipCampaignAppliedNotice? queued))
        {
            await SendAppliedNoticeAsync(player, queued).ConfigureAwait(false);
        }

        // If another NativeMods server mod owns GatewayBridgeBus, return false so the bus also receives ClientHandshake.
        return !externalBus;
    }

    public Task SendAppliedNoticeAsync(Player player, SkipCampaignAppliedNotice notice)
    {
        if (player.Closed || player.LeavingArea) return Task.CompletedTask;
        string json = SerializeAppliedNoticeJson(notice);
        return SendEnvelopeAsync(player, ModChannel, SkipCampaignProtocol.AppliedNoticeMessageType, json,
            $"0x02c9 skip-campaign notice ({notice.CharacterName} Lv.{notice.StartingLevel} {notice.StartingAreaId})");
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

    static bool ParseHasSkipCampaignPlugin(string json)
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
                    && string.Equals(idProp.GetString(), SkipCampaignProtocol.ModId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    static string SerializeServerHandshakeJson()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("serverVersion", "LocalExile-3.29.3d");
            w.WriteString("bridgeVersion", "1.2.0");
            w.WriteNumber("protocolVersion", ProtocolVersion);
            w.WriteNumber("hostRulesetId", HostRulesetId);
            w.WriteNumber("preferredTransport", 1); // NetTransportKind.HostRuleset
            w.WriteStartArray("requiredMods");
            w.WriteEndArray();
            w.WriteStartArray("serverCapabilities");
            w.WriteStringValue(ModChannel);
            w.WriteEndArray();
            w.WriteNull("expectedContentRowMapDigest");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static string SerializeAppliedNoticeJson(SkipCampaignAppliedNotice notice)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("characterName", notice.CharacterName);
            w.WriteString("league", notice.League);
            w.WriteString("startingAreaId", notice.StartingAreaId);
            w.WriteNumber("startingLevel", notice.StartingLevel);
            w.WriteNumber("waypointsGranted", notice.WaypointsGranted);
            w.WriteNumber("questFlagsApplied", notice.QuestFlagsApplied);
            w.WriteNumber("refundPoints", notice.RefundPoints);
            w.WriteNumber("ascendancyPointsEarned", notice.AscendancyPointsEarned);
            w.WriteNumber("goldGranted", notice.GoldGranted);
            w.WriteNumber("totalGold", notice.TotalGold);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
