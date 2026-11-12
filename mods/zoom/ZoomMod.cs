using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.Zoom;

/// <summary>
/// A wider view in normal play: the camera stands further back, by each player's own level (/zoom), and the mouse wheel
/// keeps working from there.
/// <para>How: the client's own camera zoom points, the ones the game uses to pull the view back at the Beacon or in a boss
/// room. A point has a place, an inner and an outer distance and a value; every frame the camera takes the value of the
/// nearest point whose outer distance holds the player (inside the inner distance the value itself, fading to 1 at the
/// outer). The mod adds one point per level change, in a far corner of the area and reaching all of it. So the game's own
/// points, nearer wherever they apply, still win there, and nothing of theirs is removed.</para>
/// <para>The one script the server can run with a value of its own is <c>camera_zoom</c> of
/// <c>Metadata/Monsters/BossBase</c> (S→C 0x285, a call of an object's Functions entry), and the one monster type that
/// inherits it is Vruun's. The mod creates one for an instant, dead (alive it would speak), where the point goes, calls
/// the function and removes it; the point stays. It is never drawn: the client draws nothing it was not told is awake.</para>
/// </summary>
public sealed class ZoomMod : IMod
{
    /// <summary>The MonsterVarieties row whose type inherits BossBase's <c>camera_zoom</c>.</summary>
    const string Carrier = "Metadata/Monsters/Breach/WildBreachBoss";
    static readonly string[] CarrierExtras = ["Transitionable", "StateMachine", "MinimapIcon", "Functions"];

    /// <summary>A point's distances are in units of 100 world units (about 9 cells): this holds any area from any corner.</summary>
    const float Everywhere = 5000;

    /// <summary>The speed the client's camera changes zoom at when no script has set one (its SetCustomCameraSpeed takes
    /// 2.5 for a negative value); camera_zoom sets a speed, so the mod sets this one.</summary>
    const float CameraSpeed = 2.5f;

    const double Least = 1, Most = 3;
    const int CornerInset = 4;
    const string SavedKey = "zoom:level";

    /// <summary>Per connection: [0] points made in this area, [1] the level the client has (0: none made), [2] the player's own
    /// level (0: not read yet, -1: none). Kept by the process, not by the mod, in types of the runtime only: a reload of the
    /// mod must still know where its last point is, and must not be held in memory by its own state.</summary>
    static readonly ConditionalWeakTable<object, double[]> States = ProcessState();

    static ConditionalWeakTable<object, double[]> ProcessState()
    {
        const string name = "LocalExile.Mods.Zoom.States";
        lock (typeof(AppDomain))
        {
            if (AppDomain.CurrentDomain.GetData(name) is ConditionalWeakTable<object, double[]> kept) return kept;
            var made = new ConditionalWeakTable<object, double[]>();
            AppDomain.CurrentDomain.SetData(name, made);
            return made;
        }
    }

    sealed record Choices(double Level, double Step);

    Choices _choices = new(1.6, 0.2);
    IModContext _mod = null!;
    int _refused;

    public void Load(IModContext mod)
    {
        _mod = mod;
        _choices = mod.Settings<Choices>();
        string id = mod.Manifest.Id;
        // A tick only runs once the area is ready on the client, and catches every way the level can differ from what the
        // client has: a new area, a /zoom, a changed setting, the mod switched on.
        GameEvents.Tick.Add(id, (conn, _) => Follow(conn));
        GameEvents.Command(id, "zoom", "/zoom [1 to 3 | out | in | off | default] (how far back the camera stands; 1 is the game's own view)", Command);
        mod.Log($"level {Text(_choices.Level)} for players who have not picked one");
    }

    static string Text(double level) => level.ToString("0.0#", CultureInfo.InvariantCulture);

    static double[] StateOf(ClientState conn) => States.GetValue(conn, _ => new double[3]);

