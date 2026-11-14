using System.Text.Json;
using GameServer.Core;

// One converter request (or a read-only corpus run) uses one immutable data revision.
// Client tables come from the same client.db as the server, never historical data/tables/*.json copies.
static class NativeData
{
    static object? revision;
    static readonly Dictionary<string, JsonDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, object> derived = new(StringComparer.Ordinal);
    static readonly Dictionary<string, DateTime> files = new(StringComparer.OrdinalIgnoreCase);

    public sealed class Lease(JsonDocument document) : IDisposable
    {
        public JsonElement RootElement => document.RootElement;
        public void Dispose() { } // The immutable document belongs to this bounded converter process.
    }

    public static void Validate()
    {
        revision ??= ClientTables.CacheKey;
        if (!Equals(revision, ClientTables.CacheKey) || files.Any(f => File.GetLastWriteTimeUtc(f.Key) != f.Value))
            throw new InvalidDataException("Game data changed during conversion; retry with one current data revision.");
    }

    static string? TableName(string path) => string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))),
        "tables", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path) : null;

    static readonly Dictionary<string, bool> tables = new(StringComparer.OrdinalIgnoreCase);
    public static bool Exists(string path)
    {
        string? name = TableName(path) ?? (Path.GetFileName(path).Equals("item-types.json", StringComparison.OrdinalIgnoreCase) ? "item_types" : null);
        if (name is null) return File.Exists(path);
        if (!tables.TryGetValue(name, out bool exists)) { Validate(); tables[name] = exists = ClientTables.Has(name); }
        return exists;
    }

    public static T Shared<T>(string key, Func<T> create) where T : class
    {
        if (!derived.TryGetValue(key, out var value)) { Validate(); derived.Add(key, value = create()); }
        return (T)value;
    }

    public static Lease Read(string path)
    {
        string key = Path.GetFullPath(path);
        if (!documents.TryGetValue(key, out var document))
        {
            Validate();
            if (TableName(path) is { } table) document = Table(table);
            else if (Path.GetFileName(path).Equals("item-types.json", StringComparison.OrdinalIgnoreCase))
            {
                using var rows = Table("item_types");
                document = JsonSerializer.SerializeToDocument(new Dictionary<string, JsonElement> { ["types"] = rows.RootElement });
            }
            else
            {
                DateTime written = File.GetLastWriteTimeUtc(key);
                document = JsonDocument.Parse(File.ReadAllBytes(key));
                if (File.GetLastWriteTimeUtc(key) != written) { document.Dispose(); throw new InvalidDataException("Game-data file changed during conversion."); }
                files.Add(key, written);
            }
            Validate();
            documents.Add(key, document);
        }
        return new(document);
    }

    static JsonDocument Table(string name)
    {
        var table = ClientTables.Table(name);
        string[] columns = table.Columns.ToArray();
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            foreach (var row in table)
            {
                writer.WriteStartObject();
                foreach (string column in columns) { writer.WritePropertyName(column); JsonSerializer.Serialize(writer, row.Get(column)); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(bytes.ToArray());
    }
}
