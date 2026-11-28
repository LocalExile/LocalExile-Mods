// GM Web Plus: the GM command panel in the browser, served on loopback, with the toolbox extras on top of what
// gm-web does -- a save snapshot taken before the first command is run (the fuse), /fuse to snapshot on demand,
// and a /ping that the panel polls to show the bridge and fuse state.
//
// A picked command runs the way typing it in the client's chat does: on the online player's connection, through
// the same command table the chat path builds (Program.Commands), under the frames gate. Nothing is simulated --
// no clipboard, no keystrokes, no window focus. The reply goes to the in-game chat.
//
// The fuse exists because characters.db runs in WAL mode: while the server is up, a plain file copy of it is a
// database that looks healthy and is quietly missing everything still in the -wal file. The snapshot goes through
// SQLite's own backup API instead, so it is whole. lx-backup.py (bundled) does that part and refuses to restore
// while the servers are running, because a Gateway shutdown writes its in-memory state back over the restore.
//
// Loopback only, one machine's own page: a request whose Host is not ours is a DNS-rebinding page and is refused,
// and a POST carrying a foreign Origin is refused too, so a site the player visits cannot run commands in game.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameServer.Modding;

public sealed class GmWebPlusMod : IMod, IDisposable
{
    const string Version = "1.0.0";

    // Settings (settings.json). The port is a setting so this mod can run beside gm-web, which serves 6111.
    sealed record Choices(int Port, bool Fuse, int FuseMinutes, int Keep);
    Choices choices = new(6111, true, 10, 12);
    int Port => choices.Port;
    int FuseMinutes => choices.FuseMinutes;

    static Choices Clamp(Choices c) => c with
    {
        Port = Math.Clamp(c.Port, 1024, 65535),
        FuseMinutes = Math.Clamp(c.FuseMinutes, 0, 1440),
        Keep = Math.Clamp(c.Keep, 1, 200),
    };

    IModContext mod = null!;
    CancellationTokenSource? stopping;
    TcpListener? listener;
    string folder = "";

    DateTime fuseAt = DateTime.MinValue;
    string fuseStamp = "";
    long fuseBytes;

    public void Dispose()
    {
        stopping?.Cancel();
        stopping = null;
        // Freed now, not when the accept loop next wakes: a reload's new instance binds the same port right away.
        listener?.Stop();
        listener = null;
    }

    public void Load(IModContext context)
    {
        mod = context;
        folder = context.Folder ?? "";
        choices = Clamp(mod.Settings<Choices>());
        stopping = new CancellationTokenSource();
        var bound = new TcpListener(IPAddress.Loopback, Port);
        try { bound.Start(); }
        catch (SocketException e)
        {
            mod.Log($"cannot listen on 127.0.0.1:{Port}: {e.Message} -- is gm-web still enabled?");
            return;
        }
        listener = bound;
        var token = stopping.Token;
        _ = Task.Run(() => Serve(bound, token));
    }

