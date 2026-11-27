using System.Globalization;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Mods.SkipCampaign.Client;

/// <summary>
/// Client half of <c>skip-campaign</c> targeting <c>LocalExile.Client.Sdk</c> (<c>&gt;=0.3.0 &lt;2.0.0</c>).
/// Injects an opt-in <c>Skip Campaign</c> mode checkbox into every <c>0xdc35</c> league card via
/// <see cref="ScreenSlot.LeagueCardModes"/>, keeps a synchronized mirror checkbox in
/// <see cref="ScreenSlot.CreateCharacterBar"/>, tags outgoing <c>C-&gt;S 0x12</c> (<c>CreateCharacter.S78</c>)
/// via <see cref="IPreGameService.CharacterCreating"/>, resets cleanly on creation success while
/// preserving state on failure, and displays a HUD toast via <see cref="IToastService"/> on first arrival.
/// </summary>
public sealed class SkipCampaignClientMod : IClientMod
{
    private const string EmbeddedCreateCharacterBarUi = """
        // Skip Campaign — CreateCharacterBar mirror toggle fallback.

        <DEFINITIONS>

        begin Frame
        	id = skip_campaign_bar;
        	size = 560, 58;
        	visible = true;
        	mouse_enabled = true;
        	propagate_properties = false;
        	border = normal,
        		"Art/2DArt/UIImages/Common/OrnateTopLeft", "Art/2DArt/UIImages/Common/OrnateTop", "Art/2DArt/UIImages/Common/OrnateTopRight",
        		"Art/2DArt/UIImages/Common/OrnateLeft", "Art/2DArt/UIImages/Common/OrnateRight",
        		"Art/2DArt/UIImages/Common/OrnateBottomLeft", "Art/2DArt/UIImages/Common/OrnateBottom", "Art/2DArt/UIImages/Common/OrnateBottomRight";
        	border_position = outside;
        	background = "Art/2DArt/UIImages/Common/Background1", tile;

        	begin CheckBox
        		id = skip_campaign_checkbox;
        		location = 16, 8;
        		size = 300, 42;
        		font = Fonts.NormalSC;
        		foreground_colour = Colours.LabelTextColour;
        		text_side = right;
        		text = "Skip Campaign";
        		visible = true;
        	end

        	begin Label
        		id = skip_campaign_status;
        		location = 330, 14;
        		size = 216, 32;
        		auto_size = false;
        		font = Fonts.Normal;
        		foreground_colour = rgb( 185, 178, 162 );
        		text = "Normal Start";
        	end
        end
        """;

    private const string EmbeddedArrivalBannerUi = """
        // Skip Campaign — In-game arrival banner fallback.

        <DEFINITIONS>

        begin Frame
        	id = skip_campaign_banner;
        	aspect_mode = scaley;
        	size = 820, 112;
        	float = centre;
        	y = 84;
        	visible = true;
        	mouse_enabled = false;
        	propagate_properties = false;
        	border = normal,
        		"Art/2DArt/UIImages/Common/OrnateTopLeft", "Art/2DArt/UIImages/Common/OrnateTop", "Art/2DArt/UIImages/Common/OrnateTopRight",
        		"Art/2DArt/UIImages/Common/OrnateLeft", "Art/2DArt/UIImages/Common/OrnateRight",
        		"Art/2DArt/UIImages/Common/OrnateBottomLeft", "Art/2DArt/UIImages/Common/OrnateBottom", "Art/2DArt/UIImages/Common/OrnateBottomRight";
        	border_position = outside;
        	background = "Art/2DArt/UIImages/Common/Background1", tile;

        	begin Label
        		id = skip_campaign_banner_title;
        		location = 24, 14;
        		size = 772, 34;
        		auto_size = false;
        		font = Fonts.NormalSC;
        		foreground_colour = rgb( 164, 221, 124 );
        		text = "Campaign Skipped";
        	end

        	begin Label
        		id = skip_campaign_banner_body;
        		location = 24, 54;
        		size = 772, 44;
        		auto_size = false;
        		font = Fonts.Normal;
        		foreground_colour = Colours.White;
        		text = "Endgame progression applied.";
        	end
        end
        """;

