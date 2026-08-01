using System;
using System.Collections.Generic;
using Content.Shared.DoAfter;
using Content.Shared.Mobs;

namespace Content.Server._LuaM.Rescue;

/// <summary>
/// Permanent identity marker for rescue personnel. Unlike the active activity
/// components it survives death/retirement, so a replacement coordinator never
/// reclassifies a former coworker as an automatic patient.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMRescuePersonnelComponent : Component
{
}

[RegisterComponent]
public sealed partial class LuaMRescueAgentComponent : Component
{
    [DataField]
    public EntityUid? AssignedTarget;

    /// <summary>
    /// Explicit coordinator/admin patient admitted through the role's manual
    /// target override. This provenance survives automatic preemption, route
    /// recovery and onboard care until the patient is released or the user
    /// explicitly replaces the order.
    /// </summary>
    [DataField]
    public EntityUid? ManualOverrideTarget;

    /// <summary>
    /// Monotonic lineage for the explicit manual patient order. Automatic
    /// preemption preserves it, while replacement/revocation advances it so a
    /// queued dispatch from an older order cannot acquire a later Aibolit.
    /// </summary>
    [DataField]
    public uint ManualOverrideGeneration;

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

    /// <summary>
    /// A terminal route is not executed again after its bounded attempt budget is
    /// exhausted. It is only observed at this low frequency so a later door or
    /// obstacle change can create one fresh intent without HTN/path-query churn.
    /// </summary>
    [DataField]
    public float DormantRouteObservationSeconds = 15f;

    [DataField]
    public float DormantRouteProbeTimeoutSeconds = 5f;

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
    public float RescueSpeechCooldown = 18f;

    [DataField]
    public float AutoPickupSupplyRange = 12f;

    [DataField]
    public float AutoResupplyRange = 24f;

    public float TargetRefreshAccumulator;

    public readonly Dictionary<EntityUid, TimeSpan> SkippedTargets = new();

    /// <summary>
    /// Patients displaced by a strictly higher-urgency local observation. This is
    /// durable mission memory, not a second active intent: entries are only resumed
    /// after the current patient owner has released all active target fields.
    /// </summary>
    public readonly Dictionary<EntityUid, TimeSpan> DeferredPatientTargets = new();

    public string LastDeferredPatientStatus = "none";

    public readonly Dictionary<EntityUid, int> RouteFailureAttempts = new();

    public EntityUid? DormantRouteTarget;

    public TimeSpan NextDormantRouteProbeAt;

    public bool DormantRouteProbeInFlight;

    public TimeSpan DormantRouteProbeStartedAt;

    public float DormantRouteActionRange;

    public int DormantRouteProbeCount;

    public int DormantRouteResumeCount;

    public string LastDormantRouteStatus = "none";

    public readonly Dictionary<EntityUid, TimeSpan> SkippedSupplyTargets = new();

    public readonly Dictionary<EntityUid, TimeSpan> SkippedDeliveryTargets = new();

    public readonly Dictionary<EntityUid, TimeSpan> AnalyzedTargets = new();

    public readonly Dictionary<EntityUid, int> AnalysisAttempts = new();

    public readonly Dictionary<EntityUid, string> TerminalAnalysisFailures = new();

    public TimeSpan NextAutoTreatmentAttempt;

    public TimeSpan NextAutoDefibAttempt;

    public TimeSpan NextAutoCommsAt;

    public TimeSpan NextAutoAnalyzeAttempt;

    public EntityUid? PendingMedicalDoAfterTarget;

    public EntityUid? PendingMedicalDoAfterItem;

    public DoAfterId? PendingMedicalDoAfterId;

    public string PendingMedicalDoAfterKind = "none";

    public TimeSpan PendingMedicalDoAfterStartedAt;

    public uint PendingMedicalIntentGeneration;

    public float PendingMedicalStartingDamage;

    public readonly Dictionary<string, float> PendingMedicalStartingDamageByType = new();

    /// <summary>
    /// Damage channels the selected item can actually improve. Medical success
    /// must be observed in one of these channels; unrelated healing is not
    /// attributable to the tracked action.
    /// </summary>
    public readonly HashSet<string> PendingMedicalExpectedDamageTypes = new(StringComparer.Ordinal);

    public float? PendingMedicalStartingVolume;

    public MobState PendingMedicalStartingMobState = MobState.Invalid;

    public float PendingMedicalStartingBleedAmount;

