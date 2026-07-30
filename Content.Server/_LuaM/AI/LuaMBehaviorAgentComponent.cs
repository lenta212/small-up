using System;
using System.Collections.Generic;
using Content.Shared._LuaM.AI;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.AI;

/// <summary>
/// Opt-in carrier for high-level behavior arbitration. Existing domain systems
/// remain responsible for performing the selected intent.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMBehaviorAgentComponent : Component
{
    [DataField(required: true)]
    public ProtoId<LuaMBehaviorProfilePrototype> Profile = string.Empty;

    [DataField]
    public bool BuiltInPerception = true;

    [DataField]
    public bool WriteHtnBlackboard = true;

    [DataField]
    public bool ReplanHtnOnDecision;

    [DataField]
    public bool DriveActivityLifecycle;

    /// <summary>
    /// Domain adapters that must publish a complete observation set atomically
    /// can opt out of the generic periodic loop and call EvaluateNow themselves.
    /// This prevents two independent update cadences from issuing decisions for
    /// the same executor.
    /// </summary>
    [DataField]
    public bool ExternalEvaluationOnly;

    [ViewVariables]
    public readonly List<LuaMBehaviorObservation> Observations = new();

    [ViewVariables]
    public LuaMBehaviorDecision Decision = new();

    [ViewVariables]
    public TimeSpan NextEvaluation;

    [ViewVariables]
    public string LastStatus = "not evaluated";
}

/// <summary>
/// Optional HTN root-task bindings for roles that want the common arbiter to
/// switch complete plans. Most roles only consume the blackboard keys.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMBehaviorHtnBindingComponent : Component
{
    [DataField]
    public Dictionary<LuaMBehaviorIntent, string> IntentTasks = new();

    [DataField]
    public string DefaultTask = string.Empty;

    /// <summary>
    /// Restores the task the actor had before behavior arbitration whenever no
    /// explicit intent binding applies.
    /// </summary>
    [DataField]
    public bool RestoreOriginalTask;

    /// <summary>
    /// Mirrors the validated behavior target into the standard HTN Target and
    /// TargetCoordinates keys for decision-bound executor plans.
    /// </summary>
    [DataField]
    public bool MirrorDecisionTarget;

    [ViewVariables]
    public string OriginalTask = string.Empty;
}

[ByRefEvent]
public readonly record struct LuaMBehaviorDecisionChangedEvent(
    EntityUid Actor,
    LuaMBehaviorDecision Previous,
    LuaMBehaviorDecision Current);
