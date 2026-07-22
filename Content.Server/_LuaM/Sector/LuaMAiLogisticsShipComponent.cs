using System;
using System.Collections.Generic;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiLogisticsShipComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    [DataField]
    public string Role = "hauler";

    [DataField]
    public string VesselId = string.Empty;

    [DataField]
    public string DisplayName = string.Empty;

    [DataField]
    public int Cycles;

    [DataField]
    public List<string> CrewRoleManifest = new();

    [DataField]
    public string CrewManifestSource = string.Empty;

    [DataField]
    public string CrewProfileId = string.Empty;

    [DataField]
    public string CrewProfileSummary = string.Empty;

    [DataField]
    public string BaseBehaviorMode = string.Empty;

    [DataField]
    public string BaseBehaviorFocusResource = string.Empty;

    [DataField]
    public string BaseBehaviorDirective = string.Empty;

    [DataField]
    public List<string> CrewStationPlan = new();

    [DataField]
    public string LastCrewReport = string.Empty;

    [DataField]
    public string LastCrewDutyReport = string.Empty;

    [DataField]
    public int CrewDutyCycles;

    [DataField]
    public Dictionary<string, int> CrewDutyRoleCycles = new();

    [DataField]
    public int CrewSpawnAttempts;

    [DataField]
    public int MarkerCrewSpawns;

    [DataField]
    public int GridCrewSpawns;

    [DataField]
    public TimeSpan NextCycle = TimeSpan.Zero;

    [DataField]
    public EntityUid? BehaviorCore;

    [DataField]
    public EntityUid? BehaviorDestination;

    [DataField]
    public EntityCoordinates? LastBehaviorDestination;

    [DataField]
    public bool RouteBlocked;

    [DataField]
    public string BehaviorState = "standby";

    [DataField]
    public string LastBehaviorStatus = "not evaluated";

    public TimeSpan NextBehaviorEvaluation;
}
