using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameServer.Modding;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// Serves the GM command page at http://127.0.0.1:6111/ and runs the commands its buttons pick on the player in
/// game, so picking a command in the browser takes effect without typing it in the client's chat. Loopback only,
/// one machine's own page; a request body that is not a slash command is refused.
///
/// Before the first command of a window is run, the saves are snapshotted (Backup.cs), so a command that goes wrong
/// can be undone: the fuse. <c>GET /ping</c> reports the fuse's state for the page's header, and <c>POST /fuse</c>
/// takes a snapshot on demand.
/// </summary>
public sealed class GmWebMod : IMod, IDisposable
{
    const string Version = "1.0.1";

    /// <summary>Settings (settings.json). The port is a setting so a second page can run beside this one.</summary>
    sealed record Choices(int Port, bool Fuse, int FuseMinutes, int Keep);

    static Choices Clamp(Choices c) => c with
    {
        Port = Math.Clamp(c.Port, 1024, 65535),
        FuseMinutes = Math.Clamp(c.FuseMinutes, 0, 1440),
        Keep = Math.Clamp(c.Keep, 1, 200),
    };

    IModContext mod = null!;
    Choices choices = new(6111, true, 10, 12);
    CancellationTokenSource? _stopping;
    TcpListener? _listener;
    string? _folder;

    DateTime _fuseAt = DateTime.MinValue;
    string _fuseStamp = "";
    long _fuseBytes;

    int Port => choices.Port;

    /// <inheritdoc/>
    public void Dispose()
    {
        _stopping?.Cancel();
        _stopping = null;
        // Freed now, not when the accept loop next wakes: a reload's new instance binds the same port right away.
        _listener?.Stop();
        _listener = null;
    }

    /// <inheritdoc/>
    public void Load(IModContext context)
    {
        mod = context;
        _folder = context.Folder;
        choices = Clamp(context.Settings<Choices>());
        _stopping = new();
        var listener = new TcpListener(System.Net.IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch (SocketException e)
        {
            context.Log($"cannot listen on 127.0.0.1:{Port}: {e.Message}");
            return;
        }
        _listener = listener;
        var stopping = _stopping.Token;
        _ = Task.Run(() => Serve(listener, stopping));
    }

    async Task Serve(TcpListener listener, CancellationToken cancelled)
    {
        Console.WriteLine($"[gm-web] the GM page is at http://127.0.0.1:{Port}/");
        while (!cancelled.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancelled); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (!cancelled.IsCancellationRequested) { continue; }
            catch (SocketException) { break; }
            _ = Task.Run(() => Answer(client), cancelled);
        }
        listener.Stop();
    }

