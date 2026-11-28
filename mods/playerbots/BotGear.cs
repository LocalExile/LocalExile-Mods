using GameServer.Core.Accounts;

namespace GameServer.Modules.PlayerBots;

/// <summary>Fallback base-item selection (used when GameLogic is absent) and gear descriptions for bot logs.</summary>
static class BotGear
{
    // The weapons a class is played with: (main hand classes, off hand class or null).
    static readonly Dictionary<CharacterClass, (string[] Main, string? Off)[]> Weapons = new()
    {
        [CharacterClass.Marauder] = [(["Two Hand Mace", "Two Hand Axe"], null), (["One Hand Mace", "One Hand Axe"], "Shield")],
        [CharacterClass.Ranger] = [(["Bow"], null)],
        [CharacterClass.Witch] = [(["Wand"], "Shield"), (["Staff"], null)],
        [CharacterClass.Duelist] = [(["One Hand Sword", "Thrusting One Hand Sword"], "Shield"), (["Two Hand Sword"], null)],
        [CharacterClass.Templar] = [(["Sceptre"], "Shield"), (["Staff"], null)],
        [CharacterClass.Shadow] = [(["Dagger"], null), (["Claw"], null)],
        [CharacterClass.Scion] = [(["One Hand Sword"], "Shield"), (["Two Hand Sword"], null)],
    };

    // The armour tag of a class's attributes (BaseItemTypes tags).
    static readonly Dictionary<CharacterClass, string> ArmourTag = new()
    {
        [CharacterClass.Marauder] = "str_armour", [CharacterClass.Ranger] = "dex_armour", [CharacterClass.Witch] = "int_armour",
        [CharacterClass.Duelist] = "str_dex_armour", [CharacterClass.Templar] = "str_int_armour", [CharacterClass.Shadow] = "dex_int_armour",
        [CharacterClass.Scion] = "str_dex_int_armour",
    };

    /// <summary>New gear for the bot at its level: base item Ids (BaseItemTypes), main hand first. A dual wielder holds the
    /// same weapon class in both hands.</summary>
    public static List<string> Pick(CharacterClass cls, int level, Random random)
    {
        var gear = new List<string>();
        var (main, off) = Weapons[cls][random.Next(Weapons[cls].Length)];
        if (Best(main, level, random) is { } weapon) gear.Add(weapon.Id);
        if (off is not null && Best([off], level, random, ArmourTag[cls]) is { } shield) gear.Add(shield.Id);
        else if (cls == CharacterClass.Shadow && Best(main, level, random) is { } second) gear.Add(second.Id);
        foreach (string piece in new[] { "Helmet", "Body Armour", "Gloves", "Boots" })
            if (Best([piece], level, random, ArmourTag[cls]) is { } armour) gear.Add(armour.Id);
        if (Best(["Belt"], level, random) is { } belt) gear.Add(belt.Id);
        return gear;
    }

    // One of the two highest bases of the classes a character of this level can wear (DropLevel), with the tag if given.
    static ItemCatalog.BaseType? Best(string[] classes, int level, Random random, string? tag = null)
    {
        var fitting = Program.Items.All
            .Where(t => classes.Contains(t.Class) && t.VisualIdentity != 0 && t.DropLevel <= Math.Max(1, level)
                && t.Id.StartsWith("Metadata/Items/", StringComparison.Ordinal) && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                && !t.Id.Contains("Royale", StringComparison.Ordinal) && !t.Id.Contains("Talisman", StringComparison.Ordinal)
                && (tag is null || t.Tags.Contains(tag) || !t.Tags.Any(g => g.EndsWith("_armour", StringComparison.Ordinal) || g.EndsWith("_shield", StringComparison.Ordinal))))
            .OrderByDescending(t => t.DropLevel).Take(2).ToList();
        return fitting.Count == 0 ? null : fitting[random.Next(fitting.Count)];
    }

    /// <summary>The gear's names for the log.</summary>
    public static string Describe(IReadOnlyList<string> gear) => string.Join(", ",
        gear.Select(id => Program.Items.All.FirstOrDefault(t => t.Id == id)?.Name).Where(n => n is not null));
}
