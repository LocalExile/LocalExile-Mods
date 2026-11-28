using GameServer.Core.Accounts;
using GameServer.Modules.PlayerBots;
using GameServer.Storage;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class BotResumeLimitTests
{
    [Fact]
    public void Restarting_at_a_zero_limit_preserves_the_character_for_a_later_restart()
    {
        string dir = Directory.CreateTempSubdirectory("bot-resume-").FullName;
        string db = Path.Combine(dir, "test.db"), state = Path.Combine(dir, "bots.json");
        Saves.Open(db);
        try
        {
            var store = new SqliteAccountStore(Saves.Db);
            var account = new Account(Guid.NewGuid(), "AuditBot", "PlayerBots:test", "", DateTimeOffset.UtcNow);
            store.AddAccount(account);
            var actor = new AccountService(store).CreateCharacter(account.Id, "AuditBot", CharacterClass.Ranger).Value!;
            File.WriteAllText(state, JsonSerializer.Serialize(new[] { new { AccountId = account.Id, CharacterId = actor.Id,
                Account = account.Name, actor.Name, actor.Class, Level = 1, QuestIndex = 0, Deaths = 0 } }));
            foreach (int limit in new[] { 0, 0, 1 })
            {
                var director = new BotDirector(new(MaxBots: limit), [], new BotLog(dir), _ => { }, state);
                director.Start();
                try { Assert.Equal(limit, director._bots.Count); }
                finally { director.Stop(); }
                Assert.NotNull(store.GetCharacter(actor.Id));
                using var saved = JsonDocument.Parse(File.ReadAllText(state));
                Assert.Equal(actor.Id, Assert.Single(saved.RootElement.EnumerateArray()).GetProperty("CharacterId").GetGuid());
            }
        }
        finally { TestEnvironment.ReleaseSaves(db); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Town_bounds_scale_in_auto_override_in_custom_and_never_backfill_in_organic()
    {
        Assert.Equal((2, 5), BotDirector.TownBounds(new(MaxBots: 100, TownBotsMode: "auto"), 11));
        Assert.Equal((4, 10), BotDirector.TownBounds(new(MaxBots: 200, TownBotsMode: "auto"), 11));
        Assert.Equal((0, 0), BotDirector.TownBounds(new(MaxBots: 0, TownBotsMode: "auto"), 11));
        Assert.Equal((10, 15), BotDirector.TownBounds(new(MaxBots: 100, TownBotsMode: "custom", TownBots: [10, 15]), 11));
        Assert.Equal((0, 0), BotDirector.TownBounds(new(MaxBots: 100, TownBotsMode: "custom", TownBots: [0, 0]), 11));
        Assert.Equal((12, 12), BotDirector.TownBounds(new(MaxBots: 12, TownBotsMode: "custom", TownBots: [20, 30]), 11));
        Assert.Equal((0, 100), BotDirector.TownBounds(new(MaxBots: 100, TownBotsMode: "organic"), 11));
    }

    [Fact]
    public void Persistent_mode_recovers_missing_state_from_sqlite_and_retains_endgame_characters()
    {
        string dir = Directory.CreateTempSubdirectory("bot-persistent-").FullName;
        string db = Path.Combine(dir, "test.db"), state = Path.Combine(dir, "bots.json");
        Saves.Open(db);
        try
        {
            var store = new SqliteAccountStore(Saves.Db);
            var account = new Account(Guid.NewGuid(), "EndgameExile_bot", "PlayerBots:persist", "", DateTimeOffset.UtcNow);
            store.AddAccount(account);
            var actor = new AccountService(store).CreateCharacter(account.Id, "EndgameExile", CharacterClass.Witch).Value!;
            store.UpdateCharacter(actor with { Level = 72, LastTown = "2_11_endgame_town" });

            var director = new BotDirector(new(MaxBots: 5, PersistenceMode: "persistent"), [], new BotLog(dir), _ => { }, state);
            director.Start();
            try
            {
                var recovered = Assert.Single(director._bots);
                Assert.Equal(actor.Id, recovered.CharacterId);
                Assert.Equal(72, recovered.Level);
                Assert.Equal("2_11_endgame_town", recovered.Town);
                director.BeginQuest(recovered, DateTime.UtcNow);
                Assert.Single(director._bots);
                Assert.Equal(BotPhase.Questing, recovered.Phase);
                Assert.Equal("2_11_endgame_town", recovered.Town);
            }
            finally { director.Stop(); }
            Assert.NotNull(store.GetCharacter(actor.Id));
        }
        finally { TestEnvironment.ReleaseSaves(db); Directory.Delete(dir, true); }
    }
}
