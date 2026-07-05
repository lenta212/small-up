namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiShipCrewMarkerComponent : Component
{
    [DataField]
    public string Role = "any";

    [DataField]
    public string Label = string.Empty;

    [DataField]
    public int Priority;

    public EntityUid LastSpawnedDrone = EntityUid.Invalid;
}
