namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiDroneTraceComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public string DroneId = string.Empty;

    [DataField]
    public string DroneRole = string.Empty;

    [DataField]
    public string TraceKind = "social_contact";

    [DataField]
    public string TargetName = string.Empty;

    [DataField]
    public string WorkStation = string.Empty;

    [DataField]
    public string WorkEffect = string.Empty;

    [DataField]
    public bool CloseContact;

    [DataField]
    public int ContactCount;

    [DataField]
    public string ContactTone = string.Empty;

    [DataField]
    public string Summary = string.Empty;
}