    private const float ArrivalBannerDurationSeconds = 7.0f;

    private readonly Dictionary<string, bool> _optInByLeague = new(StringComparer.OrdinalIgnoreCase);

    private IClientContext? _context;
    private ILeagueModeRegistration? _leagueModeReg;
    private ISlotAttachment? _barAttachment;
    private IUiControl? _boundBarControl;
    private ICheckBox? _mirrorCheckBox;
    private ILabel? _mirrorStatusLabel;
    private IUiWindow? _arrivalBannerWindow;
    private float _arrivalBannerRemainingSeconds;
    private string? _activeLeague;
    private bool _globalOptIn;
    private bool _hasPendingMirrorBroadcast;
    private bool _syncingUi;
    private bool _unloading;

    public ILeagueModeRegistration? LeagueModeRegistration => _leagueModeReg;
    public ISlotAttachment? CreateCharacterBarAttachment => _barAttachment;
    public string? ActiveLeague => _activeLeague;
    public SkipCampaignAppliedNotice? LastAppliedNotice { get; private set; }
    public ulong? LastToastId { get; private set; }
    public int PreservedOnFailureCount { get; private set; }

    public bool IsOptInEnabled(string? leagueId = null)
    {
        if (!string.IsNullOrWhiteSpace(leagueId))
        {
            string trimmed = leagueId.Trim();
            if (_optInByLeague.TryGetValue(trimmed, out bool perLeague))
            {
                return perLeague;
            }

            string stripped = StripModePrefixes(trimmed);
            if (!string.Equals(stripped, trimmed, StringComparison.OrdinalIgnoreCase) &&
                _optInByLeague.TryGetValue(stripped, out bool baseLeague))
            {
                return baseLeague;
            }

            if (_optInByLeague.Count > 0 && _optInByLeague.Keys.Any(k => !IsSyntheticLeagueId(k)))
            {
                return false;
            }
        }

        return _globalOptIn;
    }

    private static bool IsSyntheticLeagueId(string key) =>
        key.StartsWith("league_", StringComparison.OrdinalIgnoreCase) &&
        key.Length > 7 &&
        int.TryParse(key.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _unloading = false;
        _context = context;
        IGameServices game = context.Game;

        if (!game.Files.Exists(SkipCampaignProtocol.CreateCharacterBarUiPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                SkipCampaignProtocol.CreateCharacterBarUiPath,
                EmbeddedCreateCharacterBarUi,
                priority: 50));
        }

