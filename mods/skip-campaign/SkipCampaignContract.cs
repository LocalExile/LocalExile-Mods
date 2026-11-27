namespace LocalExile.Mods.SkipCampaign;

/// <summary>
/// Shared protocol constants, option keys, UI paths, and verified PoE 3.29 / LocalExile defaults
/// for the <c>skip-campaign</c> dual-side mod.
/// </summary>
public static class SkipCampaignProtocol
{
    /// <summary>Canonical lowercase dual-side mod identifier matching <c>mod.json</c>.</summary>
    public const string ModId = "skip-campaign";

    /// <summary>NativeMods channel name used for server-to-client arrival notices.</summary>
    public const string ChannelName = "skip-campaign";

    /// <summary>Message type name for <see cref="SkipCampaignAppliedNotice"/>.</summary>
    public const string AppliedNoticeMessageType = nameof(SkipCampaignAppliedNotice);

    /// <summary>Structured <c>CreateCharacter.S78</c> option key (<c>{BaseState}|nm:skip_campaign=1</c>).</summary>
    public const string OptionKey = "skip_campaign";

    /// <summary>Option value indicating the player opted in to skip the campaign.</summary>
    public const string OptionEnabledValue = "1";

    /// <summary>Display caption rendered on both <c>LeagueCardModes</c> and <c>CreateCharacterBar</c> checkboxes.</summary>
    public const string CheckboxCaption = "Skip Campaign";

    /// <summary>Virtual path for the <c>ScreenSlot.CreateCharacterBar</c> mirror checkbox layout.</summary>
    public const string CreateCharacterBarUiPath = "Metadata/UI/Mods/skip-campaign/CreateCharacterSkipBar.ui";

    /// <summary>Virtual path for the in-game first-arrival confirmation banner layout.</summary>
    public const string ArrivalBannerUiPath = "Metadata/UI/Mods/skip-campaign/SkipCampaignBanner.ui";

    /// <summary>Title label control identifier inside <see cref="ArrivalBannerUiPath"/>.</summary>
    public const string ArrivalBannerTitleId = "skip_campaign_banner_title";

    /// <summary>Body label control identifier inside <see cref="ArrivalBannerUiPath"/>.</summary>
    public const string ArrivalBannerBodyId = "skip_campaign_banner_body";

    /// <summary>Slot attachment identifier inside <c>ScreenSlot.CreateCharacterBar</c>.</summary>
    public const string CreateCharacterBarSlotId = "skip_campaign_bar";

    /// <summary>CheckBox control identifier inside <see cref="CreateCharacterBarUiPath"/>.</summary>
    public const string CreateCharacterCheckBoxId = "skip_campaign_checkbox";

    /// <summary>Status label control identifier inside <see cref="CreateCharacterBarUiPath"/>.</summary>
    public const string CreateCharacterStatusLabelId = "skip_campaign_status";

    /// <summary>Persisted character-data key recording a queued skip awaiting first in-game login.</summary>
    public const string PendingCharacterDataKey = "skip-campaign:pending";

    /// <summary>Persisted character-data key recording one-time skip-campaign finalization.</summary>
    public const string FinalizedCharacterDataKey = "skip-campaign:applied";

    /// <summary>Default starting town <c>WorldAreas</c> Id (Karui Shores).</summary>
    public const string DefaultStartingAreaId = "2_11_endgame_town";

    /// <summary>Default starting level for skipped characters.</summary>
    public const int DefaultStartingLevel = 68;

    /// <summary>Default minimum character level counted as league campaign completion.</summary>
    public const int DefaultCompletionMinLevel = 68;

    /// <summary>Default starting gold credited to the league stash on first login.</summary>
    public const long DefaultStartingGold = 50_000;

    /// <summary>Default minimum passive refund points granted on first login.</summary>
    public const ushort DefaultRefundPoints = 20;

    /// <summary>
    /// Core PoE 3.29 / LocalExile campaign completion quest flags applied on first login, including Act 5 and Act 10
    /// Kitava resistance penalties (<c>-60%</c> total), <c>EndGameAvailable</c>, Pantheon access/god unlocks,
    /// and tutorial suppression.
    /// </summary>
    public static IReadOnlyList<string> DefaultKitavaPenaltyQuestFlags { get; } =
    [
        "a5q6-CompletedBoss",
        "SinOnKitavaDefeat",
        "EndGameAvailable",
        "a10q3-UsedRewardBook",
        "CanChangePantheon",
        "a6q1-KilledBoss",
        "a7q1-KilledBoss",
        "a8q23-KilledBosses",
        "CompletedAllTutorials",
    ];

    /// <summary>
    /// Verified Acts 1–10 campaign Book of Skill quest flags from <c>QuestStaticRewards</c> (+23 passive points total).
    /// Bandit choice flags (<c>a2q1/a2q2/a2q3/a2q7</c>) are intentionally left unset so the player can still choose.
    /// </summary>
    public static IReadOnlyList<string> DefaultCampaignPassiveBookQuestFlags { get; } =
    [
        "a1q6-UsedRewardBook",
        "a1q7-UsedRewardBook",
        "a1q9-UsedRewardBook",
        "a2q5-UsedRewardBook",
        "a3q9-UsedRewardBook",
        "a3q11-UsedRewardBook",
        "a4q6-UsedRewardBook",
        "a5q3-UsedRewardBook",
        "a5q7-UsedRewardBook",
        "a6q3-UsedRewardBook",
        "a6q6-UsedRewardBook",
        "a6q7-UsedRewardBook",
        "a7q1-UsedRewardBook",
        "a7q6-UsedRewardBook",
        "a7q9-UsedRewardBook",
        "a8q4-UsedRewardBook",
        "a8q6-UsedRewardBook",
        "a8q7-UsedRewardBook",
        "a9q2-UsedRewardBook",
        "a9q5-UsedRewardBook",
        "a10q6-UsedRewardBook",
        "a10q3-UsedRewardBook",
    ];

