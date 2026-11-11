using GameServer.Modding;

public sealed partial class Forbiddenfreedom : IMod
{
    sealed record Choices(bool CrossClass, int MaximumPairs);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        JewelGrantPolicy.Use(mod.Manifest.Id, new(true, choices.CrossClass, choices.MaximumPairs));
        GameEvents.Command(mod.Manifest.Id, "forbidden_jewel", "/forbidden_jewel list | flame|flesh <notable name or graph id>", ForbiddenJewel);
    }
}
