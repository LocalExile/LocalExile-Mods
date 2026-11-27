using System.Globalization;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.SkipCampaign;

/// <summary>
/// LocalExile Gateway <see cref="IMod"/> entry point for <c>skip-campaign</c>:
/// <list type="bullet">
/// <item>Subscribes to <see cref="GameEvents.CharacterCreated"/> so characters created with the NativeMods
/// <c>Skip Campaign</c> option (<c>|nm:skip_campaign=1</c>) spawn directly in Karui Shores at endgame level
/// with <c>SuppressStartKit = true</c> and zero Twilight Strand flash.</item>
/// <item>Applies one-time idempotent campaign progression on first <see cref="GameEvents.PlayerEntered"/>
/// (Kitava resistance penalties, campaign passive books, waypoints, refund points, ascendancy trials, gold).</item>
/// <item>Sends <see cref="SkipCampaignAppliedNotice"/> over NativeMods <c>HostRuleset 0xA0</c> for a HUD toast
/// and provides <c>/skipcampaign [status|apply]</c> as a graceful vanilla-client fallback.</item>
/// </list>
/// </summary>
public sealed class SkipCampaignMod : IMod, IDisposable
{
    sealed record RawSettings(
        bool Enabled = true,
        bool RequireLeagueCompletion = false,
        string StartingAreaId = SkipCampaignProtocol.DefaultStartingAreaId,
        int StartingLevel = SkipCampaignProtocol.DefaultStartingLevel,
        bool GrantCampaignPassiveBooks = true,
        bool GrantAscendancyTrials = true,
        long StartingGold = SkipCampaignProtocol.DefaultStartingGold,
        ushort RefundPoints = SkipCampaignProtocol.DefaultRefundPoints);

    readonly NativeBridgeProtocol _bridge = new();
    SkipCampaignHandler? _handler;
    bool _disposed;

    public void Load(IModContext mod)
    {
        SkipCampaignSettings settings = LoadSettings(mod);
        _handler = new SkipCampaignHandler(
            settings,
            new LocalExileEligibilitySource(),
            mod.Log,
            persistPending: PersistPendingMarker);

        string id = mod.Manifest.Id;

        GameEvents.CharacterCreated.Add(id, OnCharacterCreated);
        GameEvents.PlayerEntered.Add(id, OnPlayerEntered);
        GameEvents.PlayerLeaving.Add(id, OnPlayerLeaving);
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, HandleRulesetAction);
        GameEvents.Command(id, "skipcampaign",
            "/skipcampaign [status | apply] (Skip Campaign status and vanilla-client fallback)",
            HandleCommand);

        mod.Log($"Loaded Skip Campaign (enabled={settings.Enabled}, requireLeagueCompletion={settings.RequireLeagueCompletion}, start={settings.StartingAreaId} Lv.{settings.StartingLevel}, passiveBooks={settings.GrantCampaignPassiveBooks}, ascendancyTrials={settings.GrantAscendancyTrials}).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bridge.Clear();
        _handler = null;
    }

    Task OnCharacterCreated(CharacterCreatedContext ctx)
    {
        if (_handler is not { } handler) return Task.CompletedTask;

        SkipCampaignCharacterCreatedEffect effect = handler.OnCharacterCreated(
            new SkipCampaignCharacterCreatedInput(
                Account: ctx.Account,
                CharacterId: ctx.CharacterId,
                CharacterName: ctx.Name,
                League: ctx.League,
                Hardcore: ctx.Hardcore,
                Ssf: ctx.Ssf,
                CreationOptions: ctx.CreationOptions));

        if (effect.Applied)
        {
            ctx.StartingAreaId = effect.StartingAreaId;
            ctx.StartingLevel = effect.StartingLevel;
            ctx.SuppressStartKit = effect.SuppressStartKit;
        }

        return Task.CompletedTask;
    }

