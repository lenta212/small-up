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
    public LuaMRescueSortiePlan SortiePlan = LuaMRescueSortiePlan.Standby;

    [DataField]
    public TimeSpan SortiePlanUpdatedAt;

    [DataField]
    public LuaMRescueSortiePlan PendingSortiePlan = LuaMRescueSortiePlan.Standby;

    [DataField]
    public TimeSpan PendingSortiePlanSince;

    [DataField]
    public int SortiePlanTransitions;

    [DataField]
    public string LastSortiePlanStatus = "plan standby";

    [DataField]
    public string LastStatus = "none";

    [DataField]
    public LuaMRescueTeamPhase LastAnnouncedPhase = LuaMRescueTeamPhase.Idle;

    [DataField]
    public bool LastAnnouncedSomberScene;

    [DataField]
    public string LastPhaseAnnouncementStatus = "none";

    [DataField]
    public TimeSpan NextPhaseAnnouncementAt;

    [DataField]
    public string LastSharedSpeechStatus = "none";

    [DataField]
    public TimeSpan NextSharedSpeechAt;

    [DataField]
    public string LastReturnOrExtractReasonStatus = "none";

    [DataField]
    public EntityUid? LastThreatNeutralizedTarget;

    [DataField]
    public EntityUid? LastThreatNeutralizedBy;

    [DataField]
    public string LastThreatNeutralizedStatus = "none";

    [DataField]
    public TimeSpan NextThreatNeutralizedReportAt;

    [DataField]
    public EntityUid? TriageCoverConfirmedPatient;

    [DataField]
    public string LastTriageCoverDecisionKey = "none";

    [DataField]
    public string LastTriageCoverStatus = "none";

    [DataField]
    public TimeSpan NextTriageCoverConfirmAt;

    [DataField]
    public EntityUid? SceneAnchor;

    [DataField]
    public EntityUid? ThreatTarget;

    [DataField]
    public EntityUid? CrowdTarget;

    [DataField]
    public EntityUid? RouteBlockerTarget;

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
    public EntityUid? LastHandoffPatient;

    [DataField]
    public TimeSpan LastHandoffUpdatedAt;

    [DataField]
    public int HandoffRecords;

    [DataField]
    public string LastHandoffRecord = "handoff pending";

    [DataField]
    public string LastHandoffDigest = "after-action pending";

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
    public TimeSpan DutyUpdatedAt;

    [DataField]
    public LuaMRescueEscortDuty PendingDuty = LuaMRescueEscortDuty.Standby;

    [DataField]
    public TimeSpan PendingDutySince;

    [DataField]
    public int DutyTransitions;

    [DataField]
    public LuaMRescueSortiePlan SortiePlan = LuaMRescueSortiePlan.Standby;

    [DataField]
    public string LastDutyStatus = "none";

    [DataField]
    public string LastDutyActionStatus = "none";

    [DataField]
    public string LastWeaponReadinessStatus = "none";

    [DataField]
    public string LastCrewHelpStatus = "none";

    [DataField]
    public int DutyActions;

    [DataField]
    public EntityUid? SceneAnchor;

    [DataField]
    public EntityUid? ThreatTarget;

    [DataField]
    public EntityUid? CrowdTarget;

    [DataField]
    public EntityUid? RouteBlockerTarget;

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

    public TimeSpan NextDutyActionAt;

    public TimeSpan NextSpeechTime;

    public string LastCrewHelpKey = "none";

    public TimeSpan NextCrewHelpRequestAt;
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

public enum LuaMRescueSortiePlan : byte
{
    Standby,
    ApproachPatient,
    SecureScene,
    ThreatScreen,
    CrowdControl,
    ClearRoute,
    Resupply,
    TreatPatient,
    EvacuatePatient,
    ReturnToShuttle,
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
