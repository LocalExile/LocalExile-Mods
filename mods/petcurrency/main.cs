using GameServer.Modding;

public sealed class Petcurrency : IMod
{
    sealed record Choices(int Radius, int MaximumItemsPerTick, bool RequirePet);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        CurrencyPickupPolicy.Use(mod.Manifest.Id, new(choices.Radius, choices.MaximumItemsPerTick, choices.RequirePet));
    }
}