    async Task OnPlayerEntered(Player player)
    {
        if (_handler is not { } handler) return;

        var adapter = new LocalExilePlayerWorldAdapter(player, Program.WorldOf(player));
        SkipCampaignFirstEnterResult result = await handler.OnFirstEnterAsync(adapter).ConfigureAwait(false);
        if (!result.Applied || result.Notice is not { } notice) return;

        if (_bridge.IsPluginReady(player))
        {
            await _bridge.SendAppliedNoticeAsync(player, notice).ConfigureAwait(false);
        }
        else if (player.Character.Name is { Length: > 0 } name)
        {
            _bridge.QueueNotice(name, notice);
        }

        await Client.For(player).SendChat(FormatChatSummary(notice)).ConfigureAwait(false);
    }

    Task OnPlayerLeaving(Player player, string _)
    {
        if (player.Closed)
        {
            _bridge.Remove(player);
        }

        return Task.CompletedTask;
    }

    Task<bool> HandleRulesetAction(Player player, byte[] message) =>
        _bridge.TryHandleRulesetActionAsync(player, message);

    async Task HandleCommand(Player player, string[] args)
    {
        if (_handler is not { } handler) return;

        string sub = args.Length > 0 ? args[0].Trim() : string.Empty;
        if (string.Equals(sub, "apply", StringComparison.OrdinalIgnoreCase))
        {
            var adapter = new LocalExilePlayerWorldAdapter(player, Program.WorldOf(player));
            SkipCampaignFirstEnterResult result = await handler.ApplyDirectAsync(adapter).ConfigureAwait(false);
            if (!result.Applied)
            {
                string reason = result.DenialReason ?? (result.AlreadyFinalized
                    ? "Campaign skip has already been applied to this character."
                    : "Campaign skip was not applied.");
                await Client.For(player).SendChat($"[Skip Campaign] {reason}").ConfigureAwait(false);
                return;
            }

            if (result.Notice is { } notice)
            {
                if (_bridge.IsPluginReady(player))
                {
                    await _bridge.SendAppliedNoticeAsync(player, notice).ConfigureAwait(false);
                }

                await Client.For(player).SendChat(FormatChatSummary(notice)).ConfigureAwait(false);
            }

            return;
        }

        SkipCampaignSettings cfg = handler.Settings;
        var status = _bridge.StatusOf(player);
        string clientState = status is { HasSkipCampaignPlugin: true }
            ? "NativeMods plugin connected"
            : status is { HasNativeHost: true }
                ? "NativeMods host connected (skip-campaign client plugin not loaded)"
                : "vanilla client (use /skipcampaign apply)";
        var world = new LocalExilePlayerWorldAdapter(player, Program.WorldOf(player));
        string charState = SkipCampaignHandler.IsPersistedFinalized(world) ? "applied" : "not applied";

        await Client.For(player).SendChat(string.Create(
            CultureInfo.InvariantCulture,
            $"[Skip Campaign] enabled={cfg.Enabled}, requireLeagueCompletion={cfg.RequireLeagueCompletion}, start={cfg.StartingAreaId} Lv.{cfg.StartingLevel}, passiveBooks={cfg.GrantCampaignPassiveBooks}, ascendancyTrials={cfg.GrantAscendancyTrials}, gold={cfg.StartingGold:N0}, refunds={cfg.RefundPoints} | character: {charState} | client: {clientState}."))
            .ConfigureAwait(false);
    }

    static void PersistPendingMarker(string characterName, string startingAreaId, int startingLevel)
    {
        if (!Saves.IsOpen || string.IsNullOrWhiteSpace(characterName)) return;
        string encoded = SkipCampaignHandler.EncodePendingMarker(startingAreaId, startingLevel);
        CharacterSaves.ChangeModuleData(characterName, SkipCampaignProtocol.PendingCharacterDataKey, _ => encoded);
    }