    async Task Serve(TcpListener listener, CancellationToken cancelled)
    {
        mod.Log(choices.Fuse
            ? $"the GM page is at http://127.0.0.1:{Port}/  (fuse: snapshot before the first command, reused {FuseMinutes} min)"
            : $"the GM page is at http://127.0.0.1:{Port}/  (fuse off: /fuse still snapshots on demand)");
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
            client.SendTimeout = 15000;
            var stream = client.GetStream();
            string? request = await ReadRequest(stream);
            if (request is null) { client.Close(); return; }
            string[] head = request.Split("\r\n");
            string[] requestLine = head[0].Split(' ');
            if (requestLine.Length < 2) { await Write(stream, 400, "text/plain", "bad request"u8.ToArray()); return; }
            string method = requestLine[0];
            string target = requestLine[1];
            string path = target.Split('?')[0];
            string query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            // Loopback only, one machine's own page: a request whose Host is not ours came from a page that
            // resolved another name to this port (DNS rebinding), and a POST carrying a foreign Origin from another
            // site. Both are refused, so a site the player visits cannot run commands in game.
            string? host = Header(head, "Host"), origin = Header(head, "Origin");
            string ours1 = $"127.0.0.1:{Port}", ours2 = $"localhost:{Port}";
            bool hostOk = host is not null && (host.Equals(ours1, StringComparison.OrdinalIgnoreCase)
                                              || host.Equals(ours2, StringComparison.OrdinalIgnoreCase));
            bool originOk = origin is null
                || origin.Equals("http://" + ours1, StringComparison.OrdinalIgnoreCase)
                || origin.Equals("http://" + ours2, StringComparison.OrdinalIgnoreCase);
            if (!hostOk || method == "POST" && !originOk)
            { await Write(stream, 403, "text/plain", "forbidden"u8.ToArray()); return; }

            byte[] body = BodyAfter(request);

            if (method == "GET" && path is "/" or "/index.html")
            {
                byte[]? page = await Page();
                if (page is null) { await Write(stream, 404, "text/plain; charset=utf-8", "the panel is missing"u8.ToArray()); return; }
                await Write(stream, 200, "text/html; charset=utf-8", page);
            }
            else if (method == "GET" && path == "/ping")
                await Write(stream, 200, "application/json; charset=utf-8", PingJson());
            else if (method == "GET" && path == "/fuse")
                await Write(stream, 200, "application/json; charset=utf-8", await FuseNow());
            else if (method == "GET" && path is "/send" or "/api/send")
                await RunCommand(stream, QueryValue(query, "cmd"));
            else if (method == "POST" && path is "/run" or "/send")
                await RunCommand(stream, Encoding.UTF8.GetString(body).Trim());
            else await Write(stream, 404, "text/plain", "not found"u8.ToArray());
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        catch (Exception e) { mod.Log("request failed: " + e.Message); }
        finally { try { client.Close(); } catch (SocketException) { } }
    }

    // ---- running a command -----------------------------------------------------------------------------------

    async Task RunCommand(NetworkStream stream, string command)
    {
        command = command.Trim();
        if (!command.StartsWith('/'))
        {
            await Write(stream, 400, "application/json; charset=utf-8", Json(new { ok = false, error = "not a command" }));
            return;
        }
        var player = OnlinePlayer();
        if (player is null)
        {
            await Write(stream, 503, "application/json; charset=utf-8",
                Json(new { ok = false, error = "nobody is in game" }));
            return;
        }

        // The fuse: give the saves a snapshot before anything is run, once per window. A failed snapshot does not
        // stop the command -- it is reported instead, so a broken Python cannot lock the panel.
        string fuse = "off";
        if (!choices.Fuse) fuse = "off";
        else if (choices.FuseMinutes <= 0 || DateTime.UtcNow - fuseAt > TimeSpan.FromMinutes(choices.FuseMinutes))
        {
            var result = await Snapshot();
            fuse = result.Ok ? "fresh" : "failed";
            if (!result.Ok) mod.Log("fuse: snapshot failed: " + result.Error);
        }
        else fuse = "reused";

        // The typed-chat path rebuilds the connection's command table for every command (a mod reload empties it,
        // Program.ForgetCommands); build the same table here, so a run right after a reload works too. The frames
        // gate keeps this from racing a frame (AreaFrames.Run).
        await player.Frames.Run(async () =>
        {
            var commands = Program.Commands(player);
            player.Commands = commands;
            await commands.RunAsync(command);
        });

        await Write(stream, 200, "application/json; charset=utf-8", Json(new
        {
            ok = true,
            ran = command,
            fuse,
            stamp = fuseStamp,
            game = GameName(),
        }));
    }

    // ---- the fuse --------------------------------------------------------------------------------------------

    async Task<byte[]> FuseNow()
    {
        var result = await Snapshot();
        return result.Ok
            ? Json(new { ok = true, stamp = fuseStamp, bytes = fuseBytes, seconds = Math.Round(result.Seconds, 2), where = BackupFolder() })
            : Json(new { ok = false, error = result.Error });
    }

    sealed record SnapshotResult(bool Ok, double Seconds, string Error);

