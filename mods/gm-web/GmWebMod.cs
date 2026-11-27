using System.Net.Sockets;
using System.Text;
using GameServer.Modding;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// Serves the GM command page at http://127.0.0.1:6111/ and runs the commands its buttons pick on the player in
/// game, so picking a command in the browser takes effect without typing it in the client's chat. Loopback only,
/// one machine's own page; a request body that is not a slash command is refused.
/// </summary>
public sealed class GmWebMod : IMod, IDisposable
{
    const int Port = 6111;
    CancellationTokenSource? _stopping;
    TcpListener? _listener;
    string? _folder;

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
    public void Load(IModContext mod)
    {
        _folder = mod.Folder;
        _stopping = new();
        var listener = new TcpListener(System.Net.IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch (SocketException e)
        {
            Console.WriteLine($"[gm-web] cannot listen on 127.0.0.1:{Port}: {e.Message}");
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
            if (host is not ("127.0.0.1:6111" or "localhost:6111")
                || method == "POST" && origin is not (null or "http://127.0.0.1:6111" or "http://localhost:6111"))
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
            else if (method == "GET" && path is "/tp" or "/tp/")
                await TpApi.Handle(stream, method);
            else if (method == "GET" && path is "/names" or "/names/")
                await NamesApi.Handle(stream, method);
            else if (method == "POST" && path == "/run")
            {
                string command = Encoding.UTF8.GetString(body).Trim();
                if (!command.StartsWith('/')) { await Write(stream, 400, "text/plain; charset=utf-8", "not a command"u8.ToArray()); return; }
                var player = OnlinePlayer();
                if (player is null) { await Write(stream, 503, "text/plain; charset=utf-8", "nobody is in game"u8.ToArray()); return; }
                // The typed-chat path rebuilds the connection's table for every command (a mod reload empties it,
                // Program.ForgetCommands); build the same table here, so a run right after a reload works too. The
                // frames gate keeps this from racing a frame (AreaFrames.Run), and the reply reaches the chat as usual.
                await player.Frames.Run(async () =>
                {
                    var commands = Program.Commands(player);
                    player.Commands = commands;
                    await commands.RunAsync(command);
                });
                await Write(stream, 200, "text/plain; charset=utf-8", "ok"u8.ToArray());
            }
            else await Write(stream, 404, "text/plain", "not found"u8.ToArray());
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        finally { try { client.Close(); } catch (SocketException) { } }
    }

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