    async Task Answer(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();
            string? request = await ReadRequest(stream);
            if (request is null) { client.Close(); return; }
            string[] head = request.Split("\r\n");
            string[] requestLine = head[0].Split(' ');
            if (requestLine.Length < 2) { await Write(stream, 400, "text/plain", "bad request"u8.ToArray()); return; }
            string method = requestLine[0], path = requestLine[1].Split('?')[0];
            // Only this page may call in: a Host other than our own is a DNS-rebinding page, and a browser marks a POST
            // from another site with its Origin, so a site the player visits cannot run commands in game.
            string? host = Header(head, "Host"), origin = Header(head, "Origin");
            string hostOwn = $"127.0.0.1:{Port}", hostAlias = $"localhost:{Port}";
            if (host is null || host != hostOwn && host != hostAlias
                || method == "POST" && origin is not null && origin != "http://" + hostOwn && origin != "http://" + hostAlias)
            { await Write(stream, 403, "text/plain", "forbidden"u8.ToArray()); return; }
            byte[] body = BodyAfter(request);
            if (method == "GET" && path is "/" or "/index.html")
            {
                // The page this mod ships, or a gm.html dropped into the server folder in its place.
                string own = Path.Combine(_folder ?? "", "gm.html");
                string serverFolder = Path.Combine(Environment.CurrentDirectory, "gm.html");
                string page = File.Exists(serverFolder) ? serverFolder : own;
                if (!File.Exists(page)) { await Write(stream, 404, "text/plain; charset=utf-8", "gm.html is missing"u8.ToArray()); return; }
                await Write(stream, 200, "text/html; charset=utf-8", await File.ReadAllBytesAsync(page));
            }
            else if (method == "GET" && path == "/online")
            {
                var player = OnlinePlayer();
                await Write(stream, 200, "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes(player?.Character.Name ?? ""));
            }
            else if (method == "GET" && path == "/ping")
                await Write(stream, 200, "application/json; charset=utf-8", PingJson());
            else if (method == "POST" && path == "/fuse")
                await Write(stream, 200, "application/json; charset=utf-8", FuseNow());
            else if (method == "GET" && path is "/tp" or "/tp/")
                await TpApi.Handle(stream, method);
            else if (method == "GET" && path is "/names" or "/names/")
                await NamesApi.Handle(stream, method);
            else if (method == "POST" && path == "/run")
                await Run(stream, Encoding.UTF8.GetString(body));
            else await Write(stream, 404, "text/plain", "not found"u8.ToArray());
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        catch (Exception e) { mod?.Log("request failed: " + e.Message); }
        finally { try { client.Close(); } catch (SocketException) { } }
    }

    // ---- running a command ---------------------------------------------------------------------------------------

    async Task Run(NetworkStream stream, string command)
    {
        command = command.Trim();
        if (!command.StartsWith('/'))
        {
            await Write(stream, 400, "application/json; charset=utf-8",
                Json(new { ok = false, error = "not a command" }));
            return;
        }
        var player = OnlinePlayer();
        if (player is null)
        {
            await Write(stream, 503, "application/json; charset=utf-8",
                Json(new { ok = false, error = "nobody is in game" }));
            return;
        }

        // The fuse: one snapshot per window, before anything is run. A snapshot that fails does not stop the command
        // -- it is reported instead, so a broken backup cannot lock the panel.
        string fuse = "off";
        if (choices.Fuse)
        {
            if (choices.FuseMinutes <= 0 || DateTime.UtcNow - _fuseAt > TimeSpan.FromMinutes(choices.FuseMinutes))
            {
                var taken = Backup.Take(choices.Keep);
                if (taken.Ok) { _fuseAt = DateTime.UtcNow; _fuseStamp = taken.Stamp; _fuseBytes = taken.Bytes; fuse = "fresh"; }
                else { fuse = "failed"; mod.Log("fuse: snapshot failed: " + taken.Error); }
            }
            else fuse = "reused";
        }

        // The typed-chat path rebuilds the connection's table for every command (a mod reload empties it,
        // Program.ForgetCommands); build the same table here, so a run right after a reload works too. The
        // frames gate keeps this from racing a frame (AreaFrames.Run), and the reply reaches the chat as usual.
        await player.Frames.Run(async () =>
        {
            var commands = Program.Commands(player);
            player.Commands = commands;
            await commands.RunAsync(command);
        });

        await Write(stream, 200, "application/json; charset=utf-8",
            Json(new { ok = true, ran = command, fuse, stamp = _fuseStamp, game = GameName() }));
    }

    // ---- the fuse ------------------------------------------------------------------------------------------------

    byte[] FuseNow()
    {
        var taken = Backup.Take(choices.Keep);
        if (!taken.Ok) return Json(new { ok = false, error = taken.Error });
        _fuseAt = DateTime.UtcNow;
        _fuseStamp = taken.Stamp;
        _fuseBytes = taken.Bytes;
        return Json(new
        {
            ok = true,
            stamp = taken.Stamp,
            bytes = taken.Bytes,
            seconds = Math.Round(taken.Seconds, 2),
            where = Backup.Folder(),
        });
    }

    byte[] PingJson()
    {
        bool has = _fuseStamp.Length > 0;
        double age = has ? (DateTime.UtcNow - _fuseAt).TotalSeconds : -1;
        return Json(new
        {
            ok = true,
            version = 2,
            port = Port,
            mod = Version,
            game = GameName(),
            fuse = new
            {
                on = choices.Fuse,
                strict = false,
                stamp = has ? _fuseStamp : null,
                age,
                window = choices.FuseMinutes * 60,
                bytes = _fuseBytes,
                where = Backup.Folder(),
            },
        });
    }

    static string? GameName()
    {
        var player = OnlinePlayer();
        return player is null ? null : $"Path of Exile ({player.Character.Name})";
    }

    static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

    // ---- plumbing ------------------------------------------------------------------------------------------------

    /// <summary>One request's head and body: reads until the head's blank line, then Content-Length bytes more.</summary>
    static async Task<string?> ReadRequest(NetworkStream stream)
    {
        var received = new List<byte>(4096);
        var buffer = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOf(received, "\r\n\r\n"u8)) < 0)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0) return null;
            for (int i = 0; i < read; i++) received.Add(buffer[i]);
            if (received.Count > 1 << 20) return null;
        }
        int contentLength = 0;
        string head = Encoding.UTF8.GetString(received.Take(headerEnd).ToArray());
        foreach (string line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out int length))
                contentLength = length;
        if (contentLength > 1 << 20) return null;
        while (received.Count < headerEnd + 4 + contentLength)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0) return null;
            for (int i = 0; i < read; i++) received.Add(buffer[i]);
        }
        return Encoding.UTF8.GetString(received.Take(headerEnd + 4 + contentLength).ToArray());
    }

    static string? Header(string[] head, string name)
    {
        foreach (string line in head.Skip(1))
        {
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return line[(colon + 1)..].Trim();
        }
        return null;
    }

    static byte[] BodyAfter(string request)
    {
        int start = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return start < 0 ? [] : Encoding.UTF8.GetBytes(request[(start + 4)..]);
    }

    static int IndexOf(List<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i <= haystack.Count - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return i;
        }
        return -1;
    }

    internal static async Task Write(NetworkStream stream, int status, string type, byte[] body)
    {
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 500 ? "Internal Server Error" : status == 503 ? "Service Unavailable" : status == 403 ? "Forbidden" : "Not Found")}\r\n" +
            $"Content-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    internal static Player? OnlinePlayer()
    {
        foreach (var player in Program.InGame.Keys)
            if (!player.Closed && !player.LeavingArea && player.Commands is not null)
                return player;
        return null;
    }
}
