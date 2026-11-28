using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using GameServer.Storage;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// A snapshot of the saves taken before a command runs, so a command that goes wrong can be undone.
///
/// The copy goes through SQLite's own backup API. characters.db runs in WAL mode, so a plain file copy taken while
/// the servers are up is a database that looks healthy and is quietly missing everything still in the -wal file;
/// the backup API walks the live database instead, and the result is whole. Both databases are one file each, so
/// restoring is copying a snapshot's folder back over data/ with the servers stopped.
///
/// One folder per snapshot under data/gm-backups/, holding characters.db, auth.db and a manifest.json that says
/// when it was taken and what it holds. The newest <c>Keep</c> are kept.
/// </summary>
static class Backup
{
    /// <summary>Where the snapshots go: data/gm-backups beside the database itself.</summary>
    internal static string Folder()
    {
        string data = Path.GetDirectoryName(Path.GetFullPath(CharactersPath())) ?? Path.Combine(Environment.CurrentDirectory, "data");
        return Path.Combine(data, "gm-backups");
    }

    static string CharactersPath() => Absolute(Saves.Db.Path);
    static string AuthPath() => Absolute(Saves.Db.AuthPath);

    static string Absolute(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));

    internal sealed record Result(bool Ok, string Stamp, long Bytes, double Seconds, string Error);

    /// <summary>Takes one snapshot and prunes the old ones. Never throws: the caller reports a failed snapshot and
    /// runs the command anyway, so a broken backup cannot lock the panel.</summary>
    internal static Result Take(int keep)
    {
        var clock = Stopwatch.StartNew();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dest = Path.Combine(Folder(), stamp);
        try
        {
            Directory.CreateDirectory(dest);
            long bytes = 0;
            foreach (string source in new[] { CharactersPath(), AuthPath() })
            {
                if (!File.Exists(source)) continue;
                string target = Path.Combine(dest, Path.GetFileName(source));
                using var from = new SqliteConnection($"Data Source={source};Pooling=False");
                using var to = new SqliteConnection($"Data Source={target};Pooling=False");
                from.Open();
                to.Open();
                from.BackupDatabase(to);
                to.Close();
                bytes += new FileInfo(target).Length;
            }
            var files = Directory.GetFiles(dest).Select(f => Path.GetFileName(f)).Order().ToArray();
            File.WriteAllText(Path.Combine(dest, "manifest.json"),
                JsonSerializer.Serialize(new { stamp, taken = DateTime.Now.ToString("o"), files }, Pretty),
                new UTF8Encoding(false));
            clock.Stop();
            Prune(keep);
            return new Result(true, stamp, bytes, clock.Elapsed.TotalSeconds, "");
        }
        catch (Exception e)
        {
            clock.Stop();
            return new Result(false, stamp, 0, clock.Elapsed.TotalSeconds, e.Message.Replace("\r", " ").Replace("\n", " "));
        }
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>The newest <paramref name="keep"/> snapshot folders stay; the rest go.</summary>
    static void Prune(int keep)
    {
        try
        {
            var folders = new DirectoryInfo(Folder()).GetDirectories()
                .Where(d => d.Name.Length == 15 && d.Name[8] == '-')
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .ToList();
            foreach (var old in folders.Skip(Math.Max(1, keep))) old.Delete(true);
        }
        catch (Exception)
        {
            // A snapshot that cannot be pruned is still a good snapshot.
        }
    }
}
