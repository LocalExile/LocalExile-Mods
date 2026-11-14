using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using Microsoft.Data.Sqlite;

// Native unique catalogue resolution only; the Gateway owns character changes.
static class ItemSend
{
    internal sealed record UniqueDefinition(string Name, string BaseTypeId, string[] Mods, byte ItemLevel = 86, bool Corrupted = false);

    // The server's catalogue row: PoB's text cannot name most unique modifiers, this row does. Older databases have no such table.
    internal static UniqueDefinition? Definition(SqliteConnection connection, string title, ItemCatalog.BaseType type, Item item)
    {
        if (item.Rarity != ItemRarity.Unique || item.FracturedCount > 0) return null;
        using var c = connection.CreateCommand();
        c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='catalog_entries'";
        if (c.ExecuteScalar() is null) return null;
        c.CommandText = "SELECT payload FROM catalog_entries WHERE kind='unique_item' AND key=$key AND enabled=1";
        // A Foulborn unique is its ordinary unique with mutated modifiers, which stay as parsed.
        const string foulborn = "Foulborn ";
        c.Parameters.AddWithValue("$key", $"{(title.StartsWith(foulborn, StringComparison.Ordinal) ? title[foulborn.Length..] : title)}|{type.Id}");
        return c.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<UniqueDefinition>(payload) : null;
    }

    /// <summary>Catalogue completion retains exported rolls; only missing fixed-value native stats can be inferred.</summary>
    internal static Item Completed(Item item, ItemCatalog.BaseType type, UniqueDefinition unique, ModCatalog mods, List<string> warnings)
    {
        HashSet<string> Stats(ModCatalog.Mod mod) => [.. mod.Stats.Select(s => s.Id)];
        var parsed = item.Mods.ToList();
        ItemMod Resolve(ModCatalog.Mod mod, ModSection section)
        {
            var match = parsed.FirstOrDefault(m => mods.ByHash(m.Hash) is { } native && Stats(native).SetEquals(Stats(mod)));
            if (match is null)
            {
                if (mod.Stats.Any(s => s.Min != s.Max))
                    throw new ArgumentException($"{type.Name}: the export omits the original roll of '{mod.Id}'.");
                return new(section, mod.Hash, [.. mod.Stats.Select(s => s.Max)]);
            }
            parsed.Remove(match);
            var values = mods.ByHash(match.Hash)!.Stats.Select((s, i) => (s.Id, Value: match.Values[i])).ToDictionary(s => s.Id, s => s.Value);
            return new(section, mod.Hash, [.. mod.Stats.Select(s => values[s.Id])]);
        }
        var implicits = (type.Implicits ?? []).Select(m => Resolve(m, ModSection.Implicit)).ToList();
        var explicits = unique.Mods.Select(id => mods.ById(id)
            ?? throw new ArgumentException($"Native unique modifier '{id}' is unavailable.")).Select(m => Resolve(m, ModSection.Explicit)).ToList();
        int? requiredLevel = item.RequiredLevel;
        var completed = item with
        {
            Mods = [.. implicits, .. explicits, .. parsed], Corrupted = item.Corrupted || unique.Corrupted,
            ItemLevel = item.ItemLevel > 1 ? item.ItemLevel : unique.ItemLevel,
        };
        return completed with { RequiredLevel = requiredLevel };
    }

    internal static string Title(string text) => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .SkipWhile(l => !l.StartsWith("Rarity:", StringComparison.Ordinal)).Skip(1).FirstOrDefault() ?? "";
}
