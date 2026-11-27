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
    readonly ConditionalWeakTable<Player, object> _views = new();
    readonly Lock _cacheGate = new();
    int _disposed;
    static readonly HashSet<byte> Slots = [1, 4, 5, 6, 7, 8, 9, 10, 102];
    static readonly HashSet<string> Classes = ["Body Armour", "Helmet", "Amulet", "Ring", "Gloves", "Boots", "Belt"];

    public void Load(IModContext mod) => GameEvents.ItemStatSources.Add(mod.Manifest.Id, Sources);

    public IReadOnlyList<(byte Row, InventoryItem Item)> Sources(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)> native)
    {
        if (Volatile.Read(ref _disposed) != 0) return Array.Empty<(byte, InventoryItem)>();
        if (!_views.TryGetValue(player, out var view))
            lock (_cacheGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return Array.Empty<(byte, InventoryItem)>();
                if (!_views.TryGetValue(player, out view)) _views.Add(player, view = new Cached());
            }
        var cache = (Cached)view;
        lock (cache.Gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _views.Remove(player);
                return Array.Empty<(byte, InventoryItem)>();
            }
            bool same = cache.Hosts.Length == native.Count;
            for (int i = 0; same && i < native.Count; i++)
                same = cache.Hosts[i].Row == native[i].Row && ReferenceEquals(cache.Hosts[i].Item, native[i].Item);
            if (same) return cache.Sources;
            cache.Sources = BuildSources(player, native);
            cache.Hosts = [.. native];
            return cache.Sources;
        }
    }

    IReadOnlyList<(byte Row, InventoryItem Item)> BuildSources(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)> native)
    {
        var catalog = ItemsFor(player);
        var result = new List<(byte Row, InventoryItem Item)>();
        foreach (var (row, target) in native)
        {
            if (!Slots.Contains(row) || target.Item.SkinTransferSource is not { } donor
                || donor.SkinTransferSource is not null || target.Id >= 0x80000000u
                || catalog.ByHash(target.Item.TypeHash) is not { } a || catalog.ByHash(donor.TypeHash) is not { } b
                || !Classes.Contains(a.Class) || a.Class != b.Class) continue;
            if (!_sources.TryGetValue(target, out var projected))
                lock (_cacheGate)
                    if (!_sources.TryGetValue(target, out projected))
                    {
                        projected = new(0x80000000u | target.Id, 0, 0, Project(donor, catalog.Mods));
                        _sources.Add(target, projected);
                    }
            result.Add((row, projected));
        }
        return result;
    }

    /// <summary>Only the calculation view changes. The native donor, including its sockets and all modifiers, is kept intact.</summary>
    public static Item Project(Item donor, ModCatalog mods)
    {
        var projected = new ItemMod[donor.Mods.Count];
        for (int i = 0; i < projected.Length; i++)
        {
            var mod = donor.Mods[i];
            if (mods.ByHash(mod.Hash) is not { } row) { projected[i] = mod; continue; }
            var values = new int[mod.Values.Count];
            for (int index = 0; index < values.Length; index++)
                values[index] = index < row.Stats.Count && SkillExecution(row.Stats[index].Name) ? 0 : mod.Values[index];
            projected[i] = mod with { Values = values };
        }
        return donor with { Sockets = Array.Empty<ItemSocket>(), Links = null, Mods = projected };
    }

    // Skill grants are never enumerated from ItemStatSources. These numeric execution controls also stay out of global stats.
    static bool SkillExecution(string id) => id.Contains("trigger", StringComparison.OrdinalIgnoreCase)
        || id.Contains("cast_on_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("cast_when_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("granted_skill", StringComparison.OrdinalIgnoreCase)
        || id.Contains("grants_skill", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_cacheGate) { _views.Clear(); _sources.Clear(); }
    }
}
