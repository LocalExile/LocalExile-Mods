using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Imports;
using Microsoft.Data.Sqlite;

// Optional operator-built registry of exact item exports in downloaded snapshots.
// A changed build may reuse an item, but never the snapshot's tree, skills or socket contents.
static class NativeItemSources
{
    public const string DefaultPath = "data/pob-import/native-items.db";
    public static string Key(PathOfBuildingItem item) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Text = Normalize(item.Text), item.Variant, item.HasAlternateVariants, item.HasLegacyModRanges,
            Ranges = item.ModRanges.OrderBy(p => p.Key).ToArray() }))));

    public static string Normalize(string text) => string.Join('\n', text.Replace("Rarity: RELIC", "Rarity: UNIQUE").Replace("\r", "").Split('\n')
        .Select(l => Regex.Replace(l.Trim(), @"^Superior (?=\S)", ""))
        .Where(l => l.Length > 0 && !l.StartsWith("{vestigial}") && !Regex.IsMatch(l, @"^(Requires Class \w+|Foil Unique \(.*\)|Ward: \d+|WardBasePercentile: [\d.]+|Memory Strands: \d+)$")));
    public static string? Find(PathOfBuildingItem item)
    {
        if (!File.Exists(DefaultPath)) return null;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DefaultPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT source FROM item_sources WHERE fingerprint=$key";
        query.Parameters.AddWithValue("$key", Key(item));
        return query.ExecuteScalar() as string;
    }
}
