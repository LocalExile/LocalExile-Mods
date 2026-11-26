using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.PlayerBots;

/// <summary>Bots the server plays, so a new server feels lived in (BotDirector). On while a league lists the mod (no league
/// does by default). Settings in settings.json and the route in story-main.md and story-side.md, beside this file; the log
/// and the bots' state in data/mods/playerbots/, as a mod keeps what it saves (its own folder is replaced by an update, and
/// a write there reloads it). The economy snapshot of tools/collect_economy.py is in economy/&lt;league&gt;/ of the mod's
/// source, for the trading to come.</summary>
public sealed class PlayerBotsModule : IModule
{
    // One director for the process: a hot reload loads this assembly again, and the old one's bots must go first.
    const string Running = "LocalExile.PlayerBots.Stop";

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        (AppContext.GetData(Running) as Action)?.Invoke();
        var settings = module.Settings<BotSettings>();
        var route = BotQuest.Route(module.Folder);
        if (route.Count == 0) { module.Log("bots: no route (story-main.md, story-side.md)"); return; }
        // What it saves. The bots' state of the time it was LocalExile's module is copied here once: without it, a start
        // would take every bot account for an orphan and delete it. A copy, so a LocalExile with the module keeps its bots.
        string data = Path.Combine("data", "mods", module.Manifest.Id), state = Path.Combine(data, "bots.json");
        string old = Path.Combine(Program.ModulesFolder, "Custom", "PlayerBots", "state", "bots.json");
        if (!File.Exists(state) && File.Exists(old))
        {
            Directory.CreateDirectory(data);
            File.Copy(old, state);
            module.Log($"bots: their state copied from {old} to {state}");
        }
        var director = new BotDirector(settings, route, new BotLog(Path.Combine(data, "logs")), module.Log, state);
        AppContext.SetData(Running, (Action)director.Stop);
        module.Lifetime.Own(new StopOnDispose(director.Stop));
        director.Start();
        // Social owns the shared roster; this module resolves town actors that have no human socket.
        GameEvents.Packet(InstanceSocial.PartyInviteId).Add(module.Manifest.Id, async (player, bytes) =>
            await director.Invite(player, InstanceSocial.ReadPartyInvite(bytes)) != BotInvite.NotFound);
        // The party panel's invite box reaches a bot by name (C→S 0x93 is passed on to this module), but a player who
        // types a command instead needs the same path: '/botinvite' takes the invite, '/botleave' takes the party away,
        // so a bot can be tried out without opening the social panel.
        GameEvents.Command(module.Manifest.Id, "botinvite", "/botinvite <bot name> (invite a bot you can see into your party)", async (player, args) =>
        {
            string name = string.Join(' ', args).Trim();
            if (name.Length == 0) { await director.Tell(Program.PlayerOf(player), "Usage: /botinvite <bot name>"); return; }
            var result = await director.Invite(player, name);
            await director.Tell(Program.PlayerOf(player), result switch
            {
                BotInvite.Joined => $"{name} joined your party.",
                BotInvite.Refused => $"Your party is full or unavailable, so {name} stayed behind.",
                _ => $"No bot you can see is called \"{name}\" (names are in data/mods/playerbots/bots.json).",
            });
        });
        GameEvents.Command(module.Manifest.Id, "botleave", "/botleave (the bots following you leave and go back to their quests)", async (player, args) =>
        {
            bool left = await director.LeaveAny(player);
            await director.Tell(Program.PlayerOf(player), left ? "The bots left your party and went back to their quests." : "No bots are following you.");
        });
    }

    sealed class StopOnDispose(Action stop) : IDisposable
    {
        Action? remaining = stop;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
