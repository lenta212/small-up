using System;
using System.Collections.Generic;
using System.Globalization;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Rescue;

public enum LuaMRescueRole : byte
{
    None,
    Aibolit,
    Tourniquet,
    Kostyl,
    Zaslon,
    Autopilot,
}

public enum LuaMRescueActivity : byte
{
    None,

    // Canonical cross-role activity vocabulary.
    Standby,
    Observing,
    Dispatching,
    PlanningRoute,
    Approaching,
    Interacting,
    Treating,
    Protecting,
    PreparingEvacuation,
    Pulling,
    Delivering,
    Resupplying,
    Handoff,
    Returning,
    Blocked,
    Recovering,

    // Role-specific substates.
    Triage,
    BucklePatient,
    OnboardCare,
    ThreatScreen,
    CrowdControl,
    ClearRoute,

    // Backwards-compatible names used by the existing rescue systems.
    Dispatch = Dispatching,
    ApproachPatient = Approaching,
    ManualAction = Interacting,
    TreatPatient = Treating,
    SecureScene = Protecting,
    PrepareEvacuation = PreparingEvacuation,
    PullPatient = Pulling,
    DeliverPatient = Delivering,
    Resupply = Resupplying,
    ReleasePatient = Handoff,
    ReturnToShuttle = Returning,
    DefibrillatePatient = Recovering,
}

public enum LuaMRescueTerminalStatus : byte
{
    None,
    Active,
    Blocked,
    Succeeded,
    Failed,
    Cancelled,
}

public enum LuaMRescueFailureReason : byte
{
    None,

    // Canonical failures consumed by planners, tests and debug tooling.
    InvalidPatient,
    UnsupportedSpecies,
    ContainedTarget,
    NoPath,
    AccessDenied,
    NoLineOfSight,
    NoFreeHand,
    ActionCancelled,
    ItemEmpty,
    NoEffectiveMedicine,
    OverdoseRisk,
    NoDefibrillatorCharge,
    Unrevivable,
    BedUnavailable,
    ShuttleUnavailable,
    ShuttleRouteFailed,
    TargetLost,
    ThreatTooHigh,

    // Coordinator/state-machine failures.
    MissingActivityComponent,
    InvalidActivity,
    RoleDisallowed,
    InvalidTransition,
    StaleGeneration,
    ActivityNotActive,
    AttemptLimitReached,
    RetryBackoffActive,
    SelfTarget,
    TargetHealthy,
    TargetNotDead,
    TargetDead,
    TargetHasNoMind,
    TargetNotInjectable,
    TargetNotPullable,
    RouteBlocked,
    DoAfterTimedOut,
    NoEligibleTarget,
    DeadlineExceeded,
    Cancelled,
    Unknown,

    // Compatibility aliases for the pre-coordinator rescue vocabulary.
    TargetMissing = TargetLost,
    TargetContained = ContainedTarget,
    ExcludedSpecies = UnsupportedSpecies,
    UnsupportedPatient = InvalidPatient,
    TargetUnrecoverable = Unrevivable,
    InteractionRejected = AccessDenied,
    DoAfterCancelled = ActionCancelled,
    TreatmentUnavailable = NoEffectiveMedicine,
    DefibrillationUnavailable = NoDefibrillatorCharge,
}

public enum LuaMRescueRouteStatus : byte
{
    None,
    Pending,
    Planning,
    Moving,
    Arrived,
    Blocked,
    NoPath,
    InvalidDestination,
}

public enum LuaMRescueDoAfterStatus : byte
{
    None,
    Pending,
    Running,
    Succeeded,
    Cancelled,
    Failed,
    TimedOut,
}

public enum LuaMRescuePatientRequestKind : byte
{
    AutomaticTreatment,
    AutomaticEvacuation,
    AutomaticDeathSignal,
    OnboardCare,
    Manual,
}

