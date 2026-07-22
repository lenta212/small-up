namespace Content.Server._LuaM.Radar;

/// <summary>
/// Marks a brain that should receive a radar signature while it is outside every container.
/// This component intentionally contains no runtime entity references so it is safe to persist
/// as part of a deep-cryo snapshot.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMFreeBrainRadarComponent : Component
{
}

/// <summary>
/// Records that <see cref="LuaMFreeBrainRadarSystem"/> added the entity's current radar blip.
/// Component presence, rather than an entity reference, preserves ownership across serialization
/// and prevents the system from removing radar blips supplied by another mechanic.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMFreeBrainRadarOwnedComponent : Component
{
}