    async Task<SnapshotResult> Snapshot()
    {
        string script = Path.Combine(folder, "lx-backup.py");
        if (!File.Exists(script)) return new SnapshotResult(false, 0, "lx-backup.py is missing beside mod.json");
        string? python = FindPython();
        if (python is null) return new SnapshotResult(false, 0, "no Python found (looked at PATH and Tools\\python)");

        var clock = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (string arg in new[] { script, "--db", Path.Combine(Environment.CurrentDirectory, "data"), "--keep", choices.Keep.ToString(), "--label", "gm-web-plus" })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return new SnapshotResult(false, 0, "python did not start");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            clock.Stop();
            if (process.ExitCode != 0)
                return new SnapshotResult(false, clock.Elapsed.TotalSeconds,
                    $"lx-backup.py exit {process.ExitCode}: {Shorten(stderr.Length > 0 ? stderr : stdout)}");

            // The last line is the JSON lx-backup.py prints for the bridge to read.
            string last = stdout.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            using var doc = JsonDocument.Parse(last);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("ok", out var ok) && ok.GetBoolean()))
                return new SnapshotResult(false, clock.Elapsed.TotalSeconds, "snapshot not taken");

            fuseStamp = root.TryGetProperty("stamp", out var s) ? s.GetString() ?? "" : "";
            fuseBytes = root.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0;
            fuseAt = DateTime.UtcNow;
            return new SnapshotResult(true, clock.Elapsed.TotalSeconds, "");
        }
        catch (Exception e)
        {
            clock.Stop();
            return new SnapshotResult(false, clock.Elapsed.TotalSeconds, Shorten(e.Message));
        }
    }

    static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    static string? FindPython()
    {
        string exe = OperatingSystem.IsWindows() ? "python.exe" : "python3";
        string[] roots =
        [
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        ];
        foreach (string root in roots)
        {
            string shipped = Path.Combine(root, "Tools", "python", exe);
            if (File.Exists(shipped)) return shipped;
        }
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
            foreach (string dir in path.Split(Path.PathSeparator))
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        return null;
    }

    string BackupFolder() => Path.Combine(Environment.CurrentDirectory, "data", "gm-backups");

    // ---- what the panel reads --------------------------------------------------------------------------------

    byte[] PingJson()
    {
        double age = (DateTime.UtcNow - fuseAt).TotalSeconds;
        bool has = fuseStamp.Length > 0;
        return Json(new
        {
            ok = true,
            version = 2,
            port = Port,
            mod = Version,
            game = GameName(),
            fuse = new
            {
                on = true,
                strict = false,
                stamp = has ? fuseStamp : null,
                age = has ? Math.Round(age, 1) : -1,
                window = FuseMinutes * 60,
                bytes = fuseBytes,
                where = BackupFolder(),
            },
        });
    }

    static string? GameName()
    {
        var player = OnlinePlayer();
        return player is null ? null : $"Path of Exile ({player.Character.Name})";
    }

    static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

    // ---- the page ----------------------------------------------------------------------------------------------

    // The page this mod ships, or a gm.html dropped into the server folder in its place, so a server can carry its
    // own page -- another language, other buttons -- without touching the mod.
    async Task<byte[]?> Page()
    {
        string[] candidates =
        [
            Path.Combine(Environment.CurrentDirectory, "gm.html"),
            Path.Combine(folder, "panel.html"),
            Path.Combine(folder, "gm.html"),
        ];
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return await File.ReadAllBytesAsync(candidate);
        return null;
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------

    static Player? OnlinePlayer()
    {
        foreach (var player in Program.InGame.Keys)
            if (!player.Closed && !player.LeavingArea && player.Commands is not null)
                return player;
        return null;
    }

    static string QueryValue(string query, string name)
    {
        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return "";
    }

    /// <summary>One request's head and body: reads until the head's blank line, then Content-Length bytes more.</summary>
    static async Task<string?> ReadRequest(NetworkStream stream)
    {
        var received = new List<byte>(4096);
        var buffer = new byte[8192];
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

    static async Task Write(NetworkStream stream, int status, string type, byte[] body)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Not Found",
        };
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                      "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
