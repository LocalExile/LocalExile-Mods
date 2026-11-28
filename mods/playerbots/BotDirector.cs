using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Storage;
using GameServer.Modules.Social;

namespace GameServer.Modules.PlayerBots;

/// <summary>The module's settings (settings.json, beside module.json, with the user's values).</summary>
sealed record BotSettings(int MaxBots = 100, int SpawnIntervalSeconds = 60, int FirstSpawnSeconds = 60,
    string League = "Standard", int[]? QuestMinutes = null, int[]? TownActivities = null, int[]? IdleSeconds = null,
    double DeathChancePerQuest = 0.15, int StatusSeconds = 10,
    bool McpEnabled = false, int McpPort = 6194, int McpLeaseSeconds = 30,
    string TownBotsMode = "auto", int[]? TownBots = null, string PersistenceMode = "cycling");

/// <summary>One quest of the bots' guides (story-main.md, story-side.md).</summary>
sealed record BotQuest(string Act, string Town, string Id, string Name, string Where, bool Side)
{
    public static List<BotQuest> Route(string folder)
    {
        var main = Read(Path.Combine(folder, "story-main.md"), side: false);
        var side = Read(Path.Combine(folder, "story-side.md"), side: true);
        // Act by act: its main quests, then its side quests.
        return [.. main.Concat(side).GroupBy(q => q.Act).OrderBy(g => main.FindIndex(q => q.Act == g.Key) is var i and >= 0 ? i : int.MaxValue)
            .SelectMany(g => g.OrderBy(q => q.Side))];
    }

    static List<BotQuest> Read(string path, bool side) => !File.Exists(path) ? [] :
        [.. File.ReadAllLines(path).Where(l => l.StartsWith("| Act") || l.StartsWith("| Epilogue"))
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .Where(c => c.Length >= 5 && c[2] != "Quest")
            .Select(c => new BotQuest(c[0], c[1], c[2], c[3], c[4], side))];
}

/// <summary>What a bot is doing now.</summary>
enum BotPhase { Arriving, Walking, Idling, Leaving, Questing, Following, Departing }

/// <summary>A bot: its account and character in the save database, its place in the route, and its live server-driven body.</summary>
sealed class Bot
{
    public required Guid AccountId, CharacterId;
    public required string Account, Name;
    public required CharacterClass Class;
    public required uint PeerId;
    public int Level = 1, QuestIndex, Deaths;
    public BotPhase Phase = BotPhase.Arriving;
    public string Doing = "создаётся";
    public string Town = "1_1_town";
    public double X, Y, TargetX, TargetY;
    public DateTime Until;
    public int ActivitiesLeft;
    public string? TargetKind;
    /// <summary>A quick visit (in, hand the quest in to an NPC, out) or a stay (by the stash, a few things to do).</summary>
    public bool Quick;
    /// <summary>What it wears: base item Ids (BotGear), and the level it was picked at.</summary>
    public List<string> Gear = [];
    public int GearLevel;
    /// <summary>The town its X and Y are a place in (null: not placed in its town yet, as after a visit nobody saw).</summary>
    public string? PlacedIn;
    /// <summary>The player whose party it is in (BotParty), and that party's id.</summary>
    public Player? Leader;
    /// <summary>The leader's character: an area change is a new connection, which the bot follows by this name.</summary>
    public string? LeaderName;
    /// <summary>When the leader's connection went without a new one yet (an area change in progress).</summary>
    public DateTime? LeaderGoneSince;
    public uint Party;
    /// <summary>Live server-driven Player in a human-observed town or in its leader's instance.</summary>
    public Player? Body;
    public (int X, int Y)? WalkOrderedTo;
    public DateTime WalkStartedAt;
    public Guid ControlEpoch = Guid.NewGuid();
    public BotControlLease? Control;
}

/// <summary>Creates bots up to the limit, runs their campaign/endgame life cycle, materializes visible server-driven bodies
/// in towns where real players are, and promotes invited bots into full party followers.</summary>
sealed partial class BotDirector
{
    readonly BotSettings _settings;
    readonly List<BotQuest> _route;
    internal readonly BotLog _log;
    readonly Action<string> _console;
    readonly SqliteAccountStore _accounts = new(Saves.Db);
    internal readonly Random _random = new();
    internal readonly List<Bot> _bots = [];
    readonly CancellationTokenSource _stop = new();
    int _stopped;
    Task? _running;
    BotMcpServer? _mcp;
    static int _nextPeer = 0x38000000;
    const int StandApart = 20;
    const string EndgameTown = "2_11_endgame_town";

    readonly string _statePath;
    readonly Dictionary<string, int> _townTargets = new(StringComparer.Ordinal);
    readonly int _townCount;

    bool IsPersistent => string.Equals(_settings.PersistenceMode, "persistent", StringComparison.OrdinalIgnoreCase);
    string TownMode => _settings.TownBotsMode?.ToLowerInvariant() switch { "custom" => "custom", "organic" => "organic", _ => "auto" };

    public BotDirector(BotSettings settings, List<BotQuest> route, BotLog log, Action<string> console, string statePath)
    {
        (_settings, _route, _log, _console, _statePath) = (settings, route, log, console, statePath);
        _townCount = Math.Max(1, _route.Select(q => q.Town).Distinct(StringComparer.Ordinal).Count());
    }

