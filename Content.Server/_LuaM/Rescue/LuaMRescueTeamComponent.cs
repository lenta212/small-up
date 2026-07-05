using System;
using System.Collections.Generic;

namespace Content.Server._LuaM.Rescue;

[RegisterComponent]
public sealed partial class LuaMRescueTeamComponent : Component
{
    [DataField]
    public int TeamId;

    [DataField]
    public EntityUid? Leader;

    [DataField]
    public EntityUid? Patient;

    [DataField]
    public EntityUid? Shuttle;

    [DataField]
    public EntityUid? ShuttleAnchor;

    [DataField]
    public LuaMRescueTeamPhase Phase = LuaMRescueTeamPhase.Idle;

    [DataField]
    public string LastStatus = "none";

    [DataField]
    public EntityUid? SceneAnchor;

    [DataField]
    public EntityUid? ThreatTarget;

    [DataField]
    public int NearbyHostiles;

    [DataField]
    public int NearbyCombatants;

    [DataField]
    public int NearbyCrowd;

    [DataField]
    public int NearbyBlockers;

    [DataField]
    public string LastSceneStatus = "scene clear";

    [DataField]
    public string LastMemoryDigest = "memory clear";

    [DataField]
    public int RecentThreatMemories;

    [DataField]
    public int RecentCrowdMemories;

    [DataField]
    public int RecentRouteMemories;

    [DataField]
    public float SceneScanInterval = 1f;

    public float SceneScanAccumulator;

    public readonly List<EntityUid> Escorts = new();

    public readonly List<LuaMRescueSceneMemoryEntry> SceneMemory = new();
}

[RegisterComponent]
public sealed partial class LuaMRescueEscortComponent : Component
{
    [DataField]
    public int TeamId;

    [DataField]
    public LuaMRescueEscortRole Role = LuaMRescueEscortRole.Tourniquet;

    [DataField]
    public EntityUid? Leader;

    [DataField]
    public EntityUid? Patient;

    [DataField]
    public EntityUid? Shuttle;

    [DataField]
    public EntityUid? ShuttleAnchor;

    [DataField]
    public EntityUid? CurrentFollowTarget;

    [DataField]
    public LuaMRescueEscortDuty CurrentDuty = LuaMRescueEscortDuty.Standby;

    [DataField]
    public string LastDutyStatus = "none";

    [DataField]
    public EntityUid? SceneAnchor;

    [DataField]
    public EntityUid? ThreatTarget;

    [DataField]
    public int NearbyHostiles;

    [DataField]
    public int NearbyCombatants;

    [DataField]
    public int NearbyCrowd;

    [DataField]
    public int NearbyBlockers;

    [DataField]
    public string LastSceneStatus = "scene clear";

    [DataField]
    public string LastMemoryDigest = "memory clear";

    [DataField]
    public int RecentThreatMemories;

    [DataField]
    public int RecentCrowdMemories;

    [DataField]
    public int RecentRouteMemories;

    [DataField]
    public float FollowCloseRange = 1.75f;

    [DataField]
    public float FollowRange = 5f;

    [DataField]
    public float DutyRefreshInterval = 1f;

    public float DutyRefreshAccumulator;

    public TimeSpan NextSpeechTime;
}

public enum LuaMRescueEscortRole : byte
{
    Tourniquet,
    Kostyl,
    Zaslon,
}

public enum LuaMRescueEscortDuty : byte
{
    Standby,
    SecureScene,
    PatientSupport,
    EvacuationCorridor,
    ReturnToShuttle,
    ThreatScreen,
    CrowdControl,
    ClearRoute,
}

public enum LuaMRescueTeamPhase : byte
{
    Idle,
    Dispatch,
    EnRoute,
    SecureScene,
    Triage,
    TreatOnSite,
    PrepareEvacuation,
    EvacuateToShuttle,
    Handoff,
    ReturnOrExtract,
}

public sealed class LuaMRescueSceneMemoryEntry
{
    public LuaMRescueScenePressure Pressure = LuaMRescueScenePressure.None;
    public EntityUid? Anchor;
    public EntityUid? ThreatTarget;
    public int HostileCount;
    public int CombatantCount;
    public int CrowdCount;
    public int BlockerCount;
    public int Observations = 1;
    public TimeSpan FirstSeen;
    public TimeSpan LastSeen;
    public TimeSpan ExpiresAt;
    public string Status = string.Empty;
}

public enum LuaMRescueScenePressure : byte
{
    None,
    Threat,
    Armed,
    Route,
    Crowd,
}
