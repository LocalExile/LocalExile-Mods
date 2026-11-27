using System.Runtime.InteropServices;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Raw;

namespace Mercenaries326.Client;

/// <summary>
/// NativeMods 1.2.0 client plugin for the <c>mercenaries326</c> dual mod (Path of Exile 3.29.3c &amp; 3.29.3d).
/// <para>
/// Installs two byte-verified, ALC-safe <see cref="HookPhase.Pre"/> hook chains via <see cref="IRawMemory"/>:
/// <list type="number">
/// <item><description>
/// <c>GetPlayerAscendancyRow</c> (<c>RVA 0x242d3f0</c>, sole caller <c>EncounterMercenaryLoadout::UpdateRules</c>
/// at <c>0x1405af736</c>): presents the Scion+Luminary class byte (<c>0xC0</c>) when <c>mercenaries326</c> is active
/// so the native Encounter <c>Hire</c> button appears for every class and ascendancy.
/// </description></item>
/// <item><description>
/// <c>CanMercenaryEquipItem</c> (<c>RVA 0x2480d70</c>): remaps <c>slotId == 1</c> (<c>BodyArmour1</c>) to <c>0</c>
/// when <c>allowUniqueBodyArmour</c> is enabled, lifting only the hardcoded <c>BodyArmour1</c> unique block
/// (<c>0x142481017</c>) while preserving Identified, <c>MercenaryDisabledUniques.dat</c>, 70% level requirement,
/// armour attribute tags, and abyssal socket checks natively.
/// </description></item>
/// </list>
/// Stays completely dormant when connected to servers without <c>mercenaries326</c>, and restores all hooks and
/// unmanaged resources cleanly on disconnect or unload.
/// </para>
/// </summary>
public sealed class Mercenaries326ClientMod : IClientMod, IDisposable
{
    /// <summary>Wire message type name matched on channel <c>"mercenaries326"</c>.</summary>
    public const string StateSyncMessageType = "Mercenaries326StateSync";

    /// <summary>Optional Address Library symbol name for <c>GetPlayerAscendancyRow</c>.</summary>
    public const string GetPlayerAscendancyRowSymbol = "components.get_player_ascendancy_row";

    /// <summary>Verified shared 3.29.3c / 3.29.3d <c>.text</c> RVA of <c>GetPlayerAscendancyRow</c>.</summary>
    public const ulong GetPlayerAscendancyRowRva = 0x242D3F0UL;

    /// <summary>
    /// Verified 16-byte Win64 prologue of <c>GetPlayerAscendancyRow</c> (zero <c>.rdata</c> operands; identical on 3.29.3c &amp; 3.29.3d).
    /// </summary>
    public const string GetPlayerAscendancyRowExpectedBytes = "488bc448895810488970205557415648";

    /// <summary>Optional Address Library symbol name for <c>CanMercenaryEquipItem</c>.</summary>
    public const string CanMercenaryEquipItemSymbol = "items.can_mercenary_equip_item";

    /// <summary>Verified shared 3.29.3c / 3.29.3d <c>.text</c> RVA of <c>CanMercenaryEquipItem</c>.</summary>
    public const ulong CanMercenaryEquipItemRva = 0x2480D70UL;

    /// <summary>
    /// Verified 20-byte Win64 prologue of <c>CanMercenaryEquipItem</c> (zero <c>.rdata</c> operands; identical on 3.29.3c &amp; 3.29.3d).
    /// </summary>
    public const string CanMercenaryEquipItemExpectedBytes = "48895c2408488974241848897c24204889542410";

    /// <summary>
    /// Encoded 3.29 Player class byte for Scion (<c>Characters.IntegerId = 0</c>) + Luminary (<c>Ascendancy.ClassNo = 3</c>):
    /// <c>0 | ((3 &amp; 3) &lt;&lt; 6) = 0xC0</c>.
    /// </summary>
    public const byte ScionLuminaryClassByte = 0xC0;

    /// <summary><c>Inventories._row</c> for <c>BodyArmour1</c> (<c>r8d == 1</c> in <c>CanMercenaryEquipItem</c>).</summary>
    public const uint BodyArmourSlotId = 1u;

    /// <summary>1-based <c>Stats.dat</c> id for <c>can_have_active_mercenary_follower</c> (<c>0x5982</c> / <c>22914</c>).</summary>
    public const int StatCanHaveActiveMercenaryFollower = 0x5982;

