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
    public EntityUid? DeathSignalTarget;

    [DataField]
    public bool DeathSignalDispatchReported;

    [DataField]
    public EntityUid? ArrivalReportedTarget;

    [DataField]
    public EntityUid? TriageReportedTarget;

    [DataField]
    public EntityUid? ShuttleRoutedTarget;

    [DataField]
    public EntityUid? EvacuatingTarget;

    [DataField]
    public EntityUid? OnboardCareTarget;

    [DataField]
    public string LastOnboardCareStatus = "none";

    [DataField]
    public string LastOnboardActionStatus = "none";

    [DataField]
    public string LastRescueActionStatus = "none";

    [DataField]
    public bool AutoAcquireTargets = true;

    [DataField]
    public float AutoAcquireMinDamage = 5f;

    [DataField]
    public bool EvacuateTargetsToShuttle = true;

    [DataField]
    public bool RecoverDeadPatientsToShuttle = true;

    [DataField]
    public bool BucklePatientsOnShuttle = true;

    [DataField]
    public bool AutoUnbucklePatientsForEvacuation = true;

    [DataField]
    public bool PreferStasisBedDelivery = true;

    [DataField]
    public bool AutoReleaseStabilizedPatients = true;

    [DataField]
    public bool AutoTreatWithCarriedItems = true;

    [DataField]
    public bool AutoDefibDeadPatients = true;

    [DataField]
    public bool AutoAnalyzeBeforeTreatment = true;

    [DataField]
    public bool AutoPickupNearbyMedicalSupplies = true;

    [DataField]
    public bool AutoTakeNearbyStoredMedicalSupplies = true;

    [DataField]
    public bool AutoStowHeldItemsForTreatment = true;

    [DataField]
    public bool AutoStoreCollectedMedicalSupplies = true;

    [DataField]
    public bool AutoResupplyFromVending = true;

    [DataField]
    public bool AutoRouteShuttleToTargets = true;

    [DataField]
    public bool AutoReturnShuttle = true;

    [DataField]
    public bool EvacuateWhenSceneThreatened = true;

    [DataField]
    public bool TemporarilySkipStalledTargets = true;

    [DataField]
    public bool TemporarilySkipFailedSupplies = true;

    [DataField]
    public bool TemporarilySkipFailedDeliveryTargets = true;

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
    public float ThreatEvacuationMinDamage = 5f;

    [DataField]
    public int OverwhelmingThreatHostileThreshold = 3;

    [DataField]
    public int OverwhelmingThreatCombatantThreshold = 4;

    [DataField]
    public float AutoReleaseMaxDamage = 5f;

    [DataField]
    public float AutoReleaseRange = 1.5f;

    [DataField]
    public float TargetStallSeconds = 20f;

    [DataField]
    public bool HoldPositionOnBlockedEvacuation = true;

    [DataField]
    public float RouteBlockHoldSeconds = 4f;

    [DataField]
    public float TargetSkipSeconds = 45f;

    [DataField]
    public float SupplySkipSeconds = 30f;

    [DataField]
    public float DeliverySkipSeconds = 30f;

    [DataField]
    public float TargetProgressTolerance = 0.25f;

    [DataField]
    public float AutoTreatMinDamage = 5f;

    [DataField]
    public float AutoTreatCooldown = 6f;

    [DataField]
    public float AutoDefibCooldown = 8f;

    [DataField]
    public float AutoCommsCooldown = 10f;

    [DataField]
    public float AutoAnalyzeCooldown = 45f;

    [DataField]
    public float OnboardActionCooldown = 10f;

    [DataField]
    public float RescueActionCooldown = 6f;

    [DataField]
    public float AutoPickupSupplyRange = 12f;

    [DataField]
    public float AutoResupplyRange = 24f;

    public float TargetRefreshAccumulator;

    public readonly Dictionary<EntityUid, TimeSpan> SkippedTargets = new();

    public readonly Dictionary<EntityUid, TimeSpan> SkippedSupplyTargets = new();

    public readonly Dictionary<EntityUid, TimeSpan> SkippedDeliveryTargets = new();

    public readonly Dictionary<EntityUid, TimeSpan> AnalyzedTargets = new();

    public TimeSpan NextAutoTreatmentAttempt;

    public TimeSpan NextAutoDefibAttempt;

    public TimeSpan NextAutoCommsAt;

    public TimeSpan NextAutoAnalyzeAttempt;

    [DataField]
    public LuaMRescueTaskStage TaskStage = LuaMRescueTaskStage.None;

    [DataField]
    public EntityUid? TaskPatientTarget;

    [DataField]
    public EntityUid? TaskSupplyTarget;

    [DataField]
    public string LastTaskStatus = "none";

    public EntityUid? ProgressTarget;

    public EntityUid? ProgressGoal;

    public float LastProgressDistance = float.PositiveInfinity;

    public float TargetStallAccumulator;

    public EntityUid? RouteBlockHoldTarget;

    public EntityUid? RouteBlockHoldGoal;

    public TimeSpan RouteBlockHoldStartedAt;

    public bool RouteBlockHelpRequested;

    [DataField]
    public string LastRouteBlockHoldStatus = "none";

    [DataField]
    public LuaMRescuePlayerActionKind PendingPlayerAction = LuaMRescuePlayerActionKind.None;

    [DataField]
    public EntityUid? PendingPlayerActionTarget;

    [DataField]
    public string? PendingPlayerActionSlot;

    [DataField]
    public string? PendingPlayerActionItem;

    [DataField]
    public float PlayerActionRange = 1.5f;

    [DataField]
    public float PlayerActionTimeout = 20f;

    public float PlayerActionAccumulator;

    [DataField]
    public string LastPlayerActionStatus = "none";

    [DataField]
    public string LastAutoTreatmentStatus = "none";

    [DataField]
    public string LastAutoDefibStatus = "none";

    public string LastAutoCommsKey = "none";

    public string LastOnboardActionKey = "none";

    public TimeSpan NextOnboardActionAt;

    public string LastRescueActionKey = "none";

    public TimeSpan NextRescueActionAt;

    [DataField]
    public string LastAutoAnalyzeStatus = "none";

    [DataField]
    public string LastAutoEvacuationStatus = "none";

    [DataField]
    public string LastAutoSupplyStatus = "none";

    [DataField]
    public string LastArrivalReportStatus = "none";

    [DataField]
    public string LastTriageDecisionKey = "none";

    [DataField]
    public string LastTriageDecisionStatus = "none";

    public bool PendingVendingStarted;

    public string? PendingVendingProduct;

    public EntityUid? PendingVendingDispensedItem;

    public EntityUid? PendingStorageTakenItem;

    [DataField]
    public string Role = "rescue";
}

public enum LuaMRescuePlayerActionKind : byte
{
    None,
    Interact,
    AltInteract,
    Use,
    Pickup,
    Drop,
    Pull,
    StopPull,
    Buckle,
    Unbuckle,
    EquipSlot,
    UnequipSlot,
    StoreSlot,
    TakeStorage,
    TakeTargetStorage,
    Treat,
    Vend,
}

public enum LuaMRescueTaskStage : byte
{
    None,
    Standby,
    FollowingPatient,
    TreatingPatient,
    PickingUpSupply,
    VendingSupply,
    EvacuatingPatient,
    DeliveringPatient,
    ManualAction,
}
