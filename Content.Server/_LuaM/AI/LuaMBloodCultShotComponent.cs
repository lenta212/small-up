namespace Content.Server._LuaM.AI;

/// <summary>
/// Runtime-only marker applied to a weapon for the duration of a blood cultist's shot.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMBloodCultShotComponent : Component
{
    public bool HitscanDamageBoostApplied;
}