    private IClientContext? _context;
    private IDisposable? _syncSub;
    private IDisposable? _ascendancyHook;
    private IDisposable? _canEquipHook;
    private nint _luminaryClassBytePtr;

    private volatile bool _hasServerSync;
    private volatile bool _serverEnabled;
    private volatile bool _allowUniqueBodyArmour;
    private volatile bool _unloading;

    /// <summary>Whether the server mod has signalled that <c>mercenaries326</c> is currently active.</summary>
    public bool IsServerEnabled => _serverEnabled;

    /// <summary>Whether Unique Body Armour equipping is currently unlocked for permanent mercenaries.</summary>
    public bool IsUniqueBodyArmourAllowed => _serverEnabled && _allowUniqueBodyArmour;

    /// <summary>Whether both native pre-hooks are currently armed.</summary>
    public bool HooksInstalled => _ascendancyHook is not null && _canEquipHook is not null;

    /// <inheritdoc/>
    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _unloading = false;
        _hasServerSync = false;
        _serverEnabled = false;
        _allowUniqueBodyArmour = false;

        if (!TryResolveTarget(
                context.Addresses,
                GetPlayerAscendancyRowSymbol,
                GetPlayerAscendancyRowRva,
                GetPlayerAscendancyRowExpectedBytes,
                out nint ascendancyAddr,
                out string ascendancyBytes) ||
            !TryResolveTarget(
                context.Addresses,
                CanMercenaryEquipItemSymbol,
                CanMercenaryEquipItemRva,
                CanMercenaryEquipItemExpectedBytes,
                out nint canEquipAddr,
                out string canEquipBytes))
        {
            context.Logger.Warn(
                $"Unsupported client build '{context.Addresses.BuildVersion}' ({context.Addresses.Build}); " +
                "mercenaries326 native hooks skipped.");
            return;
        }

        IRawMemory raw;
        try
        {
            raw = context.Raw;
        }
        catch (RawAccessDeniedException ex)
        {
            context.Logger.Error(
                "mercenaries326 requires nativeClient.rawAccess=true in mod.json to arm its two mercenary hooks.",
                ex);
            throw;
        }

        nint classByteBuf = Marshal.AllocHGlobal(1);
        Marshal.WriteByte(classByteBuf, ScionLuminaryClassByte);
        Volatile.Write(ref _luminaryClassBytePtr, classByteBuf);

