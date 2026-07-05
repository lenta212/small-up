namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorRegistryTerminalSystem))]
public sealed partial class LuaMSectorRegistryTerminalComponent : Component
{
    [DataField]
    public string PaperPrototype = "Paper";
}
