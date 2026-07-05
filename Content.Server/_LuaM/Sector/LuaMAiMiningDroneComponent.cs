using System;
using System.Collections.Generic;
using System.Numerics;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMAiMiningDroneComponent : Component
{
    [DataField]
    public string BaseId = "LuaM-AI-Base";

    public EntityUid ParentShip = EntityUid.Invalid;

    [DataField]
    public string VesselId = string.Empty;

    [DataField]
    public string DisplayName = string.Empty;

    [DataField]
    public string DroneId = string.Empty;

    [DataField]
    public string State = "idle";

    [DataField]
    public string DroneRole = "miner";

    [DataField]
    public string CrewAssignment = string.Empty;

    [DataField]
    public string CrewStation = string.Empty;

    [DataField]
    public string CrewDirective = string.Empty;

    [DataField]
    public int CrewPriority;

    [DataField]
    public bool HasCrewHome;

    [DataField]
    public Vector2 CrewHomeLocalPosition = Vector2.Zero;

    [DataField]
    public string LastCrewHomeAction = string.Empty;

    [DataField]
    public TimeSpan NextCrewDuty = TimeSpan.Zero;

    [DataField]
    public int CrewDutyCycles;

    [DataField]
    public string LastCrewDutyReport = string.Empty;

    [DataField]
    public string LastCrewDutyEffect = string.Empty;

    [DataField]
    public int OreCycles;

    [DataField]
    public int LastOreAmount;

    [DataField]
    public TimeSpan NextMove = TimeSpan.Zero;

    [DataField]
    public TimeSpan NextMine = TimeSpan.Zero;

    [DataField]
    public float SocialScanRadius = 6f;

    [DataField]
    public TimeSpan NextSocialScan = TimeSpan.Zero;

    [DataField]
    public TimeSpan NextSpeech = TimeSpan.Zero;

    [DataField]
    public TimeSpan NextWorldTrace = TimeSpan.Zero;

    public EntityUid LastSeenPerson = EntityUid.Invalid;

    [DataField]
    public string LastSeenName = string.Empty;

    [DataField]
    public string LastLine = string.Empty;

    [DataField]
    public string LastSocialAction = string.Empty;

    [DataField]
    public string LastLightMode = string.Empty;

    [DataField]
    public string LastContactKey = string.Empty;

    [DataField]
    public int LastContactCount;

    [DataField]
    public string LastContactTone = string.Empty;

    [DataField]
    public int UniquePeopleSeen;

    [DataField]
    public Dictionary<string, int> ContactMemory = new();

    public EntityUid LastWorldTraceUid = EntityUid.Invalid;

    public EntityUid LastCrewDutyTraceUid = EntityUid.Invalid;

    [DataField]
    public string LastWorldTraceSummary = string.Empty;

    [DataField]
    public int SocialScans;

    [DataField]
    public int PeopleSeen;

    [DataField]
    public int SocialPings;

    [DataField]
    public int WorldTraces;
}
