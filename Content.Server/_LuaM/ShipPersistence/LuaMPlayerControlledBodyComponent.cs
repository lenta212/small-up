using Robust.Shared.GameObjects;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Round-local evidence that an entity has been controlled by a player.
/// Actor and mind components disappear on detach, while this marker remains
/// until the body itself is deleted.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMPlayerControlledBodyComponent : Component
{
}
