using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace LocalExile.Mods.WaypointTools;

/// <summary>
/// The way Waypoint's tools reach the game without the client control bridge (README.md). Waypoint writes a request into
/// <c>data/waypoint-tools/requests/</c>; the mod carries it out on the next tick of that character's connection, so what it
/// changes and sends goes through the connection's own task, as a chat command would, and answers in
/// <c>data/waypoint-tools/replies/</c>. Nothing listens on the network.
/// </summary>
public sealed class WaypointToolsMod : IMod, IDisposable
{
    /// <summary>The version of the request and reply files; Waypoint reads it from <c>loaded.json</c>.</summary>
    const int Protocol = 1;

    /// <summary>How long a request waits for its character to be in game before it is answered "not in game".</summary>
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    static readonly string Root = Path.Combine("data", "waypoint-tools");
    static readonly string Requests = Path.Combine(Root, "requests");
    static readonly string Replies = Path.Combine(Root, "replies");
    static readonly string Loaded = Path.Combine(Root, "loaded.json");

    sealed record Request(string Id, string File, string Action, string Character, string Item, DateTime Written);

    // The requests read and not yet answered, by id. A tick claims one by removing it.
    readonly ConcurrentDictionary<string, Request> _pending = new(StringComparer.OrdinalIgnoreCase);
    // Every request this load took, answered or not: while a tick gives one, its file is still there until the answer,
    // and reading it again would give the item twice. Under _scanning.
    readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _scanning = new();
    // When each character's connection last ticked (Environment.TickCount64): who is in game, for a request that names nobody.
    readonly ConcurrentDictionary<string, long> _ticked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How recently a character's connection must have ticked to count as in game (ticks are about 4 a second).</summary>
    const long InGameMilliseconds = 2000;

    /// <summary>The characters whose connections ticked within <see cref="InGameMilliseconds"/>.</summary>
    List<string> InGame()
    {
        long now = Environment.TickCount64;
        return [.. _ticked.Where(t => now - t.Value <= InGameMilliseconds).Select(t => t.Key)];
    }
    IModContext _mod = null!;
    Timer? _scan;
    bool _disposed;

