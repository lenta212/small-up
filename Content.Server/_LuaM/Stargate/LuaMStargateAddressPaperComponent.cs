namespace Content.Server._LuaM.Stargate;

/// <summary>
/// Marks a paper that records the address of a real Stargate on its map.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMStargateAddressPaperComponent : Component
{
    [DataField]
    public List<byte> Address = new();
}
