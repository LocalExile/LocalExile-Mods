using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Social;

namespace GameServer.Modules.PlayerBots;

/// <summary>A bot in a player's party: invited by name where the player sees it, it joins at once and follows the player
/// everywhere (towns and maps). Leaving or disbanding the party (or kicking the bot) walks it to the nearest way out,
/// where it leaves and takes up its quests again. Party ids are shared with real players through the Social module.</summary>
/// <summary>What an invite from the party panel did: no bot by that name was reachable, the leader's party is full,
/// or the bot joined.</summary>
enum BotInvite : byte { NotFound, Refused, Joined }

sealed partial class BotDirector
{
    static readonly TimeSpan LeaderGrace = TimeSpan.FromSeconds(30);

    // C→S 0x93 "Invite to Party" with a bot's name: NotFound when no bot the player can reach has that name, Refused when
    // their party is already full (the invite is still consumed, so it does not reach the Social module), Joined
    // otherwise.
    public async Task<BotInvite> Invite(Player player, string name)
    {
        Bot? bot;
        uint party;
        lock (_bots)
        {
            bot = _bots.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
                && b.Leader is null
                // Visible to this player in the same instance, or standing in the same town (a module reload clears Body
                // until the next tick; BeginQuest resets Phase to Questing before changing Town).
                && (ReferenceEquals(b.Body?.Instance, player.Instance)
                    || (string.Equals(b.Town, player.Layout.Id, StringComparison.OrdinalIgnoreCase)
                        && b.Phase is BotPhase.Arriving or BotPhase.Walking or BotPhase.Idling)));
            if (bot is null) return BotInvite.NotFound;

            if (!PartyRoster.TryAddCompanion(player, Companion(bot, player.Layout.Hash), out party)) return BotInvite.Refused;
            try
            {
                if (!IsPersistent)
                {
                    // Select from the inviting town once. Later area changes preserve earned levels and experience.
                    int level = LevelForTown(bot.Level, bot.Town, _random);
                    if (level != bot.Level) { bot.Level = level; Dress(bot); StopBody(bot); }
                }
                SaveProgress(bot);
            }
            catch
            {
                PartyRoster.RemoveCompanion(party, bot.Account);
                _ = PartyRoster.Publish(party);
                throw;
            }
            var leader = PartyRoster.Leader(party)!;
            // Keep an existing live town body if it is already in the leader's instance: zero despawn flicker.
            if (bot.Body is not null && !ReferenceEquals(bot.Body.Instance, leader.Instance)) StopBody(bot);
            (bot.Leader, bot.Party, bot.Phase) = (leader, party, BotPhase.Following);
            (bot.LeaderName, bot.LeaderGoneSince) = (Program.CharacterName(leader), null);
            bot.ControlEpoch = Guid.NewGuid();
            bot.Doing = $"в группе с {Program.CharacterName(player)}";
            _log.Write(bot.Account, bot.Name, $"принял приглашение в группу от {Program.CharacterName(player)}: идёт следом");
        }
        await SendParty(player, party);
        return BotInvite.Joined;
    }

    // Out of the party: keep the live body walking to the nearest way out of the area it is in, then resume quests.
    void Depart(Bot bot, string why)
    {
        if (bot.Body is { } current) BotPlay.ClearControlledIntent(current);
        bot.Control = null;
        bot.Phase = BotPhase.Departing;
        bot.WalkOrderedTo = null;
        bot.Doing = $"{why}: уходит к ближайшему выходу";
        if (bot.Leader is { } leader)
        {
            var layout = leader.Layout;
            var exits = layout.AreaTransitions.Select(t => ((double)t.Transition.GridX, (double)t.Transition.GridY))
                .Concat(layout.Waypoint is { } wp ? [((double)wp.GridX, (double)wp.GridY)] : []).ToList();
            if (exits.Count == 0) exits.Add((layout.SpawnX, layout.SpawnY));
            (bot.TargetX, bot.TargetY) = exits.MinBy(e => Math.Abs(e.Item1 - bot.X) + Math.Abs(e.Item2 - bot.Y));
        }
        _log.Write(bot.Account, bot.Name, bot.Doing);
    }

    // A following bot is in its leader's area, and that is the area its row shows: 0xa7 +0xa8 is the WorldAreas HASH16 the
    // party panel resolves for the member's area name (a level showed "Travelling...", #715).
    static PartyCompanion Companion(Bot bot, ushort area)
    {
        var cls = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)bot.Class));
        return new(bot.Account, bot.Name, InstanceSocial.KnownPlayer(bot.Account, bot.Name, area,
            CharacterClassByte.Encode((byte)cls.IntegerId, 0)));
    }

    async Task SendParty(Player leader, uint party)
    {
        lock (_bots)
            foreach (var bot in _bots.Where(b => b.Party == party && b.Phase == BotPhase.Following))
                PartyRoster.UpdateCompanion(party, Companion(bot, leader.Layout.Hash));
        await PartyRoster.Publish(party);
    }

    // Social's first-entry tick publishes the roster when a connection is not ready yet.
    Task SendPartyWhenReady(Player player, uint party) =>
        player is { Closed: false, Connection.PlayerSent: true } ? SendParty(player, party) : Task.CompletedTask;

    static Task Send(Player player, byte[] bytes, string? log) => player.Connection.Outbox is { } outbox ? outbox(bytes, log) : Task.CompletedTask;

    /// <summary>A chat line back to the player who ran a bot command.</summary>
    public Task Tell(IPlayer player, string text) =>
        Send(Program.PlayerOf(player), InstanceChat.ChatLine(text), $"0x0017 {text}");

    /// <summary>'/botleave': every bot following this player leaves and goes back to its quests.</summary>
    public async Task<bool> LeaveAny(Player player)
    {
        uint party = PartyRoster.PartyOf(player);
        if (!PartyRoster.IsLeader(player, party)) return false;
        bool left = false;
        lock (_bots)
            foreach (var bot in _bots.Where(b => b.Party == party && b.Phase == BotPhase.Following))
            {
                PartyRoster.RemoveCompanion(party, bot.Account);
                Depart(bot, "группа распущена");
                left = true;
            }
        if (left) await PartyRoster.Publish(party);
        return left;
    }

    // A party bot's lifecycle step (under the bots' lock): roster checks, leader area rebinding, and departing cleanup.
    // Actual movement/combat runs on its live Body outside the lock in StepCombat / StepDepartingBody.
    void StepWithLeader(Bot bot, DateTime now)
    {
        if (bot.Phase == BotPhase.Following)
        {
            if (!PartyRoster.ContainsCompanion(bot.Party, bot.Account)) Depart(bot, "исключён из группы");
            else if (PartyRoster.Leader(bot.Party) is { } current && !ReferenceEquals(current, bot.Leader))
            {
                StopBody(bot);
                bot.Control = null;
                (bot.Leader, bot.LeaderName, bot.LeaderGoneSince) = (current, Program.CharacterName(current), null);
            }
        }
        var leader = bot.Leader;
        if (leader is null || leader.Closed || !Program.InGame.ContainsKey(leader))
        {
            StopBody(bot);
            // An area change is a new connection of the same character: the bot goes on with it, and its client gets the
            // party again. Only a leader not back within LeaderGrace leaves it.
            var next = Program.InGame.Keys.FirstOrDefault(c => c.HasClient && !c.Closed && !ReferenceEquals(c, leader)
                && string.Equals(Program.CharacterName(c), bot.LeaderName, StringComparison.Ordinal));
            if (next is null)
            {
                bot.LeaderGoneSince ??= now;
                if (bot.Phase == BotPhase.Departing) Unparty(bot, now, "ушёл через выход");
                else if (now - bot.LeaderGoneSince > LeaderGrace)
                {
                    if (leader is not null) PartyRoster.RemoveHuman(leader, bot.Party);
                    _ = PartyRoster.Publish(bot.Party);
                    if (PartyRoster.Leader(bot.Party) is null) Unparty(bot, now, "лидер группы вышел из игры");
                }
                return;
            }
            bool first = !_bots.Any(b => b != bot && ReferenceEquals(b.Leader, next));
            foreach (var member in _bots.Where(b => b.Party == bot.Party)) (member.Leader, member.LeaderGoneSince) = (next, null);
            if (first) _ = PartyRoster.Rebind(next);
            leader = next;
        }
        if (leader.LeavingArea || !leader.Connection.PlayerSent || leader.Instance is not { } instance
            || instance.Player(leader.Id) is null)
        {
            if (bot.Phase == BotPhase.Departing) Unparty(bot, now, "ушёл через выход");
            return;
        }
        if (bot.Phase == BotPhase.Departing && (bot.Body is null || bot.Body.Closed || !ReferenceEquals(bot.Body.Instance, instance)))
        {
            Unparty(bot, now, "ушёл через выход");
        }
    }

    // Back to its quests (from where it left them), out of the party.
    void Unparty(Bot bot, DateTime now, string why)
    {
        bot.Control = null;
        StopBody(bot);
        _log.Write(bot.Account, bot.Name, $"покинул группу ({why}): продолжает квесты");
        uint party = bot.Party;
        if (PartyRoster.RemoveCompanion(party, bot.Account)) _ = PartyRoster.Publish(party);
        (bot.Leader, bot.LeaderName, bot.Party) = (null, null, 0);
        BeginQuest(bot, now);
    }
}
