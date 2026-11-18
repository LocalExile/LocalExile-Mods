using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GlobalChat;

public sealed class GlobalChatMod : IMod
{
    // The channel prefix alone carries no player name. Neutralize markup in the displayed text.
    internal static byte[] IncomingLine(string name, string text, TypedChannel channel = TypedChannel.Global) =>
        InstanceChat.ChatLine(ChatWire.Clean($"[Discord] {name} : {text.Replace('<', '＜')}"), InstanceChat.Sender(channel, ""));

    internal static ChatChoices ReadSettings(IModContext mod)
    {
        string path = Path.Combine(mod.Folder, "globalchat.json");
        if (!File.Exists(path)) return mod.Settings<ChatChoices>();

        var schema = ModSettings.Parse(File.ReadAllText(Path.Combine(mod.Folder, ModSettings.FileName)));
        // The editor declares each field. Validate individual routes separately when reading a hand-edited file.
        var declarations = schema.Settings.ToDictionary();
        foreach (string name in new[] { "globalChannels", "tradeChannels" })
        {
            var map = declarations[name];
            declarations[name] = map with { Value = map.Value! with { Settings = new Dictionary<string, ModSettings.Declaration>() } };
        }
        string json = File.ReadAllText(path);
        try
        {
            if (JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                is JsonObject values)
            {
                if (values["sendToDiscord"] is JsonValue legacy && legacy.TryGetValue<bool>(out bool forward))
                    foreach (string name in new[] { "globalChannels", "tradeChannels" })
                        if (values[name] is JsonObject channels)
                            foreach (var (_, route) in channels)
                                if (route is JsonObject entry && !entry.ContainsKey("sendToDiscord")
                                    && entry["type"] is JsonValue kind && kind.TryGetValue<string>(out string? nameOfType)
                                    && string.Equals(nameOfType, "discord", StringComparison.OrdinalIgnoreCase))
                                    entry["sendToDiscord"] = forward;
                if (values["fallbackRoute"] is JsonObject fallback && fallback["type"]?.ToJsonString() == "\"none\"")
                    fallback["type"] = "blank";
                values.Remove("sendToDiscord");
                values.Remove("useSharedServer");
                values.Remove("enableDiscord");
                values.Remove("enableFakeChats");
                values.Remove("host");
                values.Remove("port");
                values.Remove("globalChatSeconds");
                values.Remove("language");
                json = values.ToJsonString();
            }
        }
        catch (JsonException) { } // The host settings merger reports invalid JSON.
        return ModSettings.Bound<ChatChoices>(ModSettings.Merge(schema with { Settings = declarations }, json, mod.Log));
    }

    public void Load(IModContext mod)
    {
        var settings = ReadSettings(mod);
        string owner = mod.Manifest.Id;
        var routing = new ChatChannelRouting(settings, mod.Log);
        var endpoints = routing.ChannelServers();
        var whisperServer = endpoints.Values.FirstOrDefault(endpoint => endpoint is not null);
        if (whisperServer is not null || routing.FallbackServer is not null)
            ChatRoutes.Use(owner, whisperServer, endpoints, routing.FallbackServer);

        if (routing.HasFakeChat)
        {
            var simulation = new SimulatedChat(routing, mod.Log);
            mod.Lifetime.Own(simulation);
            GameEvents.ChatPosted.Add(owner, simulation.OnPlayerChat);
            simulation.Start();
        }

        var routes = routing.DiscordRoutes(mod.Log).ToArray();
        if (routes.Length == 0) return;
        string? token = Environment.GetEnvironmentVariable("LOCALEXILE_DISCORD_BOT_TOKEN")?.Trim();
        if (string.IsNullOrEmpty(token)) token = settings.DiscordBotToken?.Trim();
        if (string.IsNullOrWhiteSpace(token)
            || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
        {
            mod.Log("Discord bridge needs a valid Discord bot token in globalchat.json or LOCALEXILE_DISCORD_BOT_TOKEN.");
            return;
        }

        var bridges = new Dictionary<(ChatChannelKind Kind, ushort Channel), DiscordBridge>(routes.Length);
        DiscordBridge? fallback = null;
        foreach (var route in routes)
        {
            var bridge = StartRoute(mod, route, routing, token);
            if (route.IsFallback) fallback = bridge;
            else bridges.Add((route.Kind, route.Channel), bridge);
        }

        if (routes.Any(route => route.SendToDiscord))
            GameEvents.ChatPosted.Add(owner, (_, line) =>
            {
                if (line is not { Kind: { } kind, Channel: { } channel }) return;
                if (bridges.TryGetValue((kind, channel), out var bridge)) bridge.Enqueue(line);
                else if (!routing.IsDefined(kind, channel)) fallback?.Enqueue(line);
            });
    }

    static DiscordBridge StartRoute(IModContext mod, DiscordRoute route, ChatChannelRouting routing, string token)
    {
        string room = route.IsFallback ? "fallback" : $"{route.Kind} {route.Channel}";
        string packetLabel = $"0x0017 Discord {room}";
        ChatChannelKind[] kinds = route.IsFallback ? [ChatChannelKind.Global, ChatChannelKind.Trade] : [route.Kind];

        bool Eligible(ClientState conn, ChatChannelKind kind) => ChatRoom.IsListener(conn)
            && ChatRoutes.Membership(Program.CharacterName(conn), kind) is { } membership
            && (route.IsFallback ? !routing.IsDefined(kind, membership.Channel) : membership.Channel == route.Channel);

        bool HasAudience()
        {
            foreach (var player in Program.InGame)
                if (kinds.Any(kind => Eligible(player.Key, kind))) return true;
            return false;
        }
        async Task Deliver(string name, string text)
        {
            foreach (var player in Program.InGame)
            {
                var conn = player.Key;
                if (conn.Outbox is not { } output) continue;
                foreach (var kind in kinds)
                {
                    if (!Eligible(conn, kind)) continue;
                    var channel = kind == ChatChannelKind.Trade ? TypedChannel.Trade : TypedChannel.Global;
                    try { await output(IncomingLine(name, text, channel), packetLabel); }
                    catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                }
            }
        }
        var bridge = new DiscordBridge(route, token, Deliver, HasAudience,
            message => mod.Log($"{room}: {message}"));
        mod.Lifetime.Own(bridge);
        bridge.Start();
        mod.Log($"Discord bridge enabled for {room}.");
        return bridge;
    }
}
