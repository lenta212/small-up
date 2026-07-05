namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorLeadReportSystem))]
public sealed partial class LuaMSectorLeadReportComponent : Component
{
    [DataField]
    public string PaperPrototype = "Paper";
}
