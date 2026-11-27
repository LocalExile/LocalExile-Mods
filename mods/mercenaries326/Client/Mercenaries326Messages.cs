namespace Mercenaries326.Client;

/// <summary>
/// Server-to-client state sync message sent by the <c>mercenaries326</c> server mod over NativeMods
/// <c>HostRuleset</c> (<c>0xA0</c>, S→C <c>0x02C9</c>) on channel <c>"mercenaries326"</c>.
/// </summary>
/// <param name="Enabled">Whether the <c>mercenaries326</c> server mod is currently active on the connected server.</param>
/// <param name="AllowUniqueBodyArmour">Whether permanent mercenaries are permitted to equip Unique Body Armours.</param>
public sealed record Mercenaries326StateSync(bool Enabled, bool AllowUniqueBodyArmour);
