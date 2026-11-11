using GameServer.Modding;

public sealed partial class Equipmentfusion : IMod
{
    sealed record Choices(bool GlobalModifiers, bool LocalDamage, bool LocalDefences, bool LocalAccuracy);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        EquipmentAttachmentPolicy.Use(mod.Manifest.Id, new(choices.GlobalModifiers, choices.LocalDamage, choices.LocalDefences, choices.LocalAccuracy));
        GameEvents.Command(mod.Manifest.Id, "skin_transfer", "/skin_transfer list | info <id> | bind <id> | remove <id>", SkinTransfer);
    }
}
