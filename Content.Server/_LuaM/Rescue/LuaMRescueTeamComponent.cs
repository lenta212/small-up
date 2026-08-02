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
    public string LastTeamLine = "none";

    [DataField]
    public string LastTeamLineKey = "none";

    [DataField]
    public TimeSpan LastTeamLineAt;

    [DataField]
    public List<LuaMRescueTeamSpeechMemoryEntry> RecentTeamLines = new();

    [DataField]
    public string LastReturnOrExtractReasonStatus = "none";

    [DataField]
    public string LastEvacuationFormationStatus = "none";

    [DataField]
    public string LastEscortActivityDigest = "none";

    [DataField]
    public string LastCrewHelpAcknowledgementStatus = "none";

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

    [DataField]
    public bool AutoManageLifeSupport = true;

    [DataField]
    public float LifeSupportCheckInterval = 1f;

    [DataField]
    public float LifeSupportSwapPressure = 30f;

    public float LifeSupportCheckAccumulator;

    [DataField]
    public string LastLifeSupportStatus = "not checked";

    public int LifeSupportSwapCount;

    public TimeSpan NextDutyActionAt;

    public TimeSpan NextSpeechTime;

    public string LastCrewHelpKey = "none";

    public TimeSpan NextCrewHelpRequestAt;

    public EntityUid? PatientAssistAttemptTarget;

    public int PatientAssistAttempts;

    public TimeSpan NextPatientAssistAttemptAt;

    public EntityUid? PatientHandoffAttemptTarget;

    public int PatientHandoffAttempts;

    public TimeSpan NextPatientHandoffAttemptAt;

    public EntityUid? ClearRoutePullAttemptTarget;

    public int ClearRoutePullAttempts;

    public TimeSpan NextClearRoutePullAttemptAt;

    public EntityUid? ClearRouteReleaseAttemptTarget;

    public int ClearRouteReleaseAttempts;

    public TimeSpan NextClearRouteReleaseAttemptAt;
}

/// <summary>
/// Activity state for rescue actors that deliberately do not carry <see cref="LuaMRescueAgentComponent"/>.
/// It uses the same role/profile/context vocabulary as Aibolit without making escorts visible to AgentSystem.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMRescueActivityCarrierComponent : Component
{
    [DataField]
    public LuaMRescueRole ActivityRole = LuaMRescueRole.None;

    [DataField]
    public LuaMRescueRoleProfile ActivityRoleProfile = new();

    [DataField]
    public LuaMRescueActivityContext ActivityContext = new();

    [DataField]
    public LuaMRescueEscortDuty SourceDuty = LuaMRescueEscortDuty.Standby;

    [DataField]
    public int IntentTransitions;

    /// <summary>
    /// Terminal route failures keep a separate, bounded recovery budget. This lets an
    /// escort resume the same logical duty when a door or temporary obstruction clears
    /// without recreating the intent every update tick.
    /// </summary>
    [DataField]
    public bool TerminalRecoveryEvaluated;

    [DataField]
    public bool TerminalRecoveryArmed;

    [DataField]
    public bool TerminalRecoveryDormant;

    [DataField]
    public int TerminalRecoveryAttempts;

    [DataField]
    public TimeSpan NextTerminalRecoveryAt;

    [DataField]
    public bool TerminalRecoveryProbeInFlight;

    [DataField]
    public TimeSpan TerminalRecoveryProbeStartedAt;

    [DataField]
    public string LastTerminalRecoveryStatus = "not-evaluated";

    [DataField]
    public string LastStatus = "role=None; activity=None; terminal=None; generation=0; target=none";
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

[DataDefinition]
public sealed partial class LuaMRescueTeamSpeechMemoryEntry
{
    [DataField]
    public string Key = string.Empty;

    [DataField]
    public string Line = string.Empty;

    [DataField]
    public TimeSpan SpokenAt;

    [DataField]
    public TimeSpan ExpiresAt;
}

public enum LuaMRescueScenePressure : byte
{
    None,
    Threat,
    Armed,
    Route,
    Crowd,
}
