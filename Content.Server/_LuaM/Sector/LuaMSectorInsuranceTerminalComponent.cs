namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorInsuranceTerminalSystem))]
public sealed partial class LuaMSectorInsuranceTerminalComponent : Component
{
    [DataField]
    public string PaperPrototype = "Paper";
}
