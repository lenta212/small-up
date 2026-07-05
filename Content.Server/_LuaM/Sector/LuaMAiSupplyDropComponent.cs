namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiSupplyDropComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public int TradeCycle;

    [DataField]
    public string Resource = string.Empty;

    [DataField]
    public int Amount;

    [DataField]
    public string Vessel = string.Empty;

    [DataField]
    public string Role = string.Empty;

    [DataField]
    public string Actor = string.Empty;

    [DataField]
    public string Summary = string.Empty;
}