    public float? PendingMedicalStartingBloodVolume;

    public bool PendingMedicalExpectedBleedReduction;

    public bool PendingMedicalExpectedBloodIncrease;

    public bool PendingMedicalExpectedMobStateImprovement;

    /// <summary>
    /// A hypo/injector transfer has completed, but its authoritative medical
    /// effect has not been observed yet. Solution consumption starts this phase;
    /// it is never itself treated as a successful medical outcome.
    /// </summary>
    public bool PendingMedicalEffectVerification;

    public TimeSpan PendingMedicalEffectVerificationStartedAt;

    [DataField]
    public float MedicalEffectVerificationTimeout = 5f;

    /// <summary>
    /// The item passed the damage/species/solution/overdose planner when the
    /// authoritative action started. Solution consumption may only open the
    /// effect-verification phase when this remains true for that generation;
    /// it never confirms success by itself.
    /// </summary>
    public bool PendingMedicalExpectedPositiveEffect;

    public bool PendingMedicalOutcomeRecorded;

    public bool PendingMedicalOutcomeSucceeded;

    public readonly Dictionary<EntityUid, int> TreatmentAttempts = new();

    public readonly Dictionary<EntityUid, string> TerminalTreatmentFailures = new();

    public readonly Dictionary<EntityUid, float> TerminalTreatmentFailureDamage = new();

    public readonly Dictionary<EntityUid, int> DefibrillationAttempts = new();

    public readonly Dictionary<EntityUid, int> CompletedDefibrillationFailures = new();

    public readonly Dictionary<EntityUid, TimeSpan> DefibrillationStartedAt = new();

    public readonly Dictionary<EntityUid, string> TerminalDefibrillationFailures = new();

    public readonly Dictionary<EntityUid, int> PullAttempts = new();

    public readonly Dictionary<EntityUid, TimeSpan> NextPullAttemptAt = new();

    public readonly Dictionary<EntityUid, string> TerminalPullFailures = new();

    public readonly Dictionary<EntityUid, int> EvacuationUnbuckleAttempts = new();

    public readonly Dictionary<EntityUid, TimeSpan> NextEvacuationUnbuckleAttemptAt = new();

    public readonly Dictionary<EntityUid, string> TerminalEvacuationUnbuckleFailures = new();

    /// <summary>
    /// Buckling can fail transiently even after the patient and bed satisfy the
    /// spatial contract. Keep the retry budget on the patient/strap pair so an
    /// activity transition back to delivery cannot reset it every update.
    /// </summary>
    public readonly Dictionary<(EntityUid Patient, EntityUid Strap), int> PatientBuckleAttempts = new();

    public readonly Dictionary<(EntityUid Patient, EntityUid Strap), TimeSpan> NextPatientBuckleAttemptAt = new();

    public readonly Dictionary<(EntityUid Patient, EntityUid Strap), string> TerminalPatientBuckleFailures = new();

    public readonly Dictionary<EntityUid, int> OnboardCareAttempts = new();

    public readonly Dictionary<EntityUid, string> TerminalOnboardCareFailures = new();

    public readonly Dictionary<EntityUid, int> OnboardHandoffAttempts = new();

    public readonly HashSet<EntityUid> IgnoredOnboardPatients = new();

    /// <summary>
    /// Accepted onboard handoffs whose exact strap contract was externally
    /// broken. These patients must be recovered/re-boarded even when currently
    /// healthy, until a real release or confirmed home handoff completes.
    /// </summary>
    public readonly HashSet<EntityUid> RequiredOnboardHandoffPatients = new();

    [DataField]
    public LuaMRescueTaskStage TaskStage = LuaMRescueTaskStage.None;

    [DataField]
    public EntityUid? TaskPatientTarget;

    [DataField]
    public EntityUid? TaskSupplyTarget;

    [DataField]
    public string LastTaskStatus = "none";

    [DataField]
    public string LastTargetTrackingStatus = "none";

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

    /// <summary>
    /// Last planner-side acceptance or rejection reason. Kept separate from
    /// <see cref="LastAutoTreatmentStatus"/> so evaluating another candidate
    /// cannot overwrite the status of the treatment action currently owned.
    /// </summary>
    public string LastAutoTreatmentDecisionStatus = "none";

    /// <summary>
    /// Records which ownership path last cancelled an active generation.
    /// </summary>
    public string LastIntentCancellationStatus = "none";

