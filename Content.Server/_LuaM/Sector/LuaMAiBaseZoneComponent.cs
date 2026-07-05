using Robust.Shared.Map;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiBaseZoneComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public string ZoneType = string.Empty;

    [DataField]
    public string Label = string.Empty;

    [DataField]
    public string ActiveWeaknessId = string.Empty;

    [DataField]
    public string ActiveWeaknessTitle = string.Empty;

    [DataField]
    public string ActiveCompensation = string.Empty;

    [DataField]
    public string SuggestedRole = string.Empty;

    [DataField]
    public string SuggestedResource = string.Empty;

    [DataField]
    public int ActiveCompensationSeverity;

    public MapCoordinates HomeCoordinates = MapCoordinates.Nullspace;
}