    internal static (int Min, int Max) TownBounds(BotSettings settings, int townCount)
    {
        int limit = Math.Max(0, settings.MaxBots);
        if (limit == 0) return (0, 0);
        string mode = settings.TownBotsMode?.ToLowerInvariant() ?? "auto";
        if (mode == "organic") return (0, Math.Max(10, limit));
        if (mode == "custom")
        {
            int lo = settings.TownBots is [var a, ..] ? Math.Clamp(a, 0, limit) : Math.Min(2, limit);
            int hi = settings.TownBots is [_, var b, ..] ? Math.Clamp(b, lo, limit) : Math.Clamp(Math.Max(lo, 5), lo, limit);
            return (lo, hi);
        }
        int towns = Math.Max(1, townCount);
        int denom = towns * 100;
        int autoMax = Math.Clamp((limit * 55 + denom - 1) / denom, 1, Math.Min(25, limit));
        int autoMin = limit >= towns ? Math.Clamp((limit * 22) / denom, 1, autoMax) : 0;
        return (autoMin, autoMax);
    }

    int RollTownTarget(string town)
    {
        var (lo, hi) = TownBounds(_settings, _townCount);
        int target = hi <= lo ? lo : _random.Next(lo, hi + 1);
        _townTargets[town] = target;
        return target;
    }

    int TownCap(string town) => TownBounds(_settings, _townCount).Max;

    public void Start()
    {
        foreach (string town in _route.Select(q => q.Town).Distinct(StringComparer.Ordinal))
            RollTownTarget(town);
        Resume();
        foreach (var bot in _bots.Where(b => b.Leader is not null))
        {
            if (PartyRoster.TryAddCompanion(bot.Leader!, Companion(bot, bot.Leader!.Layout.Hash), out uint party, restore: bot.Party)) bot.Party = party;
            else Depart(bot, "группа недоступна");
        }
        foreach (var party in _bots.Where(b => b.Leader is not null).GroupBy(b => b.Party))
            _ = SendPartyWhenReady(party.First().Leader!, party.Key);
        _running = Task.Run(Run);
        if (_settings.McpEnabled)
        {
            try { _mcp = new(this, _settings.McpPort, _console); }
            catch (Exception error) when (error is System.Net.HttpListenerException or IOException or UnauthorizedAccessException)
            { _console($"bots MCP not started: {error.Message}"); }
        }
    }

    // The bots a restart left: their progress from bots.json; in cycling mode a marked bot account the file does not know
    // is deleted, while in persistent mode SQLite bot accounts are recovered automatically.
    sealed record SavedBot(Guid AccountId, Guid CharacterId, string Account, string Name, CharacterClass Class, int Level, int QuestIndex, int Deaths,
        BotPhase Phase = BotPhase.Questing, string? Town = null, double X = 0, double Y = 0, double TargetX = 0, double TargetY = 0,
        string? TargetKind = null, string? Doing = null, double SecondsLeft = 0, int ActivitiesLeft = 0,
        bool Quick = false, List<string>? Gear = null, int GearLevel = 0, string? LeaderName = null, uint Party = 0);

    // Keep over-limit characters in the same save, ready if the operator raises the cap later.
    readonly List<SavedBot> _held = [];

