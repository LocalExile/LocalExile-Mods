using System.Collections.Concurrent;
using System.Globalization;

namespace LocalExile.Mods.SkipCampaign;

/// <summary>
/// Gateway-independent projection of LocalExile <c>CharacterCreatedContext</c> passed into
/// <see cref="SkipCampaignHandler.OnCharacterCreated"/>.
/// </summary>
public sealed record SkipCampaignCharacterCreatedInput(
    string Account,
    Guid CharacterId,
    string CharacterName,
    string League,
    bool Hardcore,
    bool Ssf,
    IReadOnlyDictionary<string, string> CreationOptions);

/// <summary>
/// Result of evaluating a brand-new character against <see cref="SkipCampaignHandler"/> before
/// Auth sends <c>S-&gt;C 0x15 / 0x13</c> and assigns the first area.
/// </summary>
public sealed record SkipCampaignCharacterCreatedEffect(
    bool Applied,
    string? StartingAreaId = null,
    int? StartingLevel = null,
    bool SuppressStartKit = false,
    string? DenialReason = null);

/// <summary>
/// Gateway-independent projection of LocalExile <c>CharacterSaves.LeagueCharacterInfo</c> used to verify
/// prior league completion while enforcing exact league, SSF, and Hardcore-death isolation.
/// </summary>
public sealed record SkipCampaignLeagueCharacterSnapshot(
    Guid Id,
    string Name,
    int Level,
    string League,
    string AreaId,
    string? LastTown,
    bool HardcoreDead = false,
    bool PermanentlyDead = false);

/// <summary>
/// Abstraction over LocalExile's <c>CharacterSaves.GetLeagueCharacters(account, league, includeDead)</c>.
/// </summary>
public interface ISkipCampaignEligibilitySource
{
    /// <summary>
    /// Lists characters owned by <paramref name="account"/> in exact <paramref name="league"/>,
    /// excluding dead Hardcore characters when <paramref name="includeDead"/> is <c>false</c>.
    /// </summary>
    IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> GetLeagueCharacters(
        string account,
        string league,
        bool includeDead = false);
}

/// <summary>
/// Abstraction over LocalExile's <c>IPlayerWorld</c> progression facades so
/// <see cref="SkipCampaignHandler"/> can be tested deterministically without a live Gateway.
/// </summary>
public interface ISkipCampaignPlayerWorld
{
    Guid CharacterId { get; }
    string CharacterName { get; }
    string Account { get; }
    string League { get; }
    int Level { get; }
    uint Gold { get; }
    ushort RefundPoints { get; }

    bool HasQuestFlag(string flag, bool earnedOnly = false);
    Task<bool> SetLevelAsync(int level, string? reason = null, CancellationToken cancellationToken = default);
    Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true, CancellationToken cancellationToken = default);
    Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null, CancellationToken cancellationToken = default);
    Task<ushort> SetRefundPointsAsync(ushort points, CancellationToken cancellationToken = default);
    Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys, CancellationToken cancellationToken = default);
    Task<uint> AddGoldAsync(long delta, string? reason = null, CancellationToken cancellationToken = default);
    Task<bool> TravelToAsync(string areaId, string reason, CancellationToken cancellationToken = default);
    string? GetCharacterData(string key);
    bool ChangeCharacterData(string key, Func<string?, string?> change);
    bool RemoveCharacterData(string key);
}

