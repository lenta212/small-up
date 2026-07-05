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

    public readonly List<EntityUid> Escorts = new();
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