public enum LuaMRescuePatientUrgency : byte
{
    None,
    Stable,
    Injured,
    RecoverableDead,
    Severe,
    Deteriorating,
    Critical,
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueActivityContext
{
    [DataField]
    public LuaMRescueActivity Activity = LuaMRescueActivity.None;

    [DataField]
    public LuaMRescueTerminalStatus TerminalStatus = LuaMRescueTerminalStatus.None;

    [DataField]
    public EntityUid? Target;

    [DataField]
    public EntityCoordinates? Destination;

    [DataField]
    public TimeSpan StartedAt;

    [DataField]
    public TimeSpan LastProgressAt;

    [DataField]
    public float? LastProgressDistance;

    [DataField]
    public int Attempts;

    [DataField]
    public uint Generation;

    [DataField]
    public bool Blocked;

    [DataField]
    public LuaMRescueActivity Fallback = LuaMRescueActivity.None;

    [DataField]
    public LuaMRescueFailureReason FailureReason = LuaMRescueFailureReason.None;

    [DataField]
    public TimeSpan RetryNotBefore;

    [DataField]
    public LuaMRescueRouteStatus RouteStatus = LuaMRescueRouteStatus.None;

    [DataField]
    public LuaMRescueDoAfterStatus DoAfterStatus = LuaMRescueDoAfterStatus.None;

    [DataField]
    public TimeSpan LastTransitionAt;

    [DataField]
    public TimeSpan Deadline;
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueActivityPolicy
{
    [DataField]
    public LuaMRescueActivity Activity = LuaMRescueActivity.None;

    [DataField]
    public TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [DataField]
    public LuaMRescueActivity TimeoutFallback = LuaMRescueActivity.Standby;

    public static LuaMRescueActivityPolicy CreateDefault(LuaMRescueActivity activity)
    {
        var timeout = activity switch
        {
            LuaMRescueActivity.Standby => TimeSpan.FromMinutes(5),
            LuaMRescueActivity.Observing => TimeSpan.FromMinutes(2),
            LuaMRescueActivity.Dispatching => TimeSpan.FromSeconds(20),
            // PlanningRoute also covers the confirmed shuttle approach. Keep its
            // deadline just beyond the shuttle's bounded 180 second route window;
            // local path probes normally complete much sooner.
            LuaMRescueActivity.PlanningRoute => TimeSpan.FromSeconds(200),
            LuaMRescueActivity.Approaching => TimeSpan.FromSeconds(90),
            LuaMRescueActivity.Interacting => TimeSpan.FromSeconds(15),
            LuaMRescueActivity.Treating => TimeSpan.FromSeconds(60),
            LuaMRescueActivity.Protecting => TimeSpan.FromSeconds(90),
            LuaMRescueActivity.PreparingEvacuation => TimeSpan.FromSeconds(30),
            LuaMRescueActivity.Pulling => TimeSpan.FromSeconds(90),
            LuaMRescueActivity.Delivering => TimeSpan.FromMinutes(2),
            LuaMRescueActivity.Resupplying => TimeSpan.FromSeconds(60),
            LuaMRescueActivity.Handoff => TimeSpan.FromSeconds(30),
            LuaMRescueActivity.Returning => TimeSpan.FromMinutes(2),
            LuaMRescueActivity.Recovering => TimeSpan.FromSeconds(45),
            LuaMRescueActivity.Triage => TimeSpan.FromSeconds(30),
            LuaMRescueActivity.BucklePatient => TimeSpan.FromSeconds(20),
            LuaMRescueActivity.OnboardCare => TimeSpan.FromMinutes(2),
            LuaMRescueActivity.ThreatScreen => TimeSpan.FromSeconds(60),
            LuaMRescueActivity.CrowdControl => TimeSpan.FromSeconds(60),
            LuaMRescueActivity.ClearRoute => TimeSpan.FromSeconds(60),
            _ => TimeSpan.Zero,
        };

        return new LuaMRescueActivityPolicy
        {
            Activity = activity,
            Timeout = timeout,
            TimeoutFallback = LuaMRescueActivity.Standby,
        };
    }
}

[Flags]
public enum LuaMRescueEquipmentKind : ushort
{
    None = 0,
    Analyzer = 1 << 0,
    TopicalMedicine = 1 << 1,
    Injector = 1 << 2,
    Defibrillator = 1 << 3,
    Pulling = 1 << 4,
    Buckling = 1 << 5,
    Weapon = 1 << 6,
    AccessCredential = 1 << 7,
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueTargetPolicy
{
    [DataField]
    public List<LuaMRescuePatientRequestKind> AllowedRequestKinds = new();

    [DataField]
    public bool CanSelectMedicalTargets;

    [DataField]
    public bool AllowManualOverride = true;

    [DataField]
    public bool RequireHumanoidForAutomatic = true;

    [DataField]
    public bool AllowDeadRecovery = true;

    [DataField]
    public bool RejectContained = true;

    [DataField]
    public bool RejectHostileAutomatic = true;

    [DataField]
    public bool RejectRescuePersonnel = true;

    [DataField]
    public bool RejectCorticalBorer = true;

    [DataField]
    public bool RequireInjectableForTreatment = true;

    [DataField]
    public bool RequirePullableForEvacuation = true;

    public bool Allows(LuaMRescuePatientRequestKind kind)
    {
        return AllowedRequestKinds.Contains(kind);
    }
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueActivityPriorityPolicy
{
    [DataField]
    public LuaMRescueActivity Activity;

    [DataField]
    public int Priority;
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueUrgencyPriorityPolicy
{
    [DataField]
    public LuaMRescuePatientUrgency Urgency;

    [DataField]
    public int Priority;
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueActivityRangePolicy
{
    [DataField]
    public LuaMRescueActivity Activity;

    [DataField]
    public float ActionRange = 1.5f;
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueEquipmentPolicy
{
    [DataField]
    public LuaMRescueEquipmentKind AllowedEquipment = LuaMRescueEquipmentKind.None;

    [DataField]
    public bool RequireFreeHandForPull = true;

    [DataField]
    public bool AllowNavPry;

    [DataField]
    public List<string> InventorySearchSlots = new();

    public bool Allows(LuaMRescueEquipmentKind equipment)
    {
        return equipment != LuaMRescueEquipmentKind.None &&
               (AllowedEquipment & equipment) == equipment;
    }
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueThreatPolicy
{
    [DataField]
    public bool EngageHostiles;

    [DataField]
    public bool ProtectPatient = true;

    [DataField]
    public bool RequestHelpWhenOverwhelmed = true;

    [DataField]
    public float ObservationRange = 6f;

    [DataField]
    public float FormationRange = 1.75f;

    [DataField]
    public float PursuitLeashRange = 8.5f;

    [DataField]
    public int SelfPreservationHostileLimit = 3;
}

[Serializable, DataDefinition]
public sealed partial class LuaMRescueRoleProfile
{
    [DataField]
    public LuaMRescueRole Role = LuaMRescueRole.None;

    [DataField]
    public List<LuaMRescueActivity> AllowedActivities = new();

    [DataField]
    public int MaxAttempts = 3;

    [DataField]
    public List<LuaMRescueActivityPolicy> ActivityPolicies = new();

    [DataField]
    public TimeSpan BaseRetryBackoff = TimeSpan.FromSeconds(1);

    [DataField]
    public TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    [DataField]
    public float ProgressTolerance = 0.25f;

    [DataField]
    public LuaMRescueTargetPolicy TargetPolicy = new();

    [DataField]
    public List<LuaMRescueActivityPriorityPolicy> ActivityPriorities = new();

    [DataField]
    public List<LuaMRescueUrgencyPriorityPolicy> PatientPriorities = new();

    [DataField]
    public List<LuaMRescueActivityRangePolicy> ActivityActionRanges = new();

    [DataField]
    public LuaMRescueEquipmentPolicy EquipmentPolicy = new();

    [DataField]
    public LuaMRescueThreatPolicy ThreatPolicy = new();

    public bool Allows(LuaMRescueActivity activity)
    {
        return activity != LuaMRescueActivity.None && AllowedActivities.Contains(activity);
    }

    public bool TryGetPolicy(LuaMRescueActivity activity, out LuaMRescueActivityPolicy policy)
    {
        foreach (var candidate in ActivityPolicies)
        {
            if (candidate.Activity != activity)
                continue;

            policy = candidate;
            return true;
        }

        policy = default!;
        return false;
    }

    public int GetActivityPriority(LuaMRescueActivity activity)
    {
        foreach (var policy in ActivityPriorities)
        {
            if (policy.Activity == activity)
                return policy.Priority;
        }

        return 0;
    }

    public int GetPatientPriority(LuaMRescuePatientUrgency urgency)
    {
        foreach (var policy in PatientPriorities)
        {
            if (policy.Urgency == urgency)
                return policy.Priority;
        }

        return 0;
    }

    public float GetActionRange(LuaMRescueActivity activity, float fallback)
    {
        foreach (var policy in ActivityActionRanges)
        {
            if (policy.Activity == activity)
                return Math.Max(0.05f, policy.ActionRange);
        }

        return Math.Max(0.05f, fallback);
    }

    public static LuaMRescueRoleProfile CreateDefault(LuaMRescueRole role)
    {
        var allowed = role switch
        {
            LuaMRescueRole.Aibolit => new List<LuaMRescueActivity>
            {
                LuaMRescueActivity.Standby,
                LuaMRescueActivity.Observing,
                LuaMRescueActivity.Dispatching,
                LuaMRescueActivity.PlanningRoute,
                LuaMRescueActivity.Approaching,
                LuaMRescueActivity.Interacting,
                LuaMRescueActivity.Triage,
                LuaMRescueActivity.Treating,
                LuaMRescueActivity.Recovering,
                LuaMRescueActivity.PreparingEvacuation,
                LuaMRescueActivity.Pulling,
                LuaMRescueActivity.Delivering,
                LuaMRescueActivity.BucklePatient,
                LuaMRescueActivity.OnboardCare,
                LuaMRescueActivity.Handoff,
                LuaMRescueActivity.Resupplying,
                LuaMRescueActivity.Returning,
            },
            LuaMRescueRole.Tourniquet => new List<LuaMRescueActivity>
            {
                LuaMRescueActivity.Standby,
                LuaMRescueActivity.Observing,
                LuaMRescueActivity.Dispatching,
                LuaMRescueActivity.PlanningRoute,
                LuaMRescueActivity.Approaching,
                LuaMRescueActivity.Interacting,
                LuaMRescueActivity.Protecting,
                LuaMRescueActivity.CrowdControl,
                LuaMRescueActivity.ClearRoute,
                LuaMRescueActivity.Returning,
            },
            LuaMRescueRole.Kostyl => new List<LuaMRescueActivity>
            {
                LuaMRescueActivity.Standby,
                LuaMRescueActivity.Observing,
                LuaMRescueActivity.Dispatching,
                LuaMRescueActivity.PlanningRoute,
                LuaMRescueActivity.Approaching,
                LuaMRescueActivity.Interacting,
                LuaMRescueActivity.PreparingEvacuation,
                LuaMRescueActivity.Pulling,
                LuaMRescueActivity.Delivering,
                LuaMRescueActivity.BucklePatient,
                LuaMRescueActivity.Handoff,
                LuaMRescueActivity.Returning,
            },
            LuaMRescueRole.Zaslon => new List<LuaMRescueActivity>
            {
                LuaMRescueActivity.Standby,
                LuaMRescueActivity.Observing,
                LuaMRescueActivity.Dispatching,
                LuaMRescueActivity.PlanningRoute,
                LuaMRescueActivity.Approaching,
                LuaMRescueActivity.Interacting,
                LuaMRescueActivity.Protecting,
                LuaMRescueActivity.ThreatScreen,
                LuaMRescueActivity.ClearRoute,
                LuaMRescueActivity.Returning,
            },
            LuaMRescueRole.Autopilot => new List<LuaMRescueActivity>
            {
                LuaMRescueActivity.Standby,
                LuaMRescueActivity.PlanningRoute,
                LuaMRescueActivity.Delivering,
                LuaMRescueActivity.Returning,
                LuaMRescueActivity.Recovering,
            },
            _ => new List<LuaMRescueActivity>(),
        };

        var activityPolicies = CreatePolicies(allowed);
        if (role == LuaMRescueRole.Autopilot)
            ConfigureAutopilotPolicies(activityPolicies);

        return new LuaMRescueRoleProfile
        {
            Role = role,
            AllowedActivities = allowed,
            ActivityPolicies = activityPolicies,
            ActivityPriorities = CreateActivityPriorities(allowed),
            PatientPriorities = CreatePatientPriorities(),
            ActivityActionRanges = CreateActivityActionRanges(role, allowed),
            TargetPolicy = CreateTargetPolicy(role),
            EquipmentPolicy = CreateEquipmentPolicy(role),
            ThreatPolicy = CreateThreatPolicy(role),
            MaxAttempts = 3,
        };
    }

    private static LuaMRescueTargetPolicy CreateTargetPolicy(LuaMRescueRole role)
    {
        var medicalSupport = role is LuaMRescueRole.Aibolit or LuaMRescueRole.Kostyl;
        var allowedRequestKinds = role switch
        {
            LuaMRescueRole.Aibolit => new List<LuaMRescuePatientRequestKind>
            {
                LuaMRescuePatientRequestKind.AutomaticTreatment,
                LuaMRescuePatientRequestKind.AutomaticEvacuation,
                LuaMRescuePatientRequestKind.AutomaticDeathSignal,
                LuaMRescuePatientRequestKind.OnboardCare,
                LuaMRescuePatientRequestKind.Manual,
            },
            LuaMRescueRole.Kostyl => new List<LuaMRescuePatientRequestKind>
            {
                LuaMRescuePatientRequestKind.AutomaticEvacuation,
                LuaMRescuePatientRequestKind.OnboardCare,
                LuaMRescuePatientRequestKind.Manual,
            },
            // Protection roles may validate the patient assigned by their
            // coordinator, but CanSelectMedicalTargets remains false: they do
            // not scan for or take ownership of medical patients themselves.
            LuaMRescueRole.Tourniquet or LuaMRescueRole.Zaslon =>
                new List<LuaMRescuePatientRequestKind>
                {
                    LuaMRescuePatientRequestKind.AutomaticEvacuation,
                },
            _ => new List<LuaMRescuePatientRequestKind>(),
        };
        return new LuaMRescueTargetPolicy
        {
            AllowedRequestKinds = allowedRequestKinds,
            CanSelectMedicalTargets = role == LuaMRescueRole.Aibolit,
            AllowManualOverride = medicalSupport,
            // Protection profiles validate the coordinator-assigned body so
            // they keep formation during a recovery sortie. They still cannot
            // independently select it because CanSelectMedicalTargets is false.
            AllowDeadRecovery = role is LuaMRescueRole.Aibolit
                or LuaMRescueRole.Kostyl
                or LuaMRescueRole.Tourniquet
                or LuaMRescueRole.Zaslon,
        };
    }

    private static LuaMRescueEquipmentPolicy CreateEquipmentPolicy(LuaMRescueRole role)
    {
        var equipment = role switch
        {
            LuaMRescueRole.Aibolit => LuaMRescueEquipmentKind.Analyzer |
                                      LuaMRescueEquipmentKind.TopicalMedicine |
                                      LuaMRescueEquipmentKind.Injector |
                                      LuaMRescueEquipmentKind.Defibrillator |
                                      LuaMRescueEquipmentKind.Pulling |
                                      LuaMRescueEquipmentKind.Buckling |
                                      LuaMRescueEquipmentKind.AccessCredential,
            LuaMRescueRole.Kostyl => LuaMRescueEquipmentKind.Pulling |
                                     LuaMRescueEquipmentKind.Buckling |
                                     LuaMRescueEquipmentKind.AccessCredential,
            LuaMRescueRole.Tourniquet or LuaMRescueRole.Zaslon => LuaMRescueEquipmentKind.Weapon |
                                                                  LuaMRescueEquipmentKind.Pulling |
                                                                  LuaMRescueEquipmentKind.AccessCredential,
            _ => LuaMRescueEquipmentKind.None,
        };

        return new LuaMRescueEquipmentPolicy
        {
            AllowedEquipment = equipment,
            RequireFreeHandForPull = true,
            AllowNavPry = false,
            InventorySearchSlots = role switch
            {
                LuaMRescueRole.Aibolit =>
                    new List<string> { "belt", "back", "pocket1", "pocket2", "suitstorage", "outerClothing", "jumpsuit" },
                LuaMRescueRole.Autopilot => new List<string>(),
                _ => new List<string> { "back", "belt", "suitstorage", "outerClothing" },
            },
        };
    }

    private static LuaMRescueThreatPolicy CreateThreatPolicy(LuaMRescueRole role)
    {
        var escort = role is LuaMRescueRole.Tourniquet or LuaMRescueRole.Kostyl or LuaMRescueRole.Zaslon;
        return new LuaMRescueThreatPolicy
        {
            EngageHostiles = role is LuaMRescueRole.Tourniquet or LuaMRescueRole.Zaslon,
            ProtectPatient = role != LuaMRescueRole.Autopilot,
            RequestHelpWhenOverwhelmed = role != LuaMRescueRole.Autopilot,
            ObservationRange = role == LuaMRescueRole.Autopilot ? 0f : escort ? 7f : 6f,
            FormationRange = role == LuaMRescueRole.Autopilot ? 0f : escort ? 1.75f : 4f,
            PursuitLeashRange = escort ? 8.5f : 0f,
            SelfPreservationHostileLimit = role == LuaMRescueRole.Aibolit ? 3 : 5,
        };
    }

    private static List<LuaMRescueActivityPriorityPolicy> CreateActivityPriorities(
        List<LuaMRescueActivity> allowed)
    {
        var policies = new List<LuaMRescueActivityPriorityPolicy>(allowed.Count);
        foreach (var activity in allowed)
        {
            var priority = activity switch
            {
                LuaMRescueActivity.Recovering => 1000,
                LuaMRescueActivity.Treating or LuaMRescueActivity.OnboardCare => 900,
                LuaMRescueActivity.Protecting or LuaMRescueActivity.ThreatScreen => 850,
                LuaMRescueActivity.Pulling or LuaMRescueActivity.Delivering => 800,
                LuaMRescueActivity.Approaching or LuaMRescueActivity.PlanningRoute => 700,
                LuaMRescueActivity.Resupplying => 500,
                LuaMRescueActivity.Returning => 300,
                LuaMRescueActivity.Standby => 100,
                _ => 600,
            };
            policies.Add(new LuaMRescueActivityPriorityPolicy { Activity = activity, Priority = priority });
        }

        return policies;
    }

    private static List<LuaMRescueUrgencyPriorityPolicy> CreatePatientPriorities()
    {
        return new List<LuaMRescueUrgencyPriorityPolicy>
        {
            new() { Urgency = LuaMRescuePatientUrgency.Critical, Priority = 600_000 },
            new() { Urgency = LuaMRescuePatientUrgency.Deteriorating, Priority = 500_000 },
            new() { Urgency = LuaMRescuePatientUrgency.Severe, Priority = 400_000 },
            new() { Urgency = LuaMRescuePatientUrgency.RecoverableDead, Priority = 300_000 },
            new() { Urgency = LuaMRescuePatientUrgency.Injured, Priority = 200_000 },
            new() { Urgency = LuaMRescuePatientUrgency.Stable, Priority = 100_000 },
        };
    }

    private static List<LuaMRescueActivityRangePolicy> CreateActivityActionRanges(
        LuaMRescueRole role,
        List<LuaMRescueActivity> allowed)
    {
        var policies = new List<LuaMRescueActivityRangePolicy>(allowed.Count);
        foreach (var activity in allowed)
        {
            var range = activity switch
            {
                LuaMRescueActivity.Standby => 4f,
                LuaMRescueActivity.Observing => role == LuaMRescueRole.Aibolit ? 16f : 7f,
                LuaMRescueActivity.ThreatScreen => 7f,
                LuaMRescueActivity.Protecting => 2.5f,
                LuaMRescueActivity.CrowdControl => 3f,
                LuaMRescueActivity.Returning => 5f,
                LuaMRescueActivity.PreparingEvacuation when role == LuaMRescueRole.Kostyl => 1.5f,
                _ when role == LuaMRescueRole.Autopilot => 0.05f,
                _ => role == LuaMRescueRole.Aibolit ? 1.5f : 1.75f,
            };
            policies.Add(new LuaMRescueActivityRangePolicy { Activity = activity, ActionRange = range });
        }

        return policies;
    }

    private static List<LuaMRescueActivityPolicy> CreatePolicies(List<LuaMRescueActivity> allowed)
    {
        var policies = new List<LuaMRescueActivityPolicy>(allowed.Count);
        foreach (var activity in allowed)
            policies.Add(LuaMRescueActivityPolicy.CreateDefault(activity));

        return policies;
    }

    private static void ConfigureAutopilotPolicies(List<LuaMRescueActivityPolicy> policies)
    {
        foreach (var policy in policies)
        {
            policy.Timeout = policy.Activity switch
            {
                LuaMRescueActivity.PlanningRoute => TimeSpan.FromSeconds(20),
                LuaMRescueActivity.Delivering or LuaMRescueActivity.Returning => TimeSpan.FromSeconds(180),
                LuaMRescueActivity.Recovering => TimeSpan.FromSeconds(30),
                _ => policy.Timeout,
            };
            policy.TimeoutFallback = LuaMRescueActivity.Standby;
        }
    }
}

[Serializable]
public readonly record struct LuaMRescueActivitySnapshot(
    EntityUid Agent,
    LuaMRescueRole Role,
    LuaMRescueActivity Activity,
    LuaMRescueTerminalStatus TerminalStatus,
    EntityUid? Target,
    EntityCoordinates? Destination,
    TimeSpan StartedAt,
    TimeSpan LastProgressAt,
    float? LastProgressDistance,
    int Attempts,
    uint Generation,
    bool Blocked,
    LuaMRescueActivity Fallback,
    LuaMRescueFailureReason FailureReason,
    TimeSpan RetryNotBefore,
    LuaMRescueRouteStatus RouteStatus,
    LuaMRescueDoAfterStatus DoAfterStatus,
    TimeSpan LastTransitionAt,
    TimeSpan Deadline)
{
    public bool IsTerminal => TerminalStatus is LuaMRescueTerminalStatus.Blocked
        or LuaMRescueTerminalStatus.Succeeded
        or LuaMRescueTerminalStatus.Failed
        or LuaMRescueTerminalStatus.Cancelled;

    public string ToDebugString()
    {
        var target = Target?.ToString() ?? "none";
        var destination = Destination is { } coordinates
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{coordinates.EntityId}@{coordinates.Position.X:0.00},{coordinates.Position.Y:0.00}")
            : "none";
        var distance = LastProgressDistance?.ToString("0.00", CultureInfo.InvariantCulture) ?? "none";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"agent={Agent}; role={Role}; activity={Activity}; terminal={TerminalStatus}; generation={Generation}; " +
            $"target={target}; destination={destination}; attempts={Attempts}; blocked={Blocked}; " +
            $"fallback={Fallback}; failure={FailureReason}; route={RouteStatus}; doAfter={DoAfterStatus}; " +
            $"started={StartedAt.TotalSeconds:0.00}s; progress={LastProgressAt.TotalSeconds:0.00}s; " +
            $"transition={LastTransitionAt.TotalSeconds:0.00}s; distance={distance}; " +
            $"retryAt={RetryNotBefore.TotalSeconds:0.00}s; deadline={Deadline.TotalSeconds:0.00}s");
    }
}
