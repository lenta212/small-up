using Robust.Shared.GameObjects;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Portable, relative salvage-expedition cooldown state stored on a persistent ship grid.
/// Component presence means a cooldown must be resumed when the vessel station is committed.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMSalvageExpeditionCooldownComponent : Component
{
    [DataField("remainingCooldown")]
    public TimeSpan RemainingCooldown;
}