    /// <summary>
    /// Default Act 1–10 towns, Karui Shores (<c>2_11_endgame_town</c>), and Aspirants' Plaza (<c>Labyrinth_Airlock</c>)
    /// waypoints granted on first login.
    /// </summary>
    public static IReadOnlyList<string> DefaultCampaignWaypoints { get; } =
    [
        "1_1_town",
        "1_2_town",
        "1_3_town",
        "1_4_town",
        "1_5_town",
        "2_6_town",
        "2_7_town",
        "2_8_town",
        "2_9_town",
        "2_10_town",
        "2_11_endgame_town",
        "Labyrinth_Airlock",
    ];

    /// <summary>
    /// Default campaign Labyrinth ascendancy tiers (<c>AscendancyRules.AwardKeys</c>) awarded when
    /// <see cref="SkipCampaignSettings.GrantAscendancyTrials"/> is enabled.
    /// </summary>
    public static IReadOnlyList<string> DefaultAscendancyAwardKeys { get; } =
    [
        "normal",
        "cruel",
        "merciless",
    ];

    /// <summary>Returns <c>true</c> when <paramref name="options"/> enables <see cref="OptionKey"/>.</summary>
    public static bool IsOptInEnabled(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null)
        {
            return false;
        }

        if (!options.TryGetValue(OptionKey, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        return string.Equals(trimmed, OptionEnabledValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Configurable server settings governing opt-in campaign skipping, league completion eligibility,
/// starting town/level, quest flags, campaign passive books, waypoints, refund points, ascendancy trials, and starting gold.
/// </summary>
public sealed record SkipCampaignSettings
{
    /// <summary>Master switch enabling or disabling campaign skip on character creation and <c>/skipcampaign apply</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, requires at least one living character on the same account in the exact same league
    /// (respecting SSF and Hardcore death isolation) at or above <see cref="CompletionMinLevel"/> or in endgame.
    /// </summary>
    public bool RequireLeagueCompletion { get; init; } = false;

    /// <summary>Target <c>WorldAreas</c> town Id assigned before first area entry (default <c>2_11_endgame_town</c>).</summary>
    public string StartingAreaId { get; init; } = SkipCampaignProtocol.DefaultStartingAreaId;

    /// <summary>Starting character level (<c>1..100</c>) assigned before <c>S-&gt;C 0x15 / 0x13</c> (default <c>68</c>).</summary>
    public int StartingLevel { get; init; } = SkipCampaignProtocol.DefaultStartingLevel;

    /// <summary>Whether to grant Acts 1–10 campaign Book of Skill quest flags (+23 passive points; default <c>true</c>).</summary>
    public bool GrantCampaignPassiveBooks { get; init; } = true;

    /// <summary>Whether to award Normal/Cruel/Merciless Labyrinth ascendancy trials on first arrival (default <c>true</c>).</summary>
    public bool GrantAscendancyTrials { get; init; } = true;

    /// <summary>Starting gold credited to the player's league stash on first arrival (default <c>50000</c>).</summary>
    public long StartingGold { get; init; } = SkipCampaignProtocol.DefaultStartingGold;

    /// <summary>Minimum passive refund points ensured on first arrival (default <c>20</c>).</summary>
    public ushort RefundPoints { get; init; } = SkipCampaignProtocol.DefaultRefundPoints;

    /// <summary>Minimum level of an existing living league character that satisfies <see cref="RequireLeagueCompletion"/>.</summary>
    public int CompletionMinLevel { get; init; } = SkipCampaignProtocol.DefaultCompletionMinLevel;

    /// <summary>Core campaign/Kitava penalty quest flags applied on first arrival.</summary>
    public IReadOnlyList<string> KitavaPenaltyQuestFlags { get; init; } = SkipCampaignProtocol.DefaultKitavaPenaltyQuestFlags;

    /// <summary>Acts 1–10 Book of Skill quest flags applied when <see cref="GrantCampaignPassiveBooks"/> is <c>true</c>.</summary>
    public IReadOnlyList<string> CampaignPassiveBookQuestFlags { get; init; } = SkipCampaignProtocol.DefaultCampaignPassiveBookQuestFlags;

    /// <summary>Act 1–10 towns, Karui Shores, and Aspirants' Plaza waypoints granted on first arrival.</summary>
    public IReadOnlyList<string> CampaignWaypoints { get; init; } = SkipCampaignProtocol.DefaultCampaignWaypoints;

    /// <summary>Labyrinth ascendancy award keys granted when <see cref="GrantAscendancyTrials"/> is <c>true</c>.</summary>
    public IReadOnlyList<string> AscendancyAwardKeys { get; init; } = SkipCampaignProtocol.DefaultAscendancyAwardKeys;

    /// <summary>Default out-of-the-box configuration.</summary>
    public static SkipCampaignSettings Default { get; } = new();
}

/// <summary>
/// Server-to-client notification sent via NativeMods <c>HostRuleset 0xA0</c> on first in-game arrival after
/// campaign skip progression has been applied, triggering a HUD toast via <c>IToastService</c>.
/// </summary>
public sealed record SkipCampaignAppliedNotice(
    string CharacterName,
    string League,
    string StartingAreaId,
    int StartingLevel,
    int WaypointsGranted,
    int QuestFlagsApplied,
    ushort RefundPoints,
    int AscendancyPointsEarned,
    long GoldGranted,
    uint TotalGold);
