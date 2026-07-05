using Robust.Shared.Map;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiDroneTaskComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public string TaskType = string.Empty;

    [DataField]
    public string ZoneType = string.Empty;

    [DataField]
    public string TaskStage = "assigned";

    public EntityUid TargetZone = EntityUid.Invalid;

    [DataField]
    public int ProgressTicks;

    [DataField]
    public int CompletedCycles;

    [DataField]
    public int BaseContributionCycles;

    [DataField]
    public bool IsStuck;

    [DataField]
    public int StuckChecks;

    [DataField]
    public string StuckReport = string.Empty;

    [DataField]
    public string LastReport = string.Empty;

    [DataField]
    public string LastContributionReport = string.Empty;

    [DataField]
    public string WeaknessId = string.Empty;

    [DataField]
    public string WeaknessTitle = string.Empty;

    [DataField]
    public string Compensation = string.Empty;

    [DataField]
    public string CompensationRole = string.Empty;

    [DataField]
    public string CompensationResource = string.Empty;

    [DataField]
    public int CompensationSeverity;

    public MapCoordinates LastTargetCoordinates = MapCoordinates.Nullspace;

    public MapCoordinates LastObservedCoordinates = MapCoordinates.Nullspace;

    public TimeSpan NextStuckCheck = TimeSpan.Zero;
}
