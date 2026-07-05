namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiBaseAnchorComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public string CreatedBy = string.Empty;
}
