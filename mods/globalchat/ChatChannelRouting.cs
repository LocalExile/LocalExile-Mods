using System.Globalization;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

enum ChatChannelRouteType { Blank, Server, Discord, Fake }

sealed record ChannelChoice(JsonNode? Type, JsonNode? DiscordChannelId, JsonNode? Address, JsonNode? Port, JsonNode? SendToDiscord = null);
sealed record ChatChoices(Dictionary<string, ChannelChoice>? GlobalChannels, Dictionary<string, ChannelChoice>? TradeChannels,
    ChannelChoice? FallbackRoute = null, string? DiscordBotToken = null);

readonly record struct ChatRoom(ChatChannelKind Kind, ushort Channel, byte Language)
{
    internal TypedChannel TypedChannel => Kind == ChatChannelKind.Trade ? TypedChannel.Trade : TypedChannel.Global;
    internal static ChatRoom? Of(ClientState conn, ChatChannelKind kind) =>
        ChatRoutes.Membership(Program.CharacterName(conn), kind) is { } channel
            ? new(kind, channel.Channel, channel.Language) : null;
    internal static bool IsListener(ClientState conn) =>
        conn is { Controlled: false, Closed: false, LeavingArea: false, PlayerSent: true, Outbox: not null };
    public override string ToString() => $"{Kind} {Channel}";
}

sealed record DiscordRoute(ushort Channel, string ChannelId, bool SendToDiscord, ChatChannelKind Kind, bool IsFallback = false);

sealed class ChatChannelRouting
{
    internal sealed record Route(ChatChannelRouteType Type, string DiscordId = "", (string Host, int Port)? Server = null, bool SendToDiscord = true);

    readonly Dictionary<(ChatChannelKind Kind, ushort Channel), Route> routes = [];
    readonly Route fallback;

    internal ChatChannelRouting(ChatChoices settings, Action<string> log)
    {
        Apply(ChatChannelKind.Global, settings.GlobalChannels, log);
        Apply(ChatChannelKind.Trade, settings.TradeChannels, log);
        fallback = settings.FallbackRoute is { } choice ? ParseRoute(choice, "Fallback route", log, defaultForward: false)
            : new(ChatChannelRouteType.Blank);
        HasFakeChat = fallback.Type == ChatChannelRouteType.Fake || routes.Values.Any(route => route.Type == ChatChannelRouteType.Fake);
    }

    internal Dictionary<(ChatChannelKind Kind, ushort Channel), (string Host, int Port)?> ChannelServers() =>
        routes.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Channel)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Server);

    internal (string Host, int Port)? FallbackServer => fallback.Server;

    static bool ValidServer(string? host, int port) =>
        !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) != UriHostNameType.Unknown && port is >= 1 and <= 65535;

    internal bool HasFakeChat { get; }
    internal bool IsDefined(ChatChannelKind kind, ushort channel) => routes.ContainsKey((kind, channel));
    internal Route? Find(ChatChannelKind kind, ushort channel) => routes.GetValueOrDefault((kind, channel))
        ?? (fallback.Type == ChatChannelRouteType.Blank ? null : fallback);

    void Apply(ChatChannelKind kind, Dictionary<string, ChannelChoice>? choices, Action<string> log)
    {
        if (choices is null) return;
        foreach (var (number, choice) in choices)
        {
            if (!ushort.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ushort channel)
                || channel is 0 or InstanceChat.AnyChannel || number != channel.ToString(CultureInfo.InvariantCulture))
            {
                log($"Ignoring {kind} chat route '{number}'. Use a channel number from 1 to 65534.");
                continue;
            }
            routes[(kind, channel)] = ParseRoute(choice, $"{kind} {channel}", log, defaultForward: true);
        }
    }

    static Route ParseRoute(ChannelChoice choice, string label, Action<string> log, bool defaultForward)
    {
        string? routeType = choice.Type is JsonValue value && value.TryGetValue<string>(out string? name)
            ? name : choice.Type is null ? "blank" : null;
        var type = routeType?.Trim().ToLowerInvariant() switch
        {
            "server" => ChatChannelRouteType.Server,
            "discord" => ChatChannelRouteType.Discord,
            "fake" => ChatChannelRouteType.Fake,
            "blank" or "none" or "" => ChatChannelRouteType.Blank,
            _ => (ChatChannelRouteType?)null
        };
        if (type is null) log($"Unknown route type for {label}. Using local chat.");

        string? discordId = null;
        if (type == ChatChannelRouteType.Discord && choice.DiscordChannelId is { } configuredId
            && (configuredId is not JsonValue text || !text.TryGetValue<string>(out discordId)))
        {
            log($"{label}: discordChannelId must be quoted. Skipping this mapping.");
            type = ChatChannelRouteType.Blank;
        }

        (string Host, int Port)? server = null;
        if (type == ChatChannelRouteType.Server)
        {
            string? host = choice.Address is JsonValue address && address.TryGetValue<string>(out string? textAddress)
                ? textAddress?.Trim() : null;
            int port = choice.Port is JsonValue portValue && portValue.TryGetValue<int>(out int numberPort) ? numberPort
                : choice.Port is null ? 0 : -1;
            if (port == 0) port = 6113;
            if (ValidServer(host, port)) server = (host!.ToLowerInvariant(), port);
            else
            {
                log($"{label}: invalid server address or port. Using local chat.");
                type = ChatChannelRouteType.Blank;
            }
        }

        bool sendToDiscord = choice.SendToDiscord is null ? defaultForward
            : choice.SendToDiscord is JsonValue outbound && outbound.TryGetValue<bool>(out bool send) && send;
        return new(type ?? ChatChannelRouteType.Blank, discordId ?? "", server, sendToDiscord);
    }

    internal IEnumerable<DiscordRoute> DiscordRoutes(Action<string> log)
    {
        var used = new HashSet<ulong>();
        foreach (var ((kind, channel), route) in routes.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Channel))
            if (RegisterDiscordId(route, $"Discord {kind} {channel}") is { } id)
                yield return new(channel, id, route.SendToDiscord, kind);
        if (RegisterDiscordId(fallback, "Discord fallback") is { } fallbackId)
            yield return new(0, fallbackId, fallback.SendToDiscord, ChatChannelKind.Global, IsFallback: true);

        string? RegisterDiscordId(Route route, string label)
        {
            if (route.Type != ChatChannelRouteType.Discord || string.IsNullOrWhiteSpace(route.DiscordId)) return null;
            if (!ulong.TryParse(route.DiscordId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0)
            {
                log($"{label}: discordChannelId must be a quoted numeric channel ID; skipping this mapping.");
                return null;
            }
            if (!used.Add(id))
            {
                log($"{label}: discordChannelId repeats an earlier channel ID; skipping this mapping.");
                return null;
            }
            return id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
