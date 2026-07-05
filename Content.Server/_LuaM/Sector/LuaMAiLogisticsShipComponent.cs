using System;
using System.Collections.Generic;

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
}
