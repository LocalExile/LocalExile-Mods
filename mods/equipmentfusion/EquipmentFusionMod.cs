using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;

namespace LocalExile.Mods.EquipmentFusion;

/// <summary>Native skin transfers retain the items; this optional provider contributes only their non-weapon stats.</summary>
public sealed class EquipmentFusionMod : IMod, IDisposable
{
    ConditionalWeakTable<InventoryItem, InventoryItem> _sources = new();
    sealed class Cached
    {
        public readonly Lock Gate = new();
        public (byte Row, InventoryItem Item)[] Hosts = [];
        public IReadOnlyList<(byte Row, InventoryItem Item)> Sources = [];
    }
    readonly ConditionalWeakTable<ClientState, Cached> _views = new();
    static readonly HashSet<byte> Slots = [1, 4, 5, 6, 7, 8, 9, 10, 102];
    static readonly HashSet<string> Classes = ["Body Armour", "Helmet", "Amulet", "Ring", "Gloves", "Boots", "Belt"];

    public void Load(IModContext mod) => GameEvents.ItemStatSources.Add(mod.Manifest.Id, Sources);

    public IReadOnlyList<(byte Row, InventoryItem Item)> Sources(ClientState conn,
        IReadOnlyList<(byte Row, InventoryItem Item)> native)
    {
        var cache = _views.GetOrCreateValue(conn);
        lock (cache.Gate)
        {
            if (cache.Hosts.Length == native.Count && cache.Hosts.Select((p, i) => p.Row == native[i].Row
                && ReferenceEquals(p.Item, native[i].Item)).All(same => same)) return cache.Sources;
            cache.Sources = BuildSources(conn, native);
            cache.Hosts = [.. native];
            return cache.Sources;
        }
    }

    IReadOnlyList<(byte Row, InventoryItem Item)> BuildSources(ClientState conn,
        IReadOnlyList<(byte Row, InventoryItem Item)> native)
    {
        var catalog = ItemsFor(conn);
        var result = new List<(byte Row, InventoryItem Item)>();
        foreach (var (row, target) in native)
        {
            if (!Slots.Contains(row) || target.Item.SkinTransferSource is not { } donor
                || donor.SkinTransferSource is not null || target.Id >= 0x80000000u
                || catalog.ByHash(target.Item.TypeHash) is not { } a || catalog.ByHash(donor.TypeHash) is not { } b
                || !Classes.Contains(a.Class) || a.Class != b.Class) continue;
            var projected = _sources.GetValue(target, host => new(0x80000000u | host.Id, 0, 0,
                Project(host.Item.SkinTransferSource!, catalog.Mods)));
            result.Add((row, projected));
        }
        return result;
    }

    /// <summary>Only the calculation view changes. The native donor, including its sockets and all modifiers, is kept intact.</summary>
    public static Item Project(Item donor, ModCatalog mods) => donor with
    {
        Sockets = [], Links = null,
        Mods = [.. donor.Mods.Select(mod => mods.ByHash(mod.Hash) is not { } row ? mod : mod with
        {
            Values = [.. mod.Values.Select((value, index) => index < row.Stats.Count && SkillExecution(row.Stats[index].Id) ? 0 : value)],
        })],
    };

    // Skill grants are never enumerated from ItemStatSources. These numeric execution controls also stay out of global stats.
    static bool SkillExecution(string id) => id.Contains("trigger", StringComparison.OrdinalIgnoreCase)
        || id.Contains("cast_on_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("cast_when_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("granted_skill", StringComparison.OrdinalIgnoreCase)
        || id.Contains("grants_skill", StringComparison.OrdinalIgnoreCase);

    public void Dispose() { _views.Clear(); _sources.Clear(); }
}