    void Resume()
    {
        var saved = File.Exists(_statePath) ? JsonSerializer.Deserialize<List<SavedBot>>(File.ReadAllText(_statePath)) ?? [] : [];
        foreach (var s in saved)
        {
            if (_accounts.GetCharacter(s.CharacterId) is null) continue;
            if (_bots.Count >= _settings.MaxBots)
            {
                _held.Add(s);
                continue;
            }
            var bot = new Bot
            {
                AccountId = s.AccountId, CharacterId = s.CharacterId, Account = s.Account, Name = s.Name, Class = s.Class,
                PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeer)), Level = s.Level, QuestIndex = s.QuestIndex, Deaths = s.Deaths,
                Phase = s.Phase, Town = s.Town ?? "1_1_town", X = s.X, Y = s.Y, TargetX = s.TargetX, TargetY = s.TargetY,
                TargetKind = s.TargetKind, Doing = s.Doing ?? "продолжает", ActivitiesLeft = s.ActivitiesLeft,
                Until = DateTime.UtcNow.AddSeconds(Math.Max(0, s.SecondsLeft)),
                Quick = s.Quick, Gear = s.Gear ?? [], GearLevel = s.GearLevel,
            };
            // A module reload retains a live party. Its members can have earned levels beyond
            // their last town's ambient range, which must not reset their build or experience.
            if (bot.Phase is BotPhase.Following or BotPhase.Departing)
            {
                var leader = bot.Phase == BotPhase.Following ? Program.InGame.Keys.FirstOrDefault(c => c.HasClient
                    && !c.Closed && !c.LeavingArea && Program.CharacterName(c) == s.LeaderName) : null;
                if (leader is not null && s.Party != 0)
                    (bot.Leader, bot.LeaderName, bot.Party) = (leader, s.LeaderName, s.Party);
                else (bot.Phase, bot.Until, bot.Doing) = (BotPhase.Questing, DateTime.UtcNow, "идёт дальше по квестам");
            }
            if (bot.Leader is null && !IsPersistent)
            {
                bot.Level = LevelForTown(bot.Level, bot.Town, _random);
            }
            if (NeedsBuildOnResume(bot, CharacterSaves.ModuleData(bot.Name, "PlayerBots.BuildVersion"),
                CharacterSaves.Load(bot.Name)?.Experience))
            { Dress(bot); SaveProgress(bot); }
            _bots.Add(bot);
            // A bot an earlier version left waiting for room in a town goes on with its quests.
            if (bot.Doing.StartsWith("ждёт", StringComparison.Ordinal))
                (bot.Phase, bot.Until, bot.Doing) = (BotPhase.Questing, DateTime.UtcNow, "идёт дальше по квестам");
            _log.Write(bot.Account, bot.Name, $"сервер перезапущен: продолжает с того же места — {bot.Doing}");
        }
        var known = _bots.Select(b => b.AccountId).Concat(_held.Select(b => b.AccountId)).ToHashSet();
        foreach (var account in _accounts.Accounts().Where(a => IsBot(a) && !known.Contains(a.Id)))
        {
            if (IsPersistent)
            {
                foreach (var c in _accounts.CharactersOf(account.Id))
                {
                    if (!string.Equals(c.League, _settings.League, StringComparison.OrdinalIgnoreCase)) continue;
                    string town = string.IsNullOrWhiteSpace(c.LastTown) ? "1_1_town" : c.LastTown;
                    int questIndex = town == EndgameTown && c.Level >= 68
                        ? _route.Count
                        : Math.Max(0, _route.FindIndex(q => q.Town == town));
                    if (_bots.Count >= _settings.MaxBots)
                    {
                        _held.Add(new SavedBot(account.Id, c.Id, account.Name, c.Name, c.Class, c.Level, questIndex, 0,
                            BotPhase.Questing, town));
                        continue;
                    }
                    var recovered = new Bot
                    {
                        AccountId = account.Id, CharacterId = c.Id, Account = account.Name, Name = c.Name, Class = c.Class,
                        PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeer)),
                        Level = Math.Clamp(c.Level, 1, 100), Town = town, QuestIndex = questIndex,
                        Phase = BotPhase.Questing, Until = DateTime.UtcNow, Doing = "восстановлен из сохранения",
                    };
                    if (NeedsBuildOnResume(recovered, CharacterSaves.ModuleData(recovered.Name, "PlayerBots.BuildVersion"),
                        CharacterSaves.Load(recovered.Name)?.Experience))
                    { Dress(recovered); SaveProgress(recovered); }
                    _bots.Add(recovered);
                    _log.Write(recovered.Account, recovered.Name, $"восстановлен из БД (ур. {recovered.Level}, {recovered.Town})");
                }
            }
            else
            {
                foreach (var c in _accounts.CharactersOf(account.Id)) _accounts.DeleteCharacter(c.Id);
                DeleteAccount(account.Id);
                _log.Write(account.Name, "-", "аккаунт бота без состояния удалён");
            }
        }
        // A restart may bring back more bots in a town than its configured cap takes: the extra ones go on with their quests.
        foreach (var town in _bots.Where(b => b.Leader is null && b.Phase != BotPhase.Questing).GroupBy(b => b.Town).ToList())
        {
            int cap = TownCap(town.Key);
            foreach (var extra in town.Skip(cap).ToList())
            {
                _log.Write(extra.Account, extra.Name, $"после перезапуска в городе {extra.Town} нет места: идёт дальше по квестам");
                BeginQuest(extra, DateTime.UtcNow);
            }
        }
        if (_bots.Count > 0) _console($"bots: resumed {_bots.Count}");
    }

    // The mark a bot account carries in place of a password hash. A real hash is base64 (AccountService), which never
    // holds ':', so no player's account, whatever its name ("some_bot" too), can be taken for a bot's and deleted.
    const string BotMark = "PlayerBots:";
    static bool IsBot(Account account) => account.PasswordHash.StartsWith(BotMark, StringComparison.Ordinal);

    void SaveState()
    {
        List<SavedBot> bots;
        var now = DateTime.UtcNow;
        lock (_bots) bots = [.. _bots.Select(b => new SavedBot(b.AccountId, b.CharacterId, b.Account, b.Name, b.Class, b.Level, b.QuestIndex, b.Deaths,
            b.Phase, b.Town, b.X, b.Y, b.TargetX, b.TargetY, b.TargetKind, b.Doing, (b.Until - now).TotalSeconds, b.ActivitiesLeft,
            b.Quick, b.Gear, b.GearLevel, b.LeaderName, b.Party)), .. _held];
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath + ".tmp", JsonSerializer.Serialize(bots, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_statePath + ".tmp", _statePath, overwrite: true);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _mcp?.Dispose();
        _stop.Cancel();
        _running?.GetAwaiter().GetResult();
        lock (_bots) foreach (var bot in _bots) { bot.Control = null; StopBody(bot); }
        try { SaveState(); } catch (IOException) { }
        uint[] parties;
        lock (_bots)
        {
            parties = [.. _bots.Where(b => b.Party != 0).Select(b => b.Party).Distinct()];
            foreach (var bot in _bots.Where(b => b.Party != 0)) PartyRoster.RemoveCompanion(bot.Party, bot.Account);
        }
        foreach (uint party in parties) _ = PartyRoster.Publish(party);
    }

    async Task Run()
    {
        var nextSpawn = DateTime.UtcNow.AddSeconds(_settings.FirstSpawnSeconds);
        var nextStatus = DateTime.UtcNow;
        _console($"bots: up to {_settings.MaxBots} ({TownMode} towns, {_settings.PersistenceMode}), one every {_settings.SpawnIntervalSeconds} s (first in {_settings.FirstSpawnSeconds} s), route of {_route.Count} quest(s)");
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now >= nextSpawn)
                {
                    nextSpawn = now.AddSeconds(_settings.SpawnIntervalSeconds);
                    int count;
                    lock (_bots) count = _bots.Count;
                    if (count < _settings.MaxBots) Spawn();
                }
                var towns = LiveTowns();
                EnsureTownPopulation(towns);
                lock (_bots)
                    foreach (var bot in _bots.ToList())
                    {
                        try { Step(bot, now, towns); }
                        catch (Exception error) { _log.Error(bot.Account, bot.Name, error); bot.Until = now.AddSeconds(5); }
                    }
                Bot[] active;
                lock (_bots) active = _bots.Where(b => b.Body is not null
                    || b.Phase is BotPhase.Following or BotPhase.Departing
                    || (b.Phase is BotPhase.Walking or BotPhase.Idling or BotPhase.Leaving && towns.ContainsKey(b.Town))).ToArray();
                foreach (var bot in active)
                    try
                    {
                        if (bot.Phase == BotPhase.Following) await StepCombat(bot);
                        else if (bot.Phase == BotPhase.Departing) await StepDepartingBody(bot);
                        else if (bot.Phase is BotPhase.Walking or BotPhase.Idling or BotPhase.Leaving
                            && towns.TryGetValue(bot.Town, out var observers))
                            await StepTownBody(bot, observers);
                        else StopBody(bot);
                    }
                    catch (Exception error) { StopBody(bot); _log.Error(bot.Account, bot.Name, error); }
                if (now >= nextStatus)
                {
                    nextStatus = now.AddSeconds(_settings.StatusSeconds);
                    lock (_bots) _log.Status(_bots.Select(b =>
                        $"{b.Account,-30} {b.Name,-24} ур. {b.Level,2}  {Act(b),-32} {b.Doing}"));
                    SaveState();
                }
            }
            catch (Exception error) { _console($"bots: {error.Message}"); }
            try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    string Act(Bot b) => b.QuestIndex < _route.Count ? _route[b.QuestIndex].Act : (IsPersistent ? "Эндгейм (Karui Shores)" : "сюжет пройден");

    // ---- accounts and characters ----

    void Spawn(string? town = null)
    {
        string name = BotNames.Pick(_random, n => _accounts.FindCharacter(n) is not null || _accounts.FindAccount(n + "_bot") is not null);
        var cls = (CharacterClass)_random.Next(Enum.GetValues<CharacterClass>().Length);
        var now = DateTimeOffset.UtcNow;
        // No password anyone knows: a bot never logs in through Auth. Its hash is the bot mark (IsBot), not a real hash.
        var account = new Account(Guid.NewGuid(), name + "_bot", BotMark + Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(Guid.NewGuid().ToByteArray()), now);
        _accounts.AddAccount(account);
        town ??= "1_1_town";
        var range = LevelsForTown(town);
        int level = _random.Next(range.Min, range.Max + 1);
        var character = new GameServer.Core.Accounts.Character(Guid.NewGuid(), account.Id, name, cls, level, _settings.League, town, now, now, town);
        _accounts.AddCharacter(character);
        var bot = new Bot
        {
            AccountId = account.Id, CharacterId = character.Id, Account = account.Name, Name = name, Class = cls,
            PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeer)),
            Level = level, Town = town, QuestIndex = Math.Max(0, _route.FindIndex(q => q.Town == town)),
        };
        Dress(bot);
        lock (_bots) _bots.Add(bot);
        _log.Write(bot.Account, bot.Name, $"создан: {cls}, лига {_settings.League}; экипировка: {BotGear.Describe(bot.Gear)}; начинает сюжет на Побережье");
        _console($"bots: created {bot.Name} ({cls}), {_bots.Count}/{_settings.MaxBots}");
        bot.Phase = BotPhase.Arriving;
        bot.ActivitiesLeft = 3;
    }

    void Retire(Bot bot)
    {
        StopBody(bot);
        _bots.Remove(bot);
        try
        {
            _accounts.DeleteCharacter(bot.CharacterId);
            DeleteAccount(bot.AccountId);
            _log.Write(bot.Account, bot.Name, $"прошёл весь сюжет и все побочные квесты (ур. {bot.Level}, смертей {bot.Deaths}): персонаж и аккаунт удалены");
        }
        catch (Exception error) { _log.Error(bot.Account, bot.Name, error); }
    }

    static void DeleteAccount(Guid id)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Saves.Db.AuthPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    void SaveProgress(Bot bot)
    {
        if (_accounts.GetCharacter(bot.CharacterId) is { } c)
            _accounts.UpdateCharacter(c with { Level = bot.Level, LastTown = bot.Town, LastPlayedUtc = DateTimeOffset.UtcNow });
    }

    // ---- the route ----

    internal void BeginQuest(Bot bot, DateTime now)
    {
        StopBody(bot);
        int[] minutes = _settings.QuestMinutes is [var lo, var hi] ? [lo, hi] : [2, 6];
        if (bot.QuestIndex >= _route.Count)
        {
            if (!IsPersistent) { Retire(bot); return; }
            bot.Town = EndgameTown;
            if (bot.Level < 68) { bot.Level = 68; Dress(bot); SaveProgress(bot); }
            bot.Phase = BotPhase.Questing;
            bot.Until = now.AddSeconds(_random.Next(minutes[0] * 60, minutes[1] * 60 + 1));
            int tier = Math.Clamp(1 + (bot.Level - 68) / 2, 1, 16);
            bot.Doing = $"эндгейм: карта T{tier} (ур. {bot.Level})";
            _log.Write(bot.Account, bot.Name, $"открывает карту T{tier} (ур. {bot.Level})");
            return;
        }
        var quest = _route[bot.QuestIndex];
        bot.Town = quest.Town;
        int nextLevel = IsPersistent
            ? Math.Max(bot.Level, LevelsForTown(bot.Town).Min)
            : LevelForTown(bot.Level, bot.Town, _random);
        if (nextLevel != bot.Level) { bot.Level = nextLevel; Dress(bot); SaveProgress(bot); }
        bot.Phase = BotPhase.Questing;
        bot.Until = now.AddSeconds(_random.Next(minutes[0] * 60, minutes[1] * 60 + 1));
        bot.Doing = $"квест «{quest.Name}» ({(quest.Side ? "побочный" : "сюжет")}): {quest.Where}";
        _log.Write(bot.Account, bot.Name, $"взял квест {quest.Id} «{quest.Name}», открывает портал: {quest.Where}");
    }

    void FinishQuest(Bot bot, DateTime now)
    {
        if (bot.QuestIndex >= _route.Count)
        {
            if (!IsPersistent) { Retire(bot); return; }
            int tier = Math.Clamp(1 + (bot.Level - 68) / 2, 1, 16);
            if (_random.NextDouble() < _settings.DeathChancePerQuest)
            {
                bot.Deaths++;
                _log.Write(bot.Account, bot.Name, $"погиб на карте T{tier}; возвращается в Karui Shores");
            }
            else
            {
                if (bot.Level < 100)
                {
                    double levelChance = bot.Level < 85 ? 1.0 : bot.Level < 93 ? 0.5 : bot.Level < 98 ? 0.25 : 0.10;
                    if (_random.NextDouble() < levelChance) bot.Level++;
                }
                _log.Write(bot.Account, bot.Name, $"закрыл карту T{tier}, уровень {bot.Level}");
                Loot(bot, $"карта T{tier}");
                SaveProgress(bot);
            }
        }
        else
        {
            var quest = _route[bot.QuestIndex];
            if (_random.NextDouble() < _settings.DeathChancePerQuest)
            {
                bot.Deaths++;
                _log.Write(bot.Account, bot.Name, $"погиб на квесте «{quest.Name}» ({quest.Where}); воскресает в городе и пробует снова");
            }
            else
            {
                int gained = quest.Side ? 1 : _random.Next(1, 3);
                int maxLevel = IsPersistent ? Math.Max(bot.Level + gained, LevelsForTown(bot.Town).Max) : LevelsForTown(bot.Town).Max;
                bot.Level = Math.Min(Math.Min(100, maxLevel), bot.Level + gained);
                _log.Write(bot.Account, bot.Name, $"выполнил квест {quest.Id} «{quest.Name}», уровень {bot.Level}");
                Loot(bot, quest.Where);
                bot.QuestIndex++;
                SaveProgress(bot);
            }
        }
        // Into town to hand the quest in, quickly, or to stay a while by the stash; when the town has no room, go on to the next quest.
        bot.Quick = _random.Next(10) < 7;
        int cap = TownCap(bot.Town);
        if (cap == 0 || InTown(bot) >= cap)
        {
            _log.Write(bot.Account, bot.Name, $"в городе {bot.Town} нет места: не заходит, идёт дальше по квестам");
            BeginQuest(bot, now);
            return;
        }
        bot.Phase = BotPhase.Arriving;
        int[] activities = _settings.TownActivities is [var lo, var hi] ? [lo, hi] : [2, 4];
        bot.ActivitiesLeft = bot.Quick ? 1 : _random.Next(activities[0], activities[1] + 1);
        bot.Doing = bot.Quick ? "прилетает в город сдать квест" : "возвращается в город";
    }

    int InTown(Bot bot) => _bots.Count(b => b != bot && b.Town == bot.Town && b.Leader is null && b.Phase != BotPhase.Questing);

    // What the out-of-sight quest did, as the log tells it: the zone cleared and its kills (simulated), and new gear when
    // the level has grown enough for better bases (real: shown on the bot's model in town and in parties).
    void Loot(Bot bot, string where)
    {
        _log.Write(bot.Account, bot.Name, $"зачистил зону ({where}): убито монстров {_random.Next(40, 220)}");
        if (bot.Level != bot.GearLevel)
        {
            Dress(bot);
            _log.Write(bot.Account, bot.Name, $"надел предметы лучше (ур. {bot.Level}): {BotGear.Describe(bot.Gear)}");
        }
        string[] events =
        [
            "вставил камень умения в гнездо", "переставил камни поддержки, сделал связь", "использовал сферу златокузнеца на кольцо",
            "перекрасил гнёзда хроматической сферой", "добавил гнездо сферой мастерства", "соединил гнёзда сферой соединения",
            "подобрал валюту и карту",
        ];
        if (_random.Next(3) == 0) _log.Write(bot.Account, bot.Name, events[_random.Next(events.Length)]);
    }

    void Dress(Bot bot)
    {
        bot.GearLevel = bot.Level;
        if (Program.GameLogicOrNull is not null)
        {
            var build = BotPlay.BuildControlledPlayer(bot.Class, bot.Level);
            if (IsPersistent && CharacterSaves.Load(bot.Name) is { } existing)
            {
                // Preserve non-equipment inventory tabs (main bag, flasks, etc.), earned XP and character progress.
                var merged = new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(existing.Inventories);
                foreach (var (row, items) in build.Inventories) merged[row] = items;
                var moduleData = existing.ModuleData is not null
                    ? new Dictionary<string, string>(existing.ModuleData) : new Dictionary<string, string>();
                if (build.ModuleData is not null)
                    foreach (var (k, v) in build.ModuleData) moduleData[k] = v;
                uint xp = Math.Max(existing.Experience ?? 0, build.Experience ?? 0);
                build = existing with
                {
                    Inventories = merged,
                    Passives = build.Passives,
                    SkillBar = build.SkillBar,
                    Experience = xp,
                    ModuleData = moduleData,
                };
            }
            Saves.Atomically(() =>
            {
                CharacterSaves.Write(bot.Name, build);
                PlayerInventories.Invalidate(bot.Name);
                CharacterEquipmentState.Replaced(bot.Name);
            });
            bot.Gear = build.Inventories.Values.SelectMany(i => i).Select(i => Program.Items.ByHash(i.Item.Type)?.Id)
                .Where(id => id is not null).Select(id => id!).ToList();
        }
        else
        {
            bot.Gear = BotGear.Pick(bot.Class, bot.Level, _random);
        }
    }

    internal static bool NeedsBuildOnResume(Bot bot, string? version, uint? experience) =>
        bot.Gear.Count == 0 || version != "1" || bot.Leader is null && (bot.GearLevel != bot.Level
            || experience is uint xp && (xp < CharacterStats.ExperienceFor(bot.Level)
                || xp >= CharacterStats.ExperienceFor(bot.Level + 1)));

    // ---- the town ----

    void Step(Bot bot, DateTime now, Dictionary<string, List<Player>> towns)
    {
        if (bot.Phase is BotPhase.Following or BotPhase.Departing) { StepWithLeader(bot, now); return; }
        if (bot.Phase == BotPhase.Questing)
        {
            if (now >= bot.Until) FinishQuest(bot, now);
            return;
        }
        towns.TryGetValue(bot.Town, out var observers);
        var layout = observers?.FirstOrDefault()?.Layout;
        switch (bot.Phase)
        {
            case BotPhase.Arriving:
                if (layout is not null)
                {
                    // Out of its own portal right at its place (by the stash, the guild stash or an NPC), as a player's town
                    // portal puts it where it is going: nobody gathers at the waypoint.
                    (bot.X, bot.Y) = PickSpot(bot, observers![0], out var kind);
                    (bot.TargetX, bot.TargetY, bot.TargetKind, bot.PlacedIn) = (bot.X, bot.Y, kind, bot.Town);
                    bot.WalkOrderedTo = null;
                    bot.ActivitiesLeft--;
                    _log.Write(bot.Account, bot.Name, $"вышел из портала в городе {bot.Town}");
                    Arrive(bot, now);
                }
                else
                {
                    bot.PlacedIn = null;
                    NextActivity(bot, now, observers);
                }
                break;
            case BotPhase.Walking:
                if (layout is null)
                {
                    if (now >= bot.Until) Arrive(bot, now);
                }
                else if (bot.Body is { Closed: false } body && ReferenceEquals(body.Layout, layout))
                {
                    double areaNow = body.Frames.Now(body);
                    var pos = body.Walk.Position(areaNow);
                    (bot.X, bot.Y) = (pos.X, pos.Y);
                    if (bot.WalkOrderedTo is { } ordered)
                    {
                        double elapsed = (now - bot.WalkStartedAt).TotalSeconds;
                        bool arrived = Math.Sqrt(Math.Pow(pos.X - ordered.X, 2) + Math.Pow(pos.Y - ordered.Y, 2)) < 3
                            || (!body.Walk.Moving(areaNow) && elapsed >= 0.4)
                            || elapsed >= 15;
                        if (arrived) { bot.WalkOrderedTo = null; Arrive(bot, now); }
                    }
                }
                else if (now >= bot.Until)
                {
                    Arrive(bot, now);
                }
                break;
            case BotPhase.Idling:
                if (now >= bot.Until) NextActivity(bot, now, observers);
                break;
            case BotPhase.Leaving:
                if (layout is null || now >= bot.Until) { BeginQuest(bot, now); return; }
                break;
        }
        if (layout is not null && bot.PlacedIn != bot.Town && bot.Phase is BotPhase.Walking or BotPhase.Idling or BotPhase.Leaving)
        {
            var any = observers![0];
            (bot.X, bot.Y) = PickSpot(bot, any, out var kind);
            (bot.TargetX, bot.TargetY, bot.TargetKind, bot.PlacedIn) = (bot.X, bot.Y, kind, bot.Town);
            bot.WalkOrderedTo = null;
            if (bot.Phase is BotPhase.Walking) Arrive(bot, now);
        }
    }

    void NextActivity(Bot bot, DateTime now, List<Player>? observers)
    {
        var any = observers?.FirstOrDefault();
        if (bot.ActivitiesLeft-- <= 0)
        {
            // Out through a portal where it stands, as players leave town: nobody queues at the waypoint.
            bot.Phase = BotPhase.Leaving;
            bot.Doing = "открывает портал";
            (bot.TargetX, bot.TargetY) = (bot.X, bot.Y);
            bot.Until = now.AddSeconds(1.5);
            return;
        }
        bot.Phase = BotPhase.Walking;
        bot.WalkOrderedTo = null;
        if (any is null)
        {
            bot.TargetKind = "spot";
            (bot.TargetX, bot.TargetY) = (bot.X, bot.Y);
            bot.Until = now.AddSeconds(_random.Next(2, 6));
        }
        else
        {
            (bot.TargetX, bot.TargetY) = PickSpot(bot, any, out var kind);
            bot.TargetKind = kind;
            bot.Until = now.AddSeconds(15);
        }
        bot.Doing = bot.TargetKind switch { "npc" => "идёт к NPC", "stash" => "идёт к тайнику", "object" => "идёт по городу", _ => "бродит по городу" };
    }

    // A walkable place in the town, at least StandApart cells from where every other bot of the town stands or is going:
    // mostly in a ring around the stash and the guild stash (as players stand by theirs), else by an NPC, else anywhere
    // walkable within reach of the waypoint. A quick visit goes to an NPC.
    (double X, double Y) PickSpot(Bot bot, Player town, out string kind)
    {
        var layout = town.Layout;
        var spots = town.StationaryBlockers;
        var stashes = spots.Where(b => b.Size == Player.StashBlockerSize).ToList();
        var npcs = spots.Where(b => b.Size == Player.NpcBlockerSize).ToList();
        var bodies = town.Blockers;
        var taken = _bots.Where(b => b != bot && b.Town == bot.Town && b.Phase != BotPhase.Questing && b.PlacedIn == bot.Town)
            .SelectMany(b => new[] { (b.X, b.Y), (b.TargetX, b.TargetY) }).ToList();
        (int X, int Y) centre = layout.Waypoint is { } wp ? (wp.GridX, wp.GridY) : (layout.SpawnX, layout.SpawnY);
        (double X, double Y) best = (centre.X, centre.Y);
        kind = "spot";
        bool Clear(int gx, int gy) => gx >= 1 && gy >= 1
            && (layout.GridW <= 0 || gx < layout.GridW - 1) && (layout.GridH <= 0 || gy < layout.GridH - 1)
            && layout.WalkableFor(gx, gy, 2) && !town.Barrier.Contains((gx, gy))
            && !ClientPathfinder.ObjectBlocks(bodies, gx, gy, 3, 6);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int roll = _random.Next(10);
            string k;
            double x, y;
            if (!bot.Quick && stashes.Count > 0 && roll < 8)
            {
                var stash = stashes[_random.Next(stashes.Count)];
                double angle = _random.NextDouble() * 2 * Math.PI, radius = _random.Next(12, 30);
                (k, x, y) = ("stash", stash.X + Math.Cos(angle) * radius, stash.Y + Math.Sin(angle) * radius);
            }
            else if (npcs.Count > 0 && (bot.Quick || roll < 9))
            {
                var npc = npcs[_random.Next(npcs.Count)];
                double angle = _random.NextDouble() * 2 * Math.PI, radius = _random.Next(npc.Size + 4, npc.Size + 14);
                (k, x, y) = ("npc", npc.X + Math.Cos(angle) * radius, npc.Y + Math.Sin(angle) * radius);
            }
            else
                (k, x, y) = ("spot", centre.X + _random.Next(-120, 121), centre.Y + _random.Next(-120, 121));
            int gx = (int)Math.Round(x), gy = (int)Math.Round(y);
            if (!Clear(gx, gy)) continue;
            (best, kind) = ((gx, gy), k);
            if (taken.All(t => Math.Max(Math.Abs(t.Item1 - gx), Math.Abs(t.Item2 - gy)) >= StandApart)) break;
        }
        return best;
    }

    void Arrive(Bot bot, DateTime now)
    {
        int[] idle = _settings.IdleSeconds is [var lo, var hi] ? [lo, hi] : [8, 75];
        bot.Phase = BotPhase.Idling;
        bot.Until = now.AddSeconds(bot.Quick ? _random.Next(3, 9) : _random.Next(idle[0], idle[1] + 1));
        var quest = bot.QuestIndex > 0 && bot.QuestIndex <= _route.Count ? _route[bot.QuestIndex - 1] : null;
        bot.Doing = bot.TargetKind switch
        {
            "npc" => quest is not null && (bot.Quick || _random.Next(2) == 0) ? $"сдаёт квест «{quest.Name}» NPC" : "разговаривает с NPC",
            "stash" => _random.Next(2) == 0 ? "у сундука: крафтит предметы" : "у сундука: перекладывает вещи",
            _ => "стоит и отдыхает",
        };
        _log.Write(bot.Account, bot.Name, $"{bot.Town}: {bot.Doing}");
    }

    // ---- live human towns & bodies ----

    // The towns human players are in now: HasClient ensures town bots never keep an empty town alive for themselves.
    static Dictionary<string, List<Player>> LiveTowns()
    {
        var towns = new Dictionary<string, List<Player>>(StringComparer.Ordinal);
        foreach (var player in Program.InGame.Keys)
        {
            if (!player.HasClient || player.Closed || player.LeavingArea || !player.Connection.PlayerSent || player.Instance is null) continue;
            if (!player.Layout.Id.Contains("_town", StringComparison.Ordinal)) continue;
            (towns.TryGetValue(player.Layout.Id, out var list) ? list : towns[player.Layout.Id] = []).Add(player);
        }
        return towns;
    }

    internal static (int Min, int Max) LevelsForTown(string town)
    {
        int act = town switch
        {
            "1_1_town" => 1, "1_2_town" => 2, "1_3_town" => 3, "1_4_town" => 4, "1_5_town" => 5,
            "2_6_town" => 6, "2_7_town" => 7, "2_8_town" => 8, "2_9_town" => 9, "2_10_town" => 10, _ => 0,
        };
        if (act == 0) return (68, 80);
        if (act == 1) return (1, 3);
        if (act == 10) return (59, 75);
        int center = 2 + (int)Math.Round((act - 1) * 65.0 / 9, MidpointRounding.AwayFromZero);
        return (center - 5, center + 5);
    }

    internal static int LevelForTown(int current, string town, Random random)
    {
        var range = LevelsForTown(town);
        return current >= range.Min && current <= range.Max ? current : random.Next(range.Min, range.Max + 1);
    }

    // Populate human-occupied towns up to their configured target in auto/custom modes; organic mode never backfills.
    internal void EnsureTownPopulation(Dictionary<string, List<Player>> liveTowns)
    {
        if (TownMode == "organic") return;
        foreach (string town in liveTowns.Keys)
        {
            int wanted = _townTargets.TryGetValue(town, out var existingTarget) ? existingTarget : RollTownTarget(town);
            int present;
            lock (_bots) present = _bots.Count(b => b.Town == town && b.Leader is null && b.Phase is not (BotPhase.Questing or BotPhase.Leaving));
            while (present++ < wanted)
            {
                Bot? visitor;
                lock (_bots) visitor = IsPersistent
                    ? _bots.FirstOrDefault(b => b.Leader is null && b.Phase == BotPhase.Questing && b.Town == town)
                    : _bots.Where(b => b.Leader is null && b.Phase == BotPhase.Questing)
                        .OrderByDescending(b => b.Town == town).FirstOrDefault();
                if (visitor is null)
                {
                    lock (_bots) if (_bots.Count >= _settings.MaxBots) break;
                    Spawn(town);
                }
                else
                {
                    if (!IsPersistent && visitor.Town != town)
                    {
                        visitor.Town = town; visitor.QuestIndex = Math.Max(0, _route.FindIndex(q => q.Town == town));
                        visitor.Level = LevelForTown(visitor.Level, town, _random);
                        Dress(visitor); SaveProgress(visitor);
                    }
                    visitor.Phase = BotPhase.Arriving; visitor.ActivitiesLeft = 3;
                }
            }
        }
    }

    static void StopBody(Bot bot)
    {
        if (bot.Body is { } old) { BotPlay.ClearControlledIntent(old); BotPlay.Close(old); }
        bot.Body = null;
        bot.WalkOrderedTo = null;
    }

    async Task StepTownBody(Bot bot, List<Player> observers)
    {
        if (_stop.IsCancellationRequested || Program.GameLogicOrNull is null || observers.Count == 0)
        { StopBody(bot); return; }
        bool sameInstance = bot.Body is { Closed: false } existing && observers.Any(o => ReferenceEquals(o.Instance, existing.Instance));
        if (!sameInstance)
        {
            StopBody(bot);
            var host = observers[0];
            var record = _accounts.GetCharacter(bot.CharacterId);
            if (record is null) return;
            (int X, int Y) spawnAt;
            lock (_bots) spawnAt = ((int)Math.Round(bot.X), (int)Math.Round(bot.Y));
            var player = BotPlay.Open(record, bot.Account, host);
            bot.Body = player;
            bot.WalkOrderedTo = null;
            await BotPlay.InitializeControlledPlayer(player, host, spawnAt);
            if (_stop.IsCancellationRequested || bot.Phase is BotPhase.Questing or BotPhase.Arriving)
            { StopBody(bot); return; }
            bot.PeerId = player.Id;
        }
        var current = bot.Body;
        if (current is null || current.Closed) return;
        if (bot.Phase == BotPhase.Walking)
        {
            (int X, int Y) goal;
            bool needOrder;
            lock (_bots)
            {
                goal = ((int)Math.Round(bot.TargetX), (int)Math.Round(bot.TargetY));
                needOrder = bot.WalkOrderedTo != goal;
                if (needOrder) { bot.WalkOrderedTo = goal; bot.WalkStartedAt = DateTime.UtcNow; }
            }
            if (needOrder) await BotPlay.OrderWalk(current, goal);
        }
    }

    async Task StepDepartingBody(Bot bot)
    {
        var body = bot.Body;
        var leader = bot.Leader;
        if (_stop.IsCancellationRequested || body is not { Closed: false } || leader is not { Closed: false }
            || !ReferenceEquals(body.Instance, leader.Instance))
            return;
        (int X, int Y) exit;
        bool needOrder;
        lock (_bots)
        {
            exit = ((int)Math.Round(bot.TargetX), (int)Math.Round(bot.TargetY));
            needOrder = bot.WalkOrderedTo != exit;
            if (needOrder) { bot.WalkOrderedTo = exit; bot.WalkStartedAt = DateTime.UtcNow; }
        }
        if (needOrder) await BotPlay.OrderWalk(body, exit);
        double areaNow = body.Frames.Now(body);
        var pos = body.Walk.Position(areaNow);
        lock (_bots)
        {
            if (bot.Phase != BotPhase.Departing) return;
            (bot.X, bot.Y) = (pos.X, pos.Y);
            double elapsed = (DateTime.UtcNow - bot.WalkStartedAt).TotalSeconds;
            bool reached = Math.Sqrt(Math.Pow(pos.X - exit.X, 2) + Math.Pow(pos.Y - exit.Y, 2)) < 4
                || (!body.Walk.Moving(areaNow) && elapsed >= 0.4)
                || elapsed >= 15;
            if (reached) Unparty(bot, DateTime.UtcNow, "ушёл через выход");
        }
    }

    async Task StepCombat(Bot bot)
    {
        lock (_bots) ExpireControl(bot);
        var leader = bot.Leader;
        if (_stop.IsCancellationRequested || bot.Phase != BotPhase.Following || leader is not { Closed: false, Connection.PlayerSent: true }
            || leader.LeavingArea || Program.GameLogicOrNull is null)
        { StopBody(bot); return; }
        if (bot.Body is null || bot.Body.Closed || !ReferenceEquals(bot.Body.Instance, leader.Instance))
        {
            StopBody(bot);
            var record = _accounts.GetCharacter(bot.CharacterId);
            if (record is null) return;
            var player = BotPlay.Open(record, bot.Account, leader);
            player.PartySize = PartyRoster.Size(bot.Party);
            bot.Body = player;
            bot.ControlEpoch = Guid.NewGuid();
            await BotPlay.InitializeControlledPlayer(player, leader);
            if (_stop.IsCancellationRequested || bot.Phase != BotPhase.Following || !ReferenceEquals(bot.Leader, leader))
            { StopBody(bot); return; }
            bot.PeerId = player.Id;
        }
        var current = bot.Body;
        if (current is null) return;
        await Program.SetPartySize(current, PartyRoster.Size(bot.Party));
        await BotPlay.StepControlledPlayer(current, leader);
        if (bot.Phase != BotPhase.Following || !ReferenceEquals(bot.Body, current)) return;
        var position = current.Walk.Position(current.Frames.Now(current));
        bool levelChanged = bot.Level != current.Pools.Level;
        (bot.X, bot.Y, bot.Level) = (position.X, position.Y, current.Pools.Level);
        if (levelChanged) await SendParty(leader, bot.Party);
    }
}