    /// <summary>The level this player gets: their own (/zoom, kept with the account), else the setting.</summary>
    double Wanted(ClientState conn, double[] state)
    {
        double own;
        lock (state) own = state[2];
        if (own == 0)
        {
            own = -1;
            try
            {
                if (double.TryParse(Program.WorldOf(conn).AccountData.Get(SavedKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double saved))
                    own = Math.Clamp(saved, Least, Most);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException) { }
            lock (state) if (state[2] == 0) state[2] = own; else own = state[2];
        }
        return own > 0 ? own : Math.Clamp(_choices.Level, Least, Most);
    }

    async Task Follow(ClientState conn)
    {
        if (!conn.PlayerSent || conn.LeavingArea || conn.Closed) return;
        double[] state = StateOf(conn);
        double wanted = Wanted(conn, state);
        int point;
        lock (state)
        {
            if (state[1] == wanted) return;
            // A new area starts at the game's own view: nothing to send for level 1.
            if (state[1] == 0 && wanted == 1) { state[1] = 1; return; }
            point = (int)state[0];
            state[0] = point + 1;
            state[1] = wanted;
        }
        await Place(conn, point, (float)wanted);
    }

    /// <summary>Adds the zoom point number <paramref name="point"/> of this area. Each stands one cell further in from the
    /// corner than the one before, so the newest is the nearest to a player anywhere but in the corner itself, and wins.</summary>
    async Task Place(ClientState conn, int point, float level)
    {
        if (Game.Monsters.Find(Carrier) is not { } variety || variety.StateMachine is not { } states
            || !variety.ComponentsOrBase.Skip(MonsterObject.BaseComponents.Length).Where(c => c != "LimitedLifespan").SequenceEqual(CarrierExtras))
        {
            Refuse("the game data has no usable " + Carrier);
            return;
        }
        var layout = conn.Layout;
        // The corner furthest from where players arrive; never outside the grid, where an object crashes the client.
        bool right = layout.SpawnX < layout.GridW / 2, down = layout.SpawnY < layout.GridH / 2;
        int inset = Math.Min(CornerInset + point, Math.Min(layout.GridW, layout.GridH) / 2);
        int x = right ? layout.GridW - 1 - inset : inset, y = down ? layout.GridH - 1 - inset : inset;

        const byte carrierLevel = 1;
        // LimitedLifespan reads nothing; the monster writer does not know it.
        string[] components = [.. variety.ComponentsOrBase.Where(c => c != "LimitedLifespan")];
        byte[] blob = new MonsterObject(x, y, 0, components, variety.AnimatedObject, StateMachine: states, TransitionState: 1, Team: 0).Blob();
        // A dead monster's Actor reads two more bytes (0, 0) after its empty path, u16 and u8. Behind them come the rest of
        // Actor (10 bytes), Transitionable (1), StateMachine, MinimapIcon (1) and the task count (1).
        var stateBytes = new BlobWriter();
        states.Write(stateBytes);
        int at = blob.Length - (10 + 1 + stateBytes.ToArray().Length + 1 + 1);
        if (at < 7 || blob.AsSpan(at - 7, 7).ContainsAnyExcept((byte)0))
        {
            Refuse("the monster's data is not laid out as this version expects");
            return;
        }
        blob = [.. blob[..at], 0, 0, .. blob[at..]];

        var player = Program.PlayerOf(conn);
        bool register;
        lock (conn.Monsters) register = conn.MonsterTypes.Add((variety.Row, carrierLevel));
        if (register) await player.Send(InstanceEntities.MonsterTypes([((ushort)variety.Row, carrierLevel)]), $"zoom: 0x0020 monster type {variety.Id}@{carrierLevel}");
        uint id = conn.TakeObjectId();
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, carrierLevel));
        await player.Send([.. InstanceEntities.CreateObject(id, type, blob),
            .. CallFunction(id, "camera_zoom", CameraSpeed, Everywhere, Everywhere, level),
            .. InstanceProtocol.RemoveObject(id)],
            $"zoom: point {point} at ({x}, {y}), level {Text(level)} (carrier 0x{id:x} created, camera_zoom called, removed)");
    }

    void Refuse(string why)
    {
        if (Interlocked.Exchange(ref _refused, 1) == 0) _mod.Log($"no zoom: {why}");
    }

    /// <summary>S→C 0x285: calls an entry of an object's Functions component with number arguments. Layout (client reader
    /// 0x141fe69f0): the object header (u32 id, u32 0, u16 0), u16 the entry's id, u8 a mask of the arguments given (arg1
    /// is bit 0), then per argument u8 count (1), u8 type (9: a float) and the value. The id is a hash of the entry's
    /// name, which the client compares with each entry's: h = h * 0x1000193 ^ byte from 0, its halves folded together.</summary>
    static byte[] CallFunction(uint objectId, string name, params float[] arguments)
    {
        uint hash = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(name)) hash = hash * 0x1000193 ^ b;
        var message = new List<byte> { 0x02, 0x85 };
        void U32(uint v) { message.Add((byte)(v >> 24)); message.Add((byte)(v >> 16)); message.Add((byte)(v >> 8)); message.Add((byte)v); }
        U32(objectId);
        U32(0);
        message.Add(0); message.Add(0);
        message.Add((byte)((hash >> 24) ^ (hash >> 8))); message.Add((byte)((hash >> 16) ^ hash));
        message.Add((byte)((1 << arguments.Length) - 1));
        foreach (float argument in arguments) { message.Add(1); message.Add(9); U32(BitConverter.SingleToUInt32Bits(argument)); }
        return [.. message];
    }

    Task Command(ClientState conn, string[] args)
    {
        double[] state = StateOf(conn);
        double now = Wanted(conn, state);
        string word = args.Length == 1 ? args[0].ToLowerInvariant() : "";
        double? level = word switch
        {
            "off" => 1,
            "out" => now + _choices.Step,
            "in" => now - _choices.Step,
            _ => double.TryParse(word.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double typed) ? typed : null,
        };
        if (args.Length == 0)
        {
            // Again, for when a boss's own script cleared every zoom point: the next tick makes a new one.
            lock (state) if (state[1] != 1) state[1] = -1;
            return conn.Commands!.Reply($"Zoom {Text(now)}. Change it: /zoom <1 to 3>, /zoom out, /zoom in, /zoom off.");
        }
        if (word == "default")
        {
            Save(conn, state, null);
            return conn.Commands!.Reply($"Zoom {Text(Wanted(conn, state))}");
        }
        if (level is not { } picked || double.IsNaN(picked))
            return conn.Commands!.Reply($"/zoom <1 to 3>, /zoom out, /zoom in, /zoom off or /zoom default. Now: zoom {Text(now)}");
        picked = Math.Round(Math.Clamp(picked, Least, Most), 2);
        Save(conn, state, picked);
        return conn.Commands!.Reply($"Zoom {Text(picked)}");
    }

    /// <summary>The player's own level, for this connection and with the account; null goes back to the setting.</summary>
    void Save(ClientState conn, double[] state, double? level)
    {
        lock (state) state[2] = level ?? -1;
        try { Program.WorldOf(conn).AccountData.Change(SavedKey, _ => level is { } l ? Text(l) : ""); }
        catch (Exception e) when (e is InvalidOperationException or IOException) { _mod.Log($"the level was not saved: {e.Message}"); }
    }
}
