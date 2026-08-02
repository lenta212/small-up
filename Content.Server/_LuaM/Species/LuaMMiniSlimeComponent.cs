namespace Content.Server._LuaM.Species;

[RegisterComponent]
public sealed partial class LuaMMiniSlimeComponent : Component
{
    [DataField]
    public float GrowthHunger = 100f;

    [ViewVariables]
    public TimeSpan NextCheck;
}
