using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

static class NativeGemCache
{
    static readonly Dictionary<ItemCatalog, (ItemCatalog.BaseType Type, ItemCatalog.GemVariant Variant)[]> Variants = [];
    public static (ItemCatalog.BaseType Type, ItemCatalog.GemVariant Variant)[] Obtainable(ItemCatalog catalog)
    {
        if (Variants.TryGetValue(catalog, out var rows)) return rows;
        return Variants[catalog] = catalog.All.Where(t => t.Supported && !t.Id.Contains("Royale", StringComparison.OrdinalIgnoreCase) && catalog.Gem(t) is not null)
            .SelectMany(t => catalog.Gem(t)!.Variants.Where(v => v.Creatable).Select(v => (t, v))).ToArray();
    }
    static readonly Dictionary<(ItemCatalog, string, PathOfBuildingGem), Item> Cache = new();
    public static Item Create(PathOfBuildingGem gem, ItemCatalog catalog, string folder, Func<Item> create)
    {
        var key = (catalog, folder, gem);
        if (Cache.TryGetValue(key, out var item)) return item;
        return Cache[key] = create();
    }
}