        if (!game.Files.Exists(SkipCampaignProtocol.ArrivalBannerUiPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                SkipCampaignProtocol.ArrivalBannerUiPath,
                EmbeddedArrivalBannerUi,
                priority: 50));
        }

        _leagueModeReg = game.Screens.RegisterLeagueCardMode(
            new LeagueModeCheckboxOptions(
                Id: SkipCampaignProtocol.OptionKey,
                Caption: SkipCampaignProtocol.CheckboxCaption,
                DefaultChecked: false,
                Order: 10,
                Tooltip: "Start directly in Karui Shores at endgame level with campaign quest flags and waypoints applied.",
                NaturalWidth: 230f),
            OnLeagueCardModeChanged);
        context.TrackDisposable(_leagueModeReg);

        _barAttachment = game.Screens.RegisterSlot(
            ScreenSlot.CreateCharacterBar,
            SkipCampaignProtocol.CreateCharacterBarSlotId,
            SkipCampaignProtocol.CreateCharacterBarUiPath,
            new SlotAttachmentOptions(
                Order: 10,
                Width: 560f,
                Height: 58f,
                Spacing: 8f,
                Tooltip: "Skip Campaign (Acts 1-10)"));
        context.TrackDisposable(_barAttachment);

        game.Screens.PreGameScreenChanged += OnPreGameScreenChanged;
        game.Screens.LeagueOverlayFilled += OnLeagueOverlayFilled;
        game.PreGame.CharacterCreating += OnCharacterCreating;
        game.PreGame.CharacterCreated += OnCharacterCreated;
        game.Lifecycle.Frame += OnFrame;
        game.Lifecycle.Disconnected += OnDisconnected;

        context.TrackDisposable(context.Net.On<SkipCampaignAppliedNotice>(
            notice => DispatchOnGameThread(() => HandleAppliedNotice(notice))));

        BindAndSyncMirrorControl();
    }

    public void Unload()
    {
        _unloading = true;

        if (_context is not null)
        {
            IGameServices game = _context.Game;
            game.Screens.PreGameScreenChanged -= OnPreGameScreenChanged;
            game.Screens.LeagueOverlayFilled -= OnLeagueOverlayFilled;
            game.PreGame.CharacterCreating -= OnCharacterCreating;
            game.PreGame.CharacterCreated -= OnCharacterCreated;
            game.Lifecycle.Frame -= OnFrame;
            game.Lifecycle.Disconnected -= OnDisconnected;
        }

        UnbindMirrorControl();
        CloseArrivalBanner();

        if (_leagueModeReg is not null)
        {
            _leagueModeReg.Dispose();
            _leagueModeReg = null;
        }

        if (_barAttachment is not null)
        {
            _barAttachment.Dispose();
            _barAttachment = null;
        }

        _optInByLeague.Clear();
        _activeLeague = null;
        _globalOptIn = false;
        _hasPendingMirrorBroadcast = false;
        _syncingUi = false;
        _context = null;
    }

    private void OnLeagueCardModeChanged(string leagueId, bool isChecked)
    {
        if (_unloading || _syncingUi || string.IsNullOrWhiteSpace(leagueId))
        {
            return;
        }

        string normalized = leagueId.Trim();
        _optInByLeague[normalized] = isChecked;
        _activeLeague = normalized;
        _globalOptIn = isChecked;
        _hasPendingMirrorBroadcast = false;

        SyncMirrorControlState();
    }

    private void OnLeagueOverlayFilled(LeagueOverlayFilledEventArgs args)
    {
        _context?.Logger.Info($"LeagueOverlayFilled: cards={args.Cards.Count} [{string.Join(", ", args.Cards.Select(c => $"{c.LeagueId}(stack=0x{c.ModeStackHandle:X},stock={c.StockCheckboxCount})"))}]");
        if (_unloading || args.Cards.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_activeLeague) ||
            !args.Cards.Any(c => string.Equals(c.LeagueId, _activeLeague, StringComparison.OrdinalIgnoreCase)))
        {
            _activeLeague = args.Cards[0].LeagueId;
        }

        bool broadcastMirror = _hasPendingMirrorBroadcast;
        _hasPendingMirrorBroadcast = false;
        _syncingUi = true;
        try
        {
            foreach (LeagueCardInfo card in args.Cards)
            {
                if (!_optInByLeague.ContainsKey(card.LeagueId))
                {
                    bool initial = broadcastMirror ? _globalOptIn : false;
                    _optInByLeague[card.LeagueId] = initial;
                    if (broadcastMirror && _leagueModeReg is not null)
                    {
                        _leagueModeReg.SetChecked(card.LeagueId, initial);
                    }
                }
            }
        }
        finally
        {
            _syncingUi = false;
        }

        SyncMirrorControlState();
    }

    private void OnPreGameScreenChanged(PreGameScreenChangedEventArgs args)
    {
        if (_unloading)
        {
            return;
        }

        if (args.Current == PreGameScreen.CreateCharacter)
        {
            BindAndSyncMirrorControl();
        }
        else if (args.Previous == PreGameScreen.CreateCharacter)
        {
            UnbindMirrorControl();
        }
    }

    private void OnFrame(float dt)
    {
        if (_unloading)
        {
            return;
        }

        if (_arrivalBannerWindow is not null)
        {
            _arrivalBannerRemainingSeconds -= Math.Max(0f, dt);
            if (_arrivalBannerRemainingSeconds <= 0f)
            {
                CloseArrivalBanner();
            }
        }

        IUiControl? currentControl = _barAttachment is { IsAttached: true } ? _barAttachment.Control : null;
        if (!ReferenceEquals(currentControl, _boundBarControl))
        {
            BindAndSyncMirrorControl();
        }
    }

    private void OnCharacterCreating(CharacterCreateContext ctx)
    {
        if (_unloading || ctx is null)
        {
            return;
        }

        IUiControl? currentControl = _barAttachment is { IsAttached: true } ? _barAttachment.Control : null;
        if (!ReferenceEquals(currentControl, _boundBarControl))
        {
            BindAndSyncMirrorControl();
        }

        if (IsOptInEnabled(ctx.League))
        {
            ctx.SetOption(SkipCampaignProtocol.OptionKey, SkipCampaignProtocol.OptionEnabledValue);
            _context?.Logger.Info($"CharacterCreating: attached skip_campaign=1 for '{ctx.Name}' in '{ctx.League}'.");
        }
        else
        {
            _context?.Logger.Info($"CharacterCreating: normal start for '{ctx.Name}' in '{ctx.League}' (activeLeague='{_activeLeague}', global={_globalOptIn}).");
        }
    }

    private void OnCharacterCreated(CharacterCreatedEventArgs args)
    {
        if (_unloading || args is null)
        {
            return;
        }

        if (!args.Succeeded)
        {
            PreservedOnFailureCount++;
            return;
        }

        ResetAllToggles();
    }

    private void OnDisconnected(string? _) => DispatchOnGameThread(() =>
    {
        if (_unloading)
        {
            return;
        }

        UnbindMirrorControl();
        CloseArrivalBanner();
    });

    private void HandleAppliedNotice(SkipCampaignAppliedNotice notice)
    {
        if (_unloading || _context is null || notice is null)
        {
            return;
        }

        LastAppliedNotice = notice;
        string townName = FormatAreaDisplayName(notice.StartingAreaId);
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Lv.{notice.StartingLevel} in {townName} — {notice.WaypointsGranted} waypoints, {notice.RefundPoints} refund pts, {notice.AscendancyPointsEarned} ascendancy pts, {notice.GoldGranted:N0} gold.");

        CloseArrivalBanner();
        try
        {
            IUiWindow banner = _context.Game.Ui.Load(SkipCampaignProtocol.ArrivalBannerUiPath, "skip_campaign_banner");
            if (banner.Find<ILabel>(SkipCampaignProtocol.ArrivalBannerBodyId) is { } bodyLabel)
            {
                bodyLabel.Text = message;
            }
            banner.Visible = true;
            _arrivalBannerWindow = banner;
            _arrivalBannerRemainingSeconds = ArrivalBannerDurationSeconds;
            LastToastId = (LastToastId ?? 0UL) + 1UL;
        }
        catch (Exception ex)
        {
            _context.Logger.Warn($"Could not display arrival banner ({ex.Message}); summary remains in chat.");
        }
    }

    private void CloseArrivalBanner()
    {
        _arrivalBannerRemainingSeconds = 0f;
        if (_arrivalBannerWindow is not null)
        {
            try
            {
                _arrivalBannerWindow.Close();
            }
            catch
            {
            }
            _arrivalBannerWindow = null;
        }
    }

    private void BindAndSyncMirrorControl()
    {
        IUiControl? control = _barAttachment is { IsAttached: true } ? _barAttachment.Control : null;
        if (ReferenceEquals(control, _boundBarControl))
        {
            SyncMirrorControlState();
            return;
        }

        UnbindMirrorControl();
        if (control is null)
        {
            return;
        }

        _boundBarControl = control;
        // Position neatly above the CreateCharacter bottom bar so it never obscures the prompt or character name field.
        control.SetPosition(12f, -146f);
        control.SetSize(560f, 58f);
        _mirrorCheckBox = control.Find<ICheckBox>(SkipCampaignProtocol.CreateCharacterCheckBoxId);
        _mirrorStatusLabel = control.Find<ILabel>(SkipCampaignProtocol.CreateCharacterStatusLabelId);

        if (_mirrorCheckBox is not null)
        {
            _mirrorCheckBox.CheckedChanged += OnMirrorCheckBoxChanged;
        }

        SyncMirrorControlState();
    }

    private void UnbindMirrorControl()
    {
        if (_mirrorCheckBox is not null)
        {
            _mirrorCheckBox.CheckedChanged -= OnMirrorCheckBoxChanged;
            _mirrorCheckBox = null;
        }

        _mirrorStatusLabel = null;
        _boundBarControl = null;
    }

    private void OnMirrorCheckBoxChanged(bool isChecked)
    {
        if (_unloading || _syncingUi)
        {
            return;
        }

        _globalOptIn = isChecked;
        if (!string.IsNullOrWhiteSpace(_activeLeague))
        {
            _optInByLeague[_activeLeague] = isChecked;
            if (_leagueModeReg is not null)
            {
                _syncingUi = true;
                try
                {
                    _leagueModeReg.SetChecked(_activeLeague, isChecked);
                }
                finally
                {
                    _syncingUi = false;
                }
            }
        }
        else
        {
            _hasPendingMirrorBroadcast = true;
        }

        UpdateMirrorStatusLabel(isChecked);
    }

    private void SyncMirrorControlState()
    {
        bool checkedState = IsOptInEnabled(_activeLeague);
        if (_mirrorCheckBox is not null)
        {
            _syncingUi = true;
            try
            {
                if (_mirrorCheckBox.IsChecked != checkedState)
                {
                    _mirrorCheckBox.IsChecked = checkedState;
                }
            }
            finally
            {
                _syncingUi = false;
            }
        }

        UpdateMirrorStatusLabel(checkedState);
    }

    private void UpdateMirrorStatusLabel(bool enabled)
    {
        if (_mirrorStatusLabel is not null)
        {
            _mirrorStatusLabel.Text = enabled ? "Endgame Start" : "Normal Start";
        }
    }

    private void ResetAllToggles()
    {
        _globalOptIn = false;
        _hasPendingMirrorBroadcast = false;

        List<string> leaguesToClear = [.. _optInByLeague.Keys];
        if (!string.IsNullOrWhiteSpace(_activeLeague) &&
            !leaguesToClear.Contains(_activeLeague, StringComparer.OrdinalIgnoreCase))
        {
            leaguesToClear.Add(_activeLeague);
        }

        _syncingUi = true;
        try
        {
            foreach (string league in leaguesToClear)
            {
                _optInByLeague[league] = false;
                _leagueModeReg?.SetChecked(league, false);
            }
        }
        finally
        {
            _syncingUi = false;
        }

        SyncMirrorControlState();
    }

    private void DispatchOnGameThread(Action action)
    {
        IClientContext? context = _context;
        if (_unloading || context is null)
        {
            return;
        }

        if (context.Scheduler.IsOnGameThread)
        {
            action();
            return;
        }

        context.Scheduler.RunOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            action();
        });
    }

    private static string FormatAreaDisplayName(string areaId) =>
        string.Equals(areaId, SkipCampaignProtocol.DefaultStartingAreaId, StringComparison.OrdinalIgnoreCase)
            ? "Karui Shores"
            : areaId;

    private static string StripModePrefixes(string league)
    {
        string current = league;
        bool stripped;
        do
        {
            stripped = false;
            foreach (string prefix in new[] { "SSF ", "Hardcore ", "HC ", "Ruthless " })
            {
                if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    current = current[prefix.Length..].Trim();
                    stripped = true;
                }
            }
        }
        while (stripped);

        return current;
    }
}
