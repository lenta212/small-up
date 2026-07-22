using System;
using System.Collections.Generic;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;

namespace Content.Shared._LuaM.AI;

/// <summary>
/// Stable priority bands. A higher band always preempts a lower one; scores
/// only order decisions inside the same band.
/// </summary>
public enum LuaMBehaviorTier : byte
{
    Disabled,
    Idle,
    Routine,
    Mission,
    Support,
    Combat,
    Safety,
    Critical,
    HardStop,
}

/// <summary>
/// Facts that perception adapters can report to the common behavior kernel.
/// They describe the world, not a requested action.
/// </summary>
public enum LuaMBehaviorStimulus : byte
{
    None,

    // Self state.
    SelfIncapacitated,
    SelfCritical,
    SelfDamaged,
    SelfOnFire,
    Immobilized,
    LowPower,
    LowFuel,
    LowAmmunition,
    Hunger,
    Thirst,

    // Environmental danger.
    UnsafeAtmosphere,
    LowPressure,
    HighPressure,
    ExtremeTemperature,
    RadiationHazard,
    ContaminationHazard,
    ElectricalHazard,
    ExplosionRisk,
    LocalFire,
    HullBreach,
    StructuralFailure,
    PowerFailure,

    // Threats and team state.
    HostileThreat,
    ProjectileThreat,
    Intruder,
    Overwhelmed,
    Isolated,
    ObjectiveThreatened,
    AllyInjured,
    AllyCritical,
    RecoverableDeadAlly,
    EscortRequired,
    ReinforcementsRequested,

    // Navigation and execution state.
    RouteBlocked,
    NoPath,
    TargetLost,
    Stranded,
    SafeAtHome,
    DockingOpportunity,

    // Work and command state.
    DirectOrder,
    WorkOrder,
    PatientWaiting,
    RepairNeeded,
    CleaningNeeded,
    FireNeedsExtinguishing,
    BreachNeedsSealing,
    DeviceNeedsPower,
    ResourceLow,
    ResupplyAvailable,
    DeliveryReady,
    CargoFull,
    MineableResource,
    SalvageTarget,
    PatrolDue,
    InvestigationTarget,
}

/// <summary>
/// Capabilities are positive permissions. Rules cannot select an intent unless
/// the profile provides every capability required by that rule.
/// </summary>
public enum LuaMBehaviorCapability : byte
{
    None,
    SelfPreservation,
    Move,
    Navigate,
    Interact,
    UseHands,
    Carry,
    Pull,
    Communicate,
    Coordinate,
    FollowOrders,
    Breathe,
    PressureVulnerable,
    TemperatureVulnerable,
    RadiationVulnerable,
    FireVulnerable,
    Medical,
    Defibrillate,
    Extinguish,
    Clean,
    Repair,
    RestorePower,
    SealBreach,
    Decontaminate,
    CombatMelee,
    CombatRanged,
    Guard,
    Escort,
    Patrol,
    Recharge,
    Refuel,
    Reload,
    Mine,
    Salvage,
    Haul,
    ShipControl,
    ShipWeapons,
    Dock,
    Stationary,
}

/// <summary>
/// Common high-level intentions. Role executors translate these into HTN
/// compounds, work-order steps, or direct domain-system calls.
/// </summary>
public enum LuaMBehaviorIntent : byte
{
    None,
    Standby,
    AwaitRescue,
    HoldPosition,
    Flee,
    EvacuateHazard,
    SeekSafeAtmosphere,
    ExtinguishSelf,
    SeekMedicalAid,
    Retreat,
    TakeCover,
    EvadeProjectile,
    DefendSelf,
    DefendArea,
    ProtectTarget,
    RequestReinforcements,
    RequestAssistance,
    Rescue,
    Treat,
    Revive,
    EvacuateCasualty,
    ExtinguishFire,
    Clean,
    SealBreach,
    Repair,
    RestorePower,
    Recharge,
    Reload,
    Refuel,
    Resupply,
    ClearRoute,
    ReplanRoute,
    SearchTarget,
    Investigate,
    Patrol,
    Escort,
    FollowOrder,
    ExecuteWorkOrder,
    Deliver,
    Haul,
    Mine,
    Salvage,
    ReturnCargo,
    ReturnHome,
    Dock,
    Navigate,
    EngageShip,
    DisengageShip,
    Report,
    Quarantine,
    Decontaminate,
    Eat,
    Drink,
}

[Serializable, DataDefinition]
public sealed partial class LuaMBehaviorObservation
{
    [DataField]
    public LuaMBehaviorStimulus Stimulus;