/// <summary>
/// Pending one-time post-entry grant queued during <c>GameEvents.CharacterCreated</c> and consumed on first login.
/// </summary>
public sealed record SkipCampaignPendingGrant(
    Guid CharacterId,
    string CharacterName,
    string Account,
    string League,
    string StartingAreaId,
    int StartingLevel,
    SkipCampaignSettings SettingsSnapshot,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Result of running <see cref="SkipCampaignHandler.OnFirstEnterAsync"/> or <see cref="SkipCampaignHandler.ApplyDirectAsync"/>.
/// </summary>
public sealed record SkipCampaignFirstEnterResult(
    bool Applied,
    bool AlreadyFinalized,
    SkipCampaignAppliedNotice? Notice = null,
    string? DenialReason = null);

/// <summary>
/// Core domain handler implementing opt-in Skip Campaign eligibility checks, <c>CharacterCreated</c>
/// starting-state overrides, durable pending-grant handoff, and idempotent first-enter progression grants.
/// </summary>
public sealed class SkipCampaignHandler
{
    private readonly ISkipCampaignEligibilitySource _eligibilitySource;
    private readonly Action<string, string, int>? _persistPending;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, SkipCampaignPendingGrant> _pendingByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, SkipCampaignPendingGrant> _pendingById = new();
    private readonly ConcurrentDictionary<string, byte> _finalizedKeys = new(StringComparer.OrdinalIgnoreCase);

    public SkipCampaignSettings Settings { get; }

    public SkipCampaignHandler(
        SkipCampaignSettings settings,
        ISkipCampaignEligibilitySource eligibilitySource,
        Action<string>? log = null,
        Action<string, string, int>? persistPending = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _eligibilitySource = eligibilitySource ?? throw new ArgumentNullException(nameof(eligibilitySource));
        _log = log;
        _persistPending = persistPending;
    }

    public bool HasPendingGrant(Guid characterId, string characterName) =>
        (characterId != Guid.Empty && _pendingById.ContainsKey(characterId)) ||
        (!string.IsNullOrWhiteSpace(characterName) && _pendingByName.ContainsKey(characterName.Trim()));

    /// <summary>
    /// Evaluates <c>GameEvents.CharacterCreated</c> options and league eligibility. When approved,
    /// records a pending one-time post-entry grant (both in memory and via <c>persistPending</c>) and returns
    /// <c>StartingAreaId</c>, <c>StartingLevel</c>, and <c>SuppressStartKit = true</c>.
    /// </summary>
    public SkipCampaignCharacterCreatedEffect OnCharacterCreated(SkipCampaignCharacterCreatedInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Settings.Enabled)
        {
            return new SkipCampaignCharacterCreatedEffect(Applied: false, DenialReason: "Skip Campaign is disabled.");
        }

        if (!SkipCampaignProtocol.IsOptInEnabled(input.CreationOptions))
        {
            return new SkipCampaignCharacterCreatedEffect(Applied: false);
        }

        if (Settings.RequireLeagueCompletion && !HasCompletedCampaignInLeague(input.Account, input.CharacterId, input.CharacterName, input.League))
        {
            string reason = $"Requires a living campaign-completed character (level {Settings.CompletionMinLevel}+ or in Karui Shores) in league '{input.League}'.";
            _log?.Invoke($"[skip-campaign] Denied for '{input.CharacterName}' ({input.Account}/{input.League}): {reason}");
            return new SkipCampaignCharacterCreatedEffect(Applied: false, DenialReason: reason);
        }

        string startingArea = ResolveStartingArea(Settings);
        int startingLevel = Math.Clamp(Settings.StartingLevel, 1, 100);

        var grant = new SkipCampaignPendingGrant(
            CharacterId: input.CharacterId,
            CharacterName: input.CharacterName,
            Account: input.Account,
            League: input.League,
            StartingAreaId: startingArea,
            StartingLevel: startingLevel,
            SettingsSnapshot: Settings,
            CreatedUtc: DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(input.CharacterName))
        {
            string trimmedName = input.CharacterName.Trim();
            _pendingByName[trimmedName] = grant;
            _persistPending?.Invoke(trimmedName, startingArea, startingLevel);
        }

        if (input.CharacterId != Guid.Empty)
        {
            _pendingById[input.CharacterId] = grant;
        }

        _log?.Invoke($"[skip-campaign] Approved for '{input.CharacterName}' ({input.Account}/{input.League}) -> {startingArea} Lv.{startingLevel} (SuppressStartKit=true).");
        return new SkipCampaignCharacterCreatedEffect(
            Applied: true,
            StartingAreaId: startingArea,
            StartingLevel: startingLevel,
            SuppressStartKit: true);
    }

    /// <summary>
    /// Applies one-time campaign progression on the character's first in-game arrival.
    /// Checks both in-memory queued grants and persisted <see cref="SkipCampaignProtocol.PendingCharacterDataKey"/>
    /// so grants survive a Gateway restart or mod reload between creation and first login.
    /// </summary>
    public async Task<SkipCampaignFirstEnterResult> OnFirstEnterAsync(
        ISkipCampaignPlayerWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);

        SkipCampaignPendingGrant? pending = ResolvePendingGrant(world);
        if (pending is null)
        {
            bool persisted = IsPersistedFinalized(world);
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: persisted);
        }

        return await ApplyGrantAsync(world, pending, raiseLevelAndTravel: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Vanilla-client fallback (<c>/skipcampaign apply</c>): checks settings and league eligibility, applies
    /// the exact same campaign skip progression, raises character level if below <c>StartingLevel</c>, and
    /// travels the player to <c>StartingAreaId</c>. Strictly one-time per character.
    /// </summary>
    public async Task<SkipCampaignFirstEnterResult> ApplyDirectAsync(
        ISkipCampaignPlayerWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!Settings.Enabled)
        {
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: false, DenialReason: "Skip Campaign is disabled in settings.");
        }

        if (IsPersistedFinalized(world))
        {
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: true, DenialReason: "Campaign skip has already been applied to this character.");
        }

        if (Settings.RequireLeagueCompletion &&
            !HasCompletedCampaignInLeague(world.Account, world.CharacterId, world.CharacterName, world.League))
        {
            string reason = $"Requires a living campaign-completed character (level {Settings.CompletionMinLevel}+ or in Karui Shores) in league '{world.League}'.";
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: false, DenialReason: reason);
        }

        string startingArea = ResolveStartingArea(Settings);
        int startingLevel = Math.Clamp(Settings.StartingLevel, 1, 100);
        var grant = new SkipCampaignPendingGrant(
            CharacterId: world.CharacterId,
            CharacterName: world.CharacterName,
            Account: world.Account,
            League: world.League,
            StartingAreaId: startingArea,
            StartingLevel: startingLevel,
            SettingsSnapshot: Settings,
            CreatedUtc: DateTimeOffset.UtcNow);

        return await ApplyGrantAsync(world, grant, raiseLevelAndTravel: true, cancellationToken).ConfigureAwait(false);
    }

    public static string EncodePendingMarker(string startingAreaId, int startingLevel) =>
        string.Create(CultureInfo.InvariantCulture, $"{startingAreaId}|{startingLevel}");

    public static bool TryDecodePendingMarker(string? raw, out string startingAreaId, out int startingLevel)
    {
        startingAreaId = SkipCampaignProtocol.DefaultStartingAreaId;
        startingLevel = SkipCampaignProtocol.DefaultStartingLevel;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        int sep = trimmed.IndexOf('|');
        if (sep < 0)
        {
            startingAreaId = trimmed;
            return true;
        }

        string area = trimmed[..sep].Trim();
        if (area.Length > 0)
        {
            startingAreaId = area;
        }

        if (int.TryParse(trimmed[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            startingLevel = Math.Clamp(parsed, 1, 100);
        }

        return true;
    }

    private async Task<SkipCampaignFirstEnterResult> ApplyGrantAsync(
        ISkipCampaignPlayerWorld world,
        SkipCampaignPendingGrant pending,
        bool raiseLevelAndTravel,
        CancellationToken cancellationToken)
    {
        string finalizeKey = BuildFinalizationKey(world.CharacterId, world.CharacterName);
        if (IsPersistedFinalized(world) || !_finalizedKeys.TryAdd(finalizeKey, 1))
        {
            RemovePendingGrant(pending.CharacterId, pending.CharacterName);
            world.RemoveCharacterData(SkipCampaignProtocol.PendingCharacterDataKey);
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: true);
        }

        try
        {
            SkipCampaignSettings cfg = pending.SettingsSnapshot;

            if (raiseLevelAndTravel && world.Level < pending.StartingLevel)
            {
                await world.SetLevelAsync(pending.StartingLevel, "skip-campaign", cancellationToken).ConfigureAwait(false);
            }

            List<string> questFlags = BuildQuestFlagList(cfg);
            if (questFlags.Count > 0)
            {
                await world.SetQuestFlagsAsync(questFlags, on: true, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<string> waypoints = cfg.CampaignWaypoints ?? SkipCampaignProtocol.DefaultCampaignWaypoints;
            int waypointsGranted = waypoints.Count > 0
                ? await world.GrantWaypointsAsync(waypoints, "skip-campaign", cancellationToken).ConfigureAwait(false)
                : 0;

            ushort desiredRefunds = Math.Max(world.RefundPoints, cfg.RefundPoints);
            ushort refundPoints = desiredRefunds != world.RefundPoints
                ? await world.SetRefundPointsAsync(desiredRefunds, cancellationToken).ConfigureAwait(false)
                : world.RefundPoints;

            int ascendancyPoints = 0;
            IReadOnlyList<string> awardKeys = cfg.AscendancyAwardKeys ?? SkipCampaignProtocol.DefaultAscendancyAwardKeys;
            if (cfg.GrantAscendancyTrials && awardKeys.Count > 0)
            {
                ascendancyPoints = await world.AwardAscendancyAsync(awardKeys, cancellationToken).ConfigureAwait(false);
            }

            uint totalGold = cfg.StartingGold > 0
                ? await world.AddGoldAsync(cfg.StartingGold, "skip-campaign", cancellationToken).ConfigureAwait(false)
                : world.Gold;

            world.ChangeCharacterData(SkipCampaignProtocol.FinalizedCharacterDataKey, _ => "1");
            world.RemoveCharacterData(SkipCampaignProtocol.PendingCharacterDataKey);
            RemovePendingGrant(pending.CharacterId, pending.CharacterName);

            var notice = new SkipCampaignAppliedNotice(
                CharacterName: world.CharacterName,
                League: string.IsNullOrWhiteSpace(world.League) ? pending.League : world.League,
                StartingAreaId: pending.StartingAreaId,
                StartingLevel: Math.Max(world.Level, pending.StartingLevel),
                WaypointsGranted: waypointsGranted,
                QuestFlagsApplied: questFlags.Count,
                RefundPoints: refundPoints,
                AscendancyPointsEarned: ascendancyPoints,
                GoldGranted: cfg.StartingGold,
                TotalGold: totalGold);

            _log?.Invoke($"[skip-campaign] Applied progression for '{world.CharacterName}': {questFlags.Count} flags, {waypointsGranted} waypoints, {refundPoints} refund pts, {ascendancyPoints} ascendancy pts, +{cfg.StartingGold} gold (total {totalGold}).");

            if (raiseLevelAndTravel)
            {
                await world.TravelToAsync(pending.StartingAreaId, "skip-campaign", cancellationToken).ConfigureAwait(false);
            }

            return new SkipCampaignFirstEnterResult(Applied: true, AlreadyFinalized: false, Notice: notice);
        }
        catch
        {
            _finalizedKeys.TryRemove(finalizeKey, out _);
            throw;
        }
    }

    private SkipCampaignPendingGrant? ResolvePendingGrant(ISkipCampaignPlayerWorld world)
    {
        if (TryPeekPendingGrant(world.CharacterId, world.CharacterName, out SkipCampaignPendingGrant? pending) &&
            pending is not null)
        {
            return pending;
        }

        string? persisted = world.GetCharacterData(SkipCampaignProtocol.PendingCharacterDataKey);
        if (TryDecodePendingMarker(persisted, out string areaId, out int level))
        {
            return new SkipCampaignPendingGrant(
                CharacterId: world.CharacterId,
                CharacterName: world.CharacterName,
                Account: world.Account,
                League: world.League,
                StartingAreaId: areaId,
                StartingLevel: level,
                SettingsSnapshot: Settings,
                CreatedUtc: DateTimeOffset.UtcNow);
        }

        return null;
    }

    private static List<string> BuildQuestFlagList(SkipCampaignSettings cfg)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var flags = new List<string>();

        void AddRange(IEnumerable<string>? source)
        {
            if (source is null) return;
            foreach (string flag in source)
            {
                if (!string.IsNullOrWhiteSpace(flag) && seen.Add(flag.Trim()))
                {
                    flags.Add(flag.Trim());
                }
            }
        }

        AddRange(cfg.KitavaPenaltyQuestFlags ?? SkipCampaignProtocol.DefaultKitavaPenaltyQuestFlags);
        if (cfg.GrantCampaignPassiveBooks)
        {
            AddRange(cfg.CampaignPassiveBookQuestFlags ?? SkipCampaignProtocol.DefaultCampaignPassiveBookQuestFlags);
        }

        return flags;
    }

    private bool HasCompletedCampaignInLeague(string account, Guid characterId, string characterName, string league)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(league))
        {
            return false;
        }

        IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> chars =
            _eligibilitySource.GetLeagueCharacters(account, league, includeDead: false);

        foreach (SkipCampaignLeagueCharacterSnapshot candidate in chars)
        {
            if ((characterId != Guid.Empty && candidate.Id == characterId) ||
                string.Equals(candidate.Name, characterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(candidate.League, league, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.HardcoreDead || candidate.PermanentlyDead)
            {
                continue;
            }

            if (IsCampaignCompleted(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCampaignCompleted(SkipCampaignLeagueCharacterSnapshot character)
    {
        int minLevel = Math.Max(1, Settings.CompletionMinLevel);
        if (character.Level >= minLevel)
        {
            return true;
        }

        return IsEndgameAreaId(character.LastTown) || IsEndgameAreaId(character.AreaId);
    }

    private bool IsEndgameAreaId(string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
        {
            return false;
        }

        string trimmed = areaId.Trim();
        return string.Equals(trimmed, SkipCampaignProtocol.DefaultStartingAreaId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, Settings.StartingAreaId, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryPeekPendingGrant(Guid characterId, string characterName, out SkipCampaignPendingGrant? grant)
    {
        if (characterId != Guid.Empty && _pendingById.TryGetValue(characterId, out grant))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(characterName) && _pendingByName.TryGetValue(characterName.Trim(), out grant))
        {
            return true;
        }

        grant = null;
        return false;
    }

    private void RemovePendingGrant(Guid characterId, string characterName)
    {
        if (characterId != Guid.Empty)
        {
            _pendingById.TryRemove(characterId, out _);
        }

        if (!string.IsNullOrWhiteSpace(characterName))
        {
            _pendingByName.TryRemove(characterName.Trim(), out _);
        }
    }

    private static string ResolveStartingArea(SkipCampaignSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StartingAreaId)
            ? SkipCampaignProtocol.DefaultStartingAreaId
            : settings.StartingAreaId.Trim();

    public static bool IsPersistedFinalized(ISkipCampaignPlayerWorld world) =>
        string.Equals(world.GetCharacterData(SkipCampaignProtocol.FinalizedCharacterDataKey), "1", StringComparison.Ordinal);

    private static string BuildFinalizationKey(Guid characterId, string characterName) =>
        characterId != Guid.Empty
            ? characterId.ToString("N")
            : characterName.Trim();
}