    static SkipCampaignSettings LoadSettings(IModContext mod)
    {
        try
        {
            RawSettings raw = mod.Settings<RawSettings>();
            return new SkipCampaignSettings
            {
                Enabled = raw.Enabled,
                RequireLeagueCompletion = raw.RequireLeagueCompletion,
                StartingAreaId = string.IsNullOrWhiteSpace(raw.StartingAreaId)
                    ? SkipCampaignProtocol.DefaultStartingAreaId
                    : raw.StartingAreaId.Trim(),
                StartingLevel = Math.Clamp(raw.StartingLevel, 1, 100),
                GrantCampaignPassiveBooks = raw.GrantCampaignPassiveBooks,
                GrantAscendancyTrials = raw.GrantAscendancyTrials,
                StartingGold = Math.Clamp(raw.StartingGold, 0, 10_000_000),
                RefundPoints = (ushort)Math.Clamp(raw.RefundPoints, (ushort)0, (ushort)255),
            };
        }
        catch (Exception ex)
        {
            mod.Log($"Using default settings ({ex.Message}).");
            return SkipCampaignSettings.Default;
        }
    }

    static string FormatChatSummary(SkipCampaignAppliedNotice notice)
    {
        string townName = string.Equals(notice.StartingAreaId, SkipCampaignProtocol.DefaultStartingAreaId, StringComparison.OrdinalIgnoreCase)
            ? "Karui Shores"
            : notice.StartingAreaId;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[Skip Campaign] Lv.{notice.StartingLevel} in {townName}: {notice.QuestFlagsApplied} quest flags, {notice.WaypointsGranted} waypoints, {notice.RefundPoints} refund pts, {notice.AscendancyPointsEarned} ascendancy pts, +{notice.GoldGranted:N0} gold.");
    }

    sealed class LocalExileEligibilitySource : ISkipCampaignEligibilitySource
    {
        public IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> GetLeagueCharacters(
            string account,
            string league,
            bool includeDead = false)
        {
            return [.. CharacterSaves.GetLeagueCharacters(account, league, includeDead).Select(c =>
                new SkipCampaignLeagueCharacterSnapshot(
                    Id: c.Id,
                    Name: c.Name,
                    Level: c.Level,
                    League: c.League,
                    AreaId: c.AreaId,
                    LastTown: c.LastTown,
                    HardcoreDead: c.HardcoreDead,
                    PermanentlyDead: c.PermanentlyDead))];
        }
    }

    sealed class LocalExilePlayerWorldAdapter(Player player, IPlayerWorld world) : ISkipCampaignPlayerWorld
    {
        public Guid CharacterId => player.Character.Id ?? Guid.Empty;
        public string CharacterName => player.Character.Name ?? string.Empty;
        public string Account => player.Character.Account ?? string.Empty;
        public string League => player.Character.League ?? string.Empty;
        public int Level => player.Pools.Level;
        public uint Gold => world.Gold;
        public ushort RefundPoints => world.RefundPoints;

        public bool HasQuestFlag(string flag, bool earnedOnly = false) =>
            world.HasQuestFlag(flag, earnedOnly);

        public Task<bool> SetLevelAsync(int level, string? reason = null, CancellationToken cancellationToken = default) =>
            world.SetLevelAsync(level, reason);

        public Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true, CancellationToken cancellationToken = default) =>
            world.SetQuestFlagsAsync(flags, on);

        public Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null, CancellationToken cancellationToken = default) =>
            world.GrantWaypointsAsync(areaIds, reason);

        public Task<ushort> SetRefundPointsAsync(ushort points, CancellationToken cancellationToken = default) =>
            world.SetRefundPointsAsync(points);

        public Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys, CancellationToken cancellationToken = default) =>
            world.AwardAscendancyAsync(awardKeys);

        public Task<uint> AddGoldAsync(long delta, string? reason = null, CancellationToken cancellationToken = default) =>
            world.AddGoldAsync(delta, reason);

        public Task<bool> TravelToAsync(string areaId, string reason, CancellationToken cancellationToken = default) =>
            world.TravelTo(areaId, reason);

        public string? GetCharacterData(string key) =>
            world.CharacterData.Get(key);

        public bool ChangeCharacterData(string key, Func<string?, string?> change) =>
            world.CharacterData.Change(key, change);

        public bool RemoveCharacterData(string key) =>
            world.CharacterData.Remove(key);
    }
}
