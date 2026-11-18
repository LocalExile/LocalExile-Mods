using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

sealed class DiscordBridge : IDisposable
{
    sealed record Pending(string Text, string Nonce, DateTimeOffset Created);
    const string CursorKey = "LocalExile.DiscordChat.Cursors";
    readonly DiscordRoute route;
    readonly DiscordApi api;
    readonly HttpClient http;
    readonly Func<string, string, Task> deliver;
    readonly Func<bool> audience;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly ConcurrentDictionary<string, ulong> cursors;
    readonly string key;
    readonly Channel<Pending> outgoing = Channel.CreateBounded<Pending>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly CancellationTokenSource stop = new();
    Task? running;
    int disposed;
    int sendingDenied;
    bool warnedEmptyContent;

    internal DiscordBridge(DiscordRoute route, string token, Func<string, string, Task> deliver,
        Func<bool> audience, Action<string> log, HttpClient? http = null, ConcurrentDictionary<string, ulong>? cursors = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null, TimeProvider? time = null)
    {
        this.route = route; this.deliver = deliver; this.audience = audience; this.log = log;
        this.time = time ?? TimeProvider.System;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        api = new(this.http, token, wait, this.time);
        this.cursors = cursors ?? SharedCursors();
        key = route.ChannelId;
    }

    static ConcurrentDictionary<string, ulong> SharedCursors()
    {
        if (AppContext.GetData(CursorKey) is ConcurrentDictionary<string, ulong> saved) return saved;
        var created = new ConcurrentDictionary<string, ulong>();
        AppContext.SetData(CursorKey, created);
        return created;
    }

