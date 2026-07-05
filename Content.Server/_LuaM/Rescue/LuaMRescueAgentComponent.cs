using System;
using System.Collections.Generic;

namespace Content.Server._LuaM.Rescue;

[RegisterComponent]
public sealed partial class LuaMRescueAgentComponent : Component
{
    [DataField]
    public EntityUid? AssignedTarget;

    [DataField]
    public EntityUid? AssignedShuttle;

    [DataField]
    public EntityUid? AssignedShuttleAnchor;

    [DataField]
    public EntityUid? AssignedPatientStrap;

    [DataField]
    public EntityUid? AssignedShuttleConsole;

    [DataField]
    public EntityUid? AssignedReturnTarget;

    [DataField]
    public EntityUid? ShuttleRoutedTarget;

    [DataField]
    public EntityUid? EvacuatingTarget;

    [DataField]
    public bool AutoAcquireTargets = true;

    [DataField]
    public bool EvacuateTargetsToShuttle = true;

    [DataField]
    public bool BucklePatientsOnShuttle = true;

    [DataField]
    public bool PreferStasisBedDelivery = true;

    [DataField]
    public bool AutoRouteShuttleToTargets = true;

    [DataField]
    public bool AutoReturnShuttle = true;

    [DataField]
    public bool TemporarilySkipStalledTargets = true;

    [DataField]
    public bool ShuttleReturnRouted;

    [DataField]
    public float SearchRange = 32f;

    [DataField]
    public float TargetRefreshInterval = 2f;

    [DataField]
    public float FollowCloseRange = 1.25f;

    [DataField]
    public float FollowRange = 4f;

    [DataField]
    public float EvacuationStartRange = 1.5f;

    [DataField]
    public float EvacuationArrivalRange = 3f;

    [DataField]
    public float EvacuationMinDamage = 50f;

    [DataField]
    public float TargetStallSeconds = 20f;

    [DataField]
    public float TargetSkipSeconds = 45f;

    [DataField]
    public float TargetProgressTolerance = 0.25f;

    public float TargetRefreshAccumulator;

    public readonly Dictionary<EntityUid, TimeSpan> SkippedTargets = new();

    public EntityUid? ProgressTarget;

    public EntityUid? ProgressGoal;

    public float LastProgressDistance = float.PositiveInfinity;

    public float TargetStallAccumulator;

    [DataField]
    public string Role = "rescue";
}