    [DataField]
    public string LastAutoDefibStatus = "none";

    public string LastAutoCommsKey = "none";

    public string LastRescueSpeechKey = "none";

    public TimeSpan NextRescueSpeechAt;

    [DataField]
    public string LastRescueSpeechStatus = "none";

    public string LastOnboardActionKey = "none";

    public TimeSpan NextOnboardActionAt;

    public string LastRescueActionKey = "none";

    public TimeSpan NextRescueActionAt;

    [DataField]
    public string LastAutoAnalyzeStatus = "none";

    [DataField]
    public string LastAutoEvacuationStatus = "none";

    [DataField]
    public string LastRedispatchStatus = "none";

    [DataField]
    public string LastShuttleReturnStatus = "none";

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

    [DataField]
    public LuaMRescueRole ActivityRole = LuaMRescueRole.Aibolit;

    [DataField]
    public LuaMRescueRoleProfile ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Aibolit);

    [DataField]
    public LuaMRescueActivityContext ActivityContext = new();

    /// <summary>
    /// Runtime guard ensuring that entity termination, direct component removal,
    /// and explicit dead-agent retirement all hand off this ownership lineage at
    /// most once.
    /// </summary>
    public bool RetirementHandoffCompleted;
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

/// <summary>
/// Revokes only the elevated eligibility provenance of an older manual
/// patient. Dispatch queues may retain the target as an ordinary automatic
/// request, but must never resurrect the superseded manual permission.
/// </summary>
[ByRefEvent]
public sealed class LuaMRescueManualOverrideRevokedEvent : EntityEventArgs
{
    public readonly EntityUid Target;
    public readonly uint Generation;
    public readonly string Reason;

    public LuaMRescueManualOverrideRevokedEvent(EntityUid target, uint generation, string reason)
    {
        Target = target;
        Generation = generation;
        Reason = reason;
    }
}

/// <summary>
/// Raised after an explicit patient order passes eligibility validation, but
/// before the agent commits the replacement intent. External mission owners use
/// this boundary to retire recovery state from an older Aibolit generation
/// without discarding an exact physical onboard contract.
/// </summary>
[ByRefEvent]
public sealed class LuaMRescueExplicitPatientOrderAcceptedEvent : EntityEventArgs
{
    public readonly EntityUid Target;

    public LuaMRescueExplicitPatientOrderAcceptedEvent(EntityUid target)
    {
        Target = target;
    }
}

/// <summary>
/// Raised immediately before a disabled Aibolit loses its active role so
/// external owners can retire queued work from that exact agent lineage.
/// </summary>
[ByRefEvent]
public sealed class LuaMRescueAgentRetiringEvent : EntityEventArgs
{
    public readonly string Reason;

    public LuaMRescueAgentRetiringEvent(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Server-side handoff of a dormant route observer between singleton Aibolit
/// generations. This is deliberately not a fresh dispatch: the exhausted route
/// budget and low-frequency probe schedule remain authoritative.
/// </summary>
public readonly record struct LuaMRescueDormantRouteTransfer(
    EntityUid Target,
    EntityUid ReplacementAnchor,
    EntityUid? AssignedShuttle,
    EntityUid? AssignedShuttleAnchor,
    EntityUid? AssignedShuttleConsole,
    EntityUid? AssignedReturnTarget,
    TimeSpan NextProbeAt,
    float ActionRange,
    int ProbeCount,
    int ResumeCount,
    int RouteFailureAttempts,
    float ObservationSeconds,
    float ProbeTimeoutSeconds,
    string Status);

/// <summary>
/// Compact operator-facing health projection. Counts represent independently
/// bounded subsystem budgets/circuits rather than unique patients, because one
/// patient may legitimately have separate route and treatment failures.
/// </summary>
public readonly record struct LuaMRescueReliabilitySnapshot(
    int ActiveRetryBudgets,
    int OpenCircuits,
    int PendingOperations,
    int RequiredCustodyPatients,
    uint Generation,
    LuaMRescueTerminalStatus TerminalStatus)
{
    public bool Degraded => OpenCircuits > 0;

    public string ToDebugString()
    {
        return $"{(Degraded ? "degraded" : "nominal")}," +
               $"retry={ActiveRetryBudgets},circuits={OpenCircuits},pending={PendingOperations}," +
               $"custody={RequiredCustodyPatients},generation={Generation},terminal={TerminalStatus}";
    }
}