    internal bool Enqueue(ChatFrame line)
    {
        if (!route.SendToDiscord || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref sendingDenied) != 0
            || line is not { Type: ChatFrame.Line, Kind: ChatChannelKind.Global or ChatChannelKind.Trade, Channel: not null }
            || (!route.IsFallback && (line.Kind != route.Kind || line.Channel != route.Channel))
            || !ChatWire.ValidMember(line.Name)) return false;
        string text = ChatWire.Clean(line.Text);
        if (string.IsNullOrWhiteSpace(text)) return false;
        return outgoing.Writer.TryWrite(new($"[Game] {line.Name}: {text}", Guid.NewGuid().ToString("N")[..24], time.GetUtcNow()));
    }

    internal void Start() => running ??= Task.Run(Run);

    async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (audience()) await PollOnce(stop.Token).ConfigureAwait(false);
                else cursors.TryRemove(key, out _);
                await SendQueued(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (DiscordApiException error) when (error.Status is 401 or 403 or 404)
            {
                log(error.Status switch
                {
                    401 => "Discord route stopped (HTTP 401). Verify the configured bot token and any LOCALEXILE_DISCORD_BOT_TOKEN override.",
                    403 => $"Discord route stopped (HTTP 403). Verify the bot is installed and has View Channel and Read Message History for channel {route.ChannelId}.",
                    _ => $"Discord route stopped (HTTP 404). Check channel ID {route.ChannelId} and the bot's visibility of that channel."
                });
                break;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or DiscordApiException
                or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
            { log($"Discord bridge will retry ({error.GetType().Name}); local chat is unaffected."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        }
        outgoing.Writer.TryComplete();
    }

    internal async Task PollOnce(CancellationToken cancel = default)
    {
        if (!cursors.TryGetValue(key, out ulong since))
        {
            // Establish the live boundary from Discord, not the machine clock, and never dump old history on entry.
            var latest = await api.Request(HttpMethod.Get, $"channels/{route.ChannelId}/messages?limit=1", null, cancel).ConfigureAwait(false);
            if (latest.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid Discord message list.");
            ulong newest = 0;
            foreach (var row in latest.EnumerateArray())
                if (TryReadMessageId(row, out ulong id)) newest = Math.Max(newest, id);
            cursors[key] = newest;
            return;
        }
        var messages = new Dictionary<ulong, JsonElement>();
        string query = "after=" + since;
        // Fetch older pages too when the API returns the newest page first. Bound reconnect catch-up to 1,000 lines.
        for (int page = 0; page < 10; page++)
        {
            var rows = await api.Request(HttpMethod.Get, $"channels/{route.ChannelId}/messages?{query}&limit=100", null, cancel).ConfigureAwait(false);
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid Discord message list.");
            ulong oldest = ulong.MaxValue;
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryReadMessageId(row, out ulong id)) continue;
                oldest = Math.Min(oldest, id);
                if (id > since) messages.TryAdd(id, row);
            }
            if (rows.GetArrayLength() < 100 || oldest <= since || oldest == ulong.MaxValue) break;
            query = "before=" + oldest;
        }
        foreach (var (id, message) in messages.OrderBy(m => m.Key))
        {
            var author = message.GetProperty("author");
            if (!(author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
                && !message.TryGetProperty("webhook_id", out _))
            {
                string text = IncomingText(message);
                if (!string.IsNullOrWhiteSpace(text))
                    await deliver(IncomingName(author), text).ConfigureAwait(false);
                else if (!warnedEmptyContent)
                {
                    warnedEmptyContent = true;
                    log("Discord returned a human message without readable text. If it contained text, enable Message Content Intent under Developer Portal > Bot, then send a new message.");
                }
            }
            cursors[key] = id;
        }
    }

    bool TryReadMessageId(JsonElement row, out ulong id)
    {
        id = 0;
        return (!row.TryGetProperty("channel_id", out var channel)
                || channel.ValueKind == JsonValueKind.String && channel.GetString() == route.ChannelId)
            && row.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
            && ulong.TryParse(value.GetString(), out id) && id != 0;
    }

    internal async Task SendQueued(CancellationToken cancel = default)
    {
        for (int i = 0; i < 5 && outgoing.Reader.TryPeek(out var item); i++)
        {
            if (time.GetUtcNow() - item.Created > TimeSpan.FromMinutes(5)) { outgoing.Reader.TryRead(out _); continue; }
            string body = new JsonObject
            {
                ["content"] = item.Text,
                ["nonce"] = item.Nonce,
                ["enforce_nonce"] = true,
                ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }
            }.ToJsonString();
            try { await api.Request(HttpMethod.Post, $"channels/{route.ChannelId}/messages", body, cancel).ConfigureAwait(false); }
            catch (DiscordApiException error) when (error.Status == 403)
            {
                Volatile.Write(ref sendingDenied, 1);
                outgoing.Writer.TryComplete();
                while (outgoing.Reader.TryRead(out _)) { }
                log($"Discord posting disabled (HTTP 403) for channel {route.ChannelId}. Incoming relay remains enabled. Check Send Messages or Send Messages in Threads, then reload the module.");
                return;
            }
            catch (DiscordApiException error) when (error.Status == 400)
            { log("Discord rejected a message; it was discarded without affecting game chat."); }
            outgoing.Reader.TryRead(out _);
        }
    }

    static string IncomingName(JsonElement author)
    {
        static string SenderName(string? name) => string.Concat((name ?? "")
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(23));
        if (author.TryGetProperty("global_name", out var display) && display.ValueKind == JsonValueKind.String
            && SenderName(display.GetString()) is { Length: >= 3 } displayName)
            return displayName;
        string username = SenderName(author.GetProperty("username").GetString());
        return username.Length >= 3 ? username : "DiscordUser";
    }

    static string IncomingText(JsonElement message)
    {
        string text = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
        if (message.TryGetProperty("mentions", out var mentions))
            foreach (var mention in mentions.EnumerateArray())
            {
                string? id = mention.GetProperty("id").GetString();
                string name = mention.GetProperty("username").GetString() ?? "user";
                text = text.Replace("<@" + id + ">", "@" + name).Replace("<@!" + id + ">", "@" + name);
            }
        if (message.TryGetProperty("attachments", out var attachments))
            foreach (var attachment in attachments.EnumerateArray().Take(3))
                if (attachment.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == "https")
                    text += " " + uri.AbsoluteUri;
        return ChatWire.Clean(text.Replace('\n', ' ').Replace('\r', ' '));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); outgoing.Writer.TryComplete();
        running?.GetAwaiter().GetResult();
        http.Dispose(); stop.Dispose();
    }
}