    /// <inheritdoc/>
    public void Load(IModContext mod)
    {
        _mod = mod;
        Directory.CreateDirectory(Requests);
        Directory.CreateDirectory(Replies);
        try { Serving(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { mod.Log($"writing {Loaded} failed: {e.Message}"); }
        GameEvents.Tick.Add(mod.Manifest.Id, (conn, _) => Tick(conn));
        _scan = new Timer(_ => Scan(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_scanning) _disposed = true;
        using (var stopped = new ManualResetEvent(false))
            if (_scan?.Dispose(stopped) == true) stopped.WaitOne(TimeSpan.FromSeconds(2));
        // What was not answered stays in requests/: the next load reads it again, with its own age. The file goes only
        // when it is this Gateway's: another one's stays.
        try { if (Named() is var (pid, _) && pid == Environment.ProcessId) File.Delete(Loaded); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Whether this Gateway is the one <c>loaded.json</c> names, the one Waypoint's requests are for: the first to load
    /// the mod in this folder, for as long as it runs. It takes the file when no running Gateway has it.
    ///
    /// A second Gateway in the same folder is one that was only asked a question: its <c>commands</c> and <c>values</c>
    /// modes load the mods too, for their commands, and Waypoint's Console asks when it starts. Such a Gateway serves no
    /// player and ends in a moment, so it must leave the file and the requests to the one that runs the game; written
    /// by it, the file named a process that had ended, and Waypoint took the mod for not loaded. Should the named
    /// Gateway end without unloading, the next scan of another takes its place.
    /// </summary>
    bool Serving()
    {
        if (Named() is var (pid, started))
        {
            if (pid == Environment.ProcessId) return true;
            if (Runs(pid, started)) return false;
        }
        using var me = Process.GetCurrentProcess();
        WriteAtomically(Loaded, new JsonObject
        {
            ["protocol"] = Protocol,
            ["version"] = _mod.Manifest.Version,
            ["pid"] = Environment.ProcessId,
            // With the pid, which process: a number of an ended Gateway may be another process's by now.
            ["started"] = me.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["actions"] = new JsonArray("item_give"),
        });
        return true;
    }

    /// <summary>The process <c>loaded.json</c> names and when it started (null from a version that did not write it), or
    /// null without a readable file.</summary>
    static (int Pid, DateTime? Started)? Named()
    {
        try
        {
            if (!File.Exists(Loaded) || JsonNode.Parse(File.ReadAllText(Loaded)) is not JsonObject json || (int?)json["pid"] is not int pid) return null;
            return (pid, DateTime.TryParse((string?)json["started"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                ? started.ToUniversalTime() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether that process still runs and is the one that wrote the file: started when the file says, or,
    /// without a start time, a program of this Gateway's name.</summary>
    static bool Runs(int pid, DateTime? started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (started is { } at) return Math.Abs((process.StartTime.ToUniversalTime() - at).TotalSeconds) < 2;
            using var me = Process.GetCurrentProcess();
            return string.Equals(process.ProcessName, me.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads the new requests, refuses those it cannot read, and answers those that waited too long.</summary>
    void Scan()
    {
        lock (_scanning)
        {
            if (_disposed) return;
            try
            {
                // The requests are for the Gateway the file names.
                if (!Serving()) return;
                foreach (string file in Directory.EnumerateFiles(Requests, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    if (_taken.Contains(id)) continue;
                    if (Read(id, file) is { } request && _taken.Add(id)) _pending[id] = request;
                }
                DateTime now = DateTime.UtcNow;
                var inGame = InGame();
                foreach (var request in _pending.Values)
                {
                    if (request.Character.Length == 0 && inGame.Count > 1 && _pending.TryRemove(request.Id, out _))
                        Answer(request, false, $"Several characters are in game ({string.Join(", ", inGame)}): name one");
                    else if (now - request.Written > Wait && _pending.TryRemove(request.Id, out _))
                        Answer(request, false, request.Character.Length > 0 ? $"{request.Character} is not in game" : "No character is in game");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _mod.Log($"reading {Requests} failed: {e.Message}");
            }
        }
    }

    /// <summary>The request in a file, or null when it is refused (and answered) or not readable yet.</summary>
    Request? Read(string id, string file)
    {
        DateTime written;
        string text;
        try
        {
            written = File.GetLastWriteTimeUtc(file);
            text = File.ReadAllText(file);
        }
        catch (IOException) { return null; } // still being written: the next scan reads it
        var refused = new Request(id, file, "", "", "", written);
        try
        {
            if (JsonNode.Parse(text) is not JsonObject json) { Answer(refused, false, "A request is one JSON object"); return null; }
            string action = (string?)json["action"] ?? "";
            string character = (string?)json["character"] ?? "";
            if (action != "item_give") { Answer(refused, false, $"Unknown action \"{action}\""); return null; }
            if (json["item"] is not { } item || item is not (JsonObject or JsonArray)) { Answer(refused, false, "Which item?"); return null; }
            return new Request(id, file, action, character, item.ToJsonString(), written);
        }
        catch (JsonException e)
        {
            Answer(refused, false, $"Not JSON: {e.Message}");
            return null;
        }
    }

    /// <summary>On a connection's own tick: carries out the requests for its character.</summary>
    async Task Tick(ClientState conn)
    {
        if (conn is not { PlayerSent: true, Character: { Length: > 0 } character }) return;
        _ticked[character] = Environment.TickCount64;
        if (_pending.IsEmpty) return;
        foreach (var request in _pending.Values)
        {
            bool mine = request.Character.Length > 0
                ? string.Equals(request.Character, character, StringComparison.OrdinalIgnoreCase)
                : InGame() is [var only] && string.Equals(only, character, StringComparison.OrdinalIgnoreCase);
            if (!mine || !_pending.TryRemove(request.Id, out _)) continue;
            var (ok, message) = await GiveItems(conn, request.Item);
            Answer(request, ok, message);
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChatLine($"Waypoint: {message}"),
                $"waypoint-tools: {message}");
        }
    }

    /// <summary>Builds each item with the Gateway's own ItemCrafter.Build (the item format of LocalExile's
    /// Agents/docs/dev/item-crafter.md) and puts it into the main inventory, one object or each item of a JSON array in
    /// order, until one is refused or does not fit. Self-contained: no other mod is needed.</summary>
    static async Task<(bool Ok, string Message)> GiveItems(ClientState conn, string json)
    {
        if (ItemsFor(conn).Error is { } error) return (false, error);
        var items = ItemsOf(conn);
        var added = new List<InventoryItem>();
        string? failure = null, singleName = null;
        foreach (string spec in Specs(json))
        {
            if (ItemCrafter.Build(ItemsFor(conn), RollsFor(conn), spec, conn.Pools.Level, Random.Shared, out string? why) is not { } created
                || ItemsFor(conn).ByHash(created.TypeHash) is not { } type)
            { failure = why ?? "Unknown item type"; break; }
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
            { failure = "No room in the inventory"; break; }
            added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created)));
            singleName = $"{(created.NameWords.Count > 0 ? RollsFor(conn).NameOf(created.NameWords) + " " : "")}{type.Name}";
        }
        if (added.Count == 0) return (false, failure ?? "No room in the inventory");
        await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], added),
            $"0x00bf inventory 1: waypoint-tools item_give × {added.Count}");
        return failure is null
            ? (true, added.Count == 1 && singleName is not null ? $"Added {singleName}" : $"Added {added.Count} item(s)")
            : (false, $"Added {added.Count} item(s), then stopped: {failure}");
    }

    /// <summary>One JSON object, or every element of a JSON array in order.</summary>
    static List<string> Specs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? [.. document.RootElement.EnumerateArray().Select(element => element.GetRawText())]
            : [json];
    }

    /// <summary>Writes the reply, then removes the request: a request without a reply is still waiting.</summary>
    void Answer(Request request, bool ok, string message)
    {
        try
        {
            WriteAtomically(Path.Combine(Replies, request.Id + ".json"), new JsonObject
            {
                ["protocol"] = Protocol,
                ["id"] = request.Id,
                ["ok"] = ok,
                ["message"] = message,
            });
            File.Delete(request.File);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _mod.Log($"answering {request.Id} failed: {e.Message}");
        }
        _mod.Log($"{request.Id} {(request.Character.Length > 0 ? request.Character + " " : "")}{(ok ? "done" : "refused")}: {message}");
    }

    /// <summary>A reader never sees half a file: it is written beside, then moved into place.</summary>
    static void WriteAtomically(string path, JsonNode json)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }
}
