namespace Content.Shared._LuaM.Stargate;

/// <summary>
/// Two-disk workstation used to create, copy, move, and erase Stargate addresses.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMStargateAddressEditorComponent : Component
{
    [DataField]
    public int AddressLength = 6;

    [DataField]
    public byte SymbolCount = LuaMStargateGlyphs.Count;

    [ViewVariables]
    public readonly List<byte> CurrentInput = new();
}