    [DataField]
    public float Severity;

    [DataField]
    public float Confidence = 1f;

    [DataField]
    public EntityUid? Target;

    [DataField]
    public EntityCoordinates? Destination;

    [DataField]
    public TimeSpan ObservedAt;

    [DataField]
    public TimeSpan ExpiresAt;

    [DataField]
    public string Source = string.Empty;
}

[Serializable, DataDefinition]
public sealed partial class LuaMBehaviorSignalWeight
{
    [DataField(required: true)]
    public LuaMBehaviorStimulus Stimulus;

    [DataField]
    public float Weight = 1000f;
}

[Serializable, DataDefinition]
public sealed partial class LuaMBehaviorRule
{
    [DataField(required: true)]
    public string Id = string.Empty;

    [DataField(required: true)]
    public LuaMBehaviorIntent Intent;

    [DataField]
    public LuaMBehaviorTier Tier = LuaMBehaviorTier.Routine;

    [DataField]
    public float BaseScore;

    [DataField]
    public List<LuaMBehaviorCapability> RequiredCapabilities = new();

    [DataField]
    public List<LuaMBehaviorCapability> RequiredAnyCapabilities = new();

    [DataField]
    public List<LuaMBehaviorCapability> ForbiddenCapabilities = new();

    [DataField]
    public List<LuaMBehaviorStimulus> RequiredAll = new();

    [DataField]
    public List<LuaMBehaviorStimulus> RequiredAny = new();

    [DataField]
    public List<LuaMBehaviorStimulus> Forbidden = new();

    [DataField]
    public List<LuaMBehaviorSignalWeight> Weights = new();

    /// <summary>
    /// When set, one candidate is generated for every matching target. This is
    /// what lets the same algorithm compare multiple patients or threats.
    /// </summary>
    [DataField]
    public LuaMBehaviorStimulus TargetFrom;

    [DataField]
    public bool RequireTarget;

    [DataField]
    public float MinimumSignal = 0.01f;

    [DataField]
    public float Stickiness = 75f;

    [DataField]
    public float CommitSeconds = 1f;

    [DataField]
    public bool Interruptible = true;

    /// <summary>
    /// Optional activity identifier for <c>LuaMNpcActivityLifecycleSystem</c>.
    /// Empty values leave execution entirely to the role adapter.
    /// </summary>
    [DataField]
    public string Activity = string.Empty;
}

[Prototype("luamBehaviorProfile")]
public sealed partial class LuaMBehaviorProfilePrototype : IPrototype, IInheritingPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<LuaMBehaviorProfilePrototype>))]
    public string[]? Parents { get; private set; }

    [NeverPushInheritance]
    [AbstractDataField]
    public bool Abstract { get; private set; }

    [DataField, AlwaysPushInheritance]
    public List<LuaMBehaviorCapability> Capabilities = new();

    [DataField, AlwaysPushInheritance]
    public List<LuaMBehaviorRule> Rules = new();

    [DataField]
    public LuaMBehaviorIntent DefaultIntent = LuaMBehaviorIntent.Standby;

    [DataField]
    public LuaMBehaviorTier DefaultTier = LuaMBehaviorTier.Idle;

    [DataField]
    public string DefaultActivity = string.Empty;

    [DataField]
    public float DecisionIntervalSeconds = 0.5f;

    [DataField]
    public float SameTierPreemptMargin = 100f;

    [DataField]
    public float PerceptionRange = 12f;

    [DataField]
    public float ThreatTolerance = 2f;

    [DataField]
    public int MaxObservations = 64;

    public bool HasCapability(LuaMBehaviorCapability capability)
    {
        return capability != LuaMBehaviorCapability.None && Capabilities.Contains(capability);
    }
}

[Serializable, DataDefinition]
public sealed partial class LuaMBehaviorDecision
{
    [DataField]
    public LuaMBehaviorIntent Intent = LuaMBehaviorIntent.Standby;

    [DataField]
    public LuaMBehaviorTier Tier = LuaMBehaviorTier.Idle;

    [DataField]
    public float Score;

    [DataField]
    public EntityUid? Target;

    [DataField]
    public EntityCoordinates? Destination;

    [DataField]
    public string RuleId = string.Empty;

    [DataField]
    public string Activity = string.Empty;

    [DataField]
    public string Reason = string.Empty;

    [DataField]
    public TimeSpan DecidedAt;

    [DataField]
    public TimeSpan CommitUntil;

    [DataField]
    public uint Generation;
}