        try
        {
            _syncSub = context.Net.On<Mercenaries326StateSync>(OnStateSync, StateSyncMessageType);
            context.Game.Lifecycle.Disconnected += OnDisconnected;

            _ascendancyHook = raw.InstallHookChain(
                ascendancyAddr,
                ascendancyBytes,
                HookPhase.Pre,
                OnGetPlayerAscendancyRowPre);

            _canEquipHook = raw.InstallHookChain(
                canEquipAddr,
                canEquipBytes,
                HookPhase.Pre,
                OnCanMercenaryEquipItemPre);

            context.Logger.Info(
                $"mercenaries326 client hooks armed on {context.Addresses.BuildVersion} " +
                $"(GetPlayerAscendancyRow=0x{(ulong)ascendancyAddr:X}, CanMercenaryEquipItem=0x{(ulong)canEquipAddr:X}); " +
                "dormant until server sync.");
        }
        catch (Exception ex)
        {
            context.Logger.Error("Failed to arm mercenaries326 native hooks; cleaning up.", ex);
            CleanupResources();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Unload() => CleanupResources();

    /// <inheritdoc/>
    public void Dispose() => CleanupResources();

    private void OnStateSync(Mercenaries326StateSync sync)
    {
        if (_unloading || sync is null)
        {
            return;
        }

        bool nextEnabled = sync.Enabled;
        bool nextUniqueBody = sync.Enabled && sync.AllowUniqueBodyArmour;
        bool changed = !_hasServerSync || nextEnabled != _serverEnabled || nextUniqueBody != _allowUniqueBodyArmour;

        _serverEnabled = nextEnabled;
        _allowUniqueBodyArmour = nextUniqueBody;
        _hasServerSync = true;

        if (changed)
        {
            _context?.Logger.Info(
                $"mercenaries326 state updated: enabled={nextEnabled}, allowUniqueBodyArmour={nextUniqueBody}.");
        }
    }

    private void OnDisconnected(string? reason)
    {
        bool wasActive = _serverEnabled || _allowUniqueBodyArmour;
        _hasServerSync = false;
        _serverEnabled = false;
        _allowUniqueBodyArmour = false;

        if (wasActive)
        {
            _context?.Logger.Info(
                $"mercenaries326 returned to dormant retail mode on disconnect ({reason ?? "none"}).");
        }
    }

    private void OnGetPlayerAscendancyRowPre(HookContext ctx)
    {
        if (_unloading)
        {
            return;
        }

        bool allowHireAll = _serverEnabled;
        if (!allowHireAll && !_hasServerSync && _context is { } context)
        {
            // Fallback for Hire button only: if the server granted can_have_active_mercenary_follower (0x5982)
            // before the HostRuleset sync arrived, unlock the Hire button. (In retail 3.29 only Luminary has
            // 0x5982, and Luminary already passes the stock check — so this stays 100% transparent on retail.)
            try
            {
                allowHireAll = context.Game.Stats.TryReadLocalPlayerStat(
                    StatCanHaveActiveMercenaryFollower,
                    out int canHaveFollower) && canHaveFollower > 0;
            }
            catch
            {
                allowHireAll = false;
            }
        }

        if (!allowHireAll)
        {
            return;
        }

        nint classBytePtr = Volatile.Read(ref _luminaryClassBytePtr);
        if (classBytePtr != nint.Zero)
        {
            ctx.Args[0] = unchecked((ulong)classBytePtr);
        }
    }

    private void OnCanMercenaryEquipItemPre(HookContext ctx)
    {
        if (_unloading || !_serverEnabled || !_allowUniqueBodyArmour)
        {
            return;
        }

        // Remap slotId 1 (BodyArmour1) -> 0 so the hardcoded BodyArmour1 unique block (0x142481017)
        // falls through to Step 3 while preserving Mods/Identified, MercenaryDisabledUniques.dat,
        // 70% level requirement, armour attribute tag matching, and abyssal socket checks natively.
        if (unchecked((uint)ctx.Args[2]) == BodyArmourSlotId)
        {
            ctx.Args[2] = 0UL;
        }
    }

    private void CleanupResources()
    {
        _unloading = true;
        _hasServerSync = false;
        _serverEnabled = false;
        _allowUniqueBodyArmour = false;

        IClientContext? context = Interlocked.Exchange(ref _context, null);
        if (context is not null)
        {
            try { context.Game.Lifecycle.Disconnected -= OnDisconnected; }
            catch { }
        }

        // Remove native hooks FIRST so no callback can ever read _luminaryClassBytePtr after it is freed.
        Interlocked.Exchange(ref _canEquipHook, null)?.Dispose();
        Interlocked.Exchange(ref _ascendancyHook, null)?.Dispose();
        Interlocked.Exchange(ref _syncSub, null)?.Dispose();

        nint classByteBuf = Interlocked.Exchange(ref _luminaryClassBytePtr, nint.Zero);
        if (classByteBuf != nint.Zero)
        {
            Marshal.FreeHGlobal(classByteBuf);
        }
    }

    private static bool TryResolveTarget(
        IAddressLibrary addresses,
        string symbolName,
        ulong fallbackRva,
        string fallbackExpectedBytes,
        out nint targetAddress,
        out string expectedBytes)
    {
        if (addresses.TryGetEntry(symbolName, out AddressEntry? entry) &&
            entry is { IsVerified: true } &&
            addresses.TryResolveAddress(symbolName, out nint resolved) &&
            resolved != nint.Zero)
        {
            targetAddress = resolved;
            expectedBytes = string.IsNullOrWhiteSpace(entry.ExpectedBytes)
                ? fallbackExpectedBytes
                : entry.ExpectedBytes;
            return true;
        }

        if (addresses.Build is ClientBuildId.Build3293d or ClientBuildId.Build3293c &&
            addresses.ImageBase != nint.Zero)
        {
            targetAddress = unchecked((nint)((ulong)addresses.ImageBase + fallbackRva));
            expectedBytes = fallbackExpectedBytes;
            return true;
        }

        targetAddress = nint.Zero;
        expectedBytes = string.Empty;
        return false;
    }
}
