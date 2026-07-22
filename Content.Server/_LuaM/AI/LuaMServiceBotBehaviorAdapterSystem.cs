using System;
using System.Collections.Generic;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Damage;
using Content.Shared.Fluids.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Silicons.Bots;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.AI;

public enum LuaMServiceBotRole : byte
{
    General,
    Fire,
    Medical,
    Cleaning,
    Taxi,
    Supply,
    Entertainment,
}

[RegisterComponent]
public sealed partial class LuaMServiceBotBehaviorAdapterComponent : Component
{
    [DataField]
    public LuaMServiceBotRole Role;

    [DataField]
    public EntityUid? WorkOrderBoard;

    [DataField]
    public float WorkOrderLeaseSeconds = 20f;

    [DataField]
    public float WorkOrderStallSeconds = 15f;

    [DataField]
    public float WorkOrderArrivalRange = 1.25f;

    [ViewVariables]
    public TimeSpan NextEvaluation;

    [ViewVariables]
    public uint ActiveWorkOrderId;

    [ViewVariables]
    public float LastWorkOrderDistance = float.MaxValue;

    [ViewVariables]
    public TimeSpan LastWorkOrderProgressAt;

    [ViewVariables]
    public string WorkOrderStatus = "unassigned";
}

/// <summary>
/// Converts the actionable state already understood by stock service bots into
/// common behavior observations, then binds the selected intent back to their
/// existing HTN compounds.
/// </summary>
public sealed partial class LuaMServiceBotBehaviorAdapterSystem : EntitySystem
{
    private const string ObservationSource = "service-bot:domain";
    private const float PerceptionRange = 10f;
    private const int MaximumTargets = 32;
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ObservationTtl = TimeSpan.FromSeconds(3);
    private static readonly IReadOnlySet<LuaMBehaviorIntent> GeneralWorkIntents =
        new HashSet<LuaMBehaviorIntent>
        {
            LuaMBehaviorIntent.FollowOrder,
            LuaMBehaviorIntent.Investigate,
            LuaMBehaviorIntent.Patrol,
        };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> FireWorkIntents =
        new HashSet<LuaMBehaviorIntent> { LuaMBehaviorIntent.ExtinguishFire };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> MedicalWorkIntents =
        new HashSet<LuaMBehaviorIntent> { LuaMBehaviorIntent.Treat };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> CleaningWorkIntents =
        new HashSet<LuaMBehaviorIntent> { LuaMBehaviorIntent.Clean };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> TaxiWorkIntents =
        new HashSet<LuaMBehaviorIntent>
        {
            LuaMBehaviorIntent.Navigate,
            LuaMBehaviorIntent.FollowOrder,
        };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> SupplyWorkIntents =
        new HashSet<LuaMBehaviorIntent>
        {
            LuaMBehaviorIntent.Deliver,
            LuaMBehaviorIntent.Haul,
            LuaMBehaviorIntent.ReturnCargo,
        };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> NoWorkIntents =
        new HashSet<LuaMBehaviorIntent>();

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;
    [Dependency] private LuaMWorkOrderSystem _workOrders = default!;
    [Dependency] private MedibotSystem _medibot = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMServiceBotBehaviorAdapterComponent>();
        while (query.MoveNext(out var uid, out var adapter))
        {
            if (HasComp<ActorComponent>(uid) ||
                adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
            {
                continue;
            }

            RefreshNow(uid, adapter);
        }
    }

    public bool RefreshNow(
        EntityUid uid,
        LuaMServiceBotBehaviorAdapterComponent adapter,
        bool force = false)
    {
        if (HasComp<ActorComponent>(uid))
            return false;

        var now = _timing.CurTime;
        if (!force && adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
            return true;

        adapter.NextEvaluation = now + EvaluationInterval;
        ConfigureBehavior(uid, adapter.Role);
        _behavior.ClearObservations(uid, source: ObservationSource);
        RefreshWorkOrderClaim(uid, adapter, now);

        switch (adapter.Role)
        {
            case LuaMServiceBotRole.Fire:
                ObserveFires(uid);
                break;
            case LuaMServiceBotRole.Medical:
                ObservePatients(uid);
                break;
            case LuaMServiceBotRole.Cleaning:
                ObservePuddles(uid);
                break;
            default:
                Report(uid, LuaMBehaviorStimulus.PatrolDue, 0.25f);
                break;
        }

        if (!_behavior.EvaluateNow(uid, out var decision))
            return false;

        UpdateWorkOrderExecution(uid, adapter, decision, now);
        return true;
    }

    private void ConfigureBehavior(EntityUid uid, LuaMServiceBotRole role)
    {
        var agent = EnsureComp<LuaMBehaviorAgentComponent>(uid);
        var profile = role switch
        {
            LuaMServiceBotRole.Fire => "LuaMFireBotBehavior",
            LuaMServiceBotRole.Medical => "LuaMMedibotBehavior",
            LuaMServiceBotRole.Cleaning => "LuaMCleanBotBehavior",
            LuaMServiceBotRole.Taxi => "LuaMTaxiBotBehavior",
            LuaMServiceBotRole.Supply => "LuaMSupplyBotBehavior",
            LuaMServiceBotRole.Entertainment => "LuaMEntertainmentBotBehavior",
            _ => "LuaMServiceRobotBehavior",
        };
        if (agent.Profile != profile)
        {
            agent.Profile = profile;
            agent.NextEvaluation = TimeSpan.Zero;
        }

        agent.BuiltInPerception = true;
        agent.WriteHtnBlackboard = true;

        var binding = EnsureComp<LuaMBehaviorHtnBindingComponent>(uid);
        binding.DefaultTask = string.Empty;
        binding.RestoreOriginalTask = true;
        binding.MirrorDecisionTarget = true;
        binding.IntentTasks.Clear();
        binding.IntentTasks[LuaMBehaviorIntent.Flee] = "LuaMBehaviorRetreatCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Retreat] = "LuaMBehaviorRetreatCompound";
        binding.IntentTasks[LuaMBehaviorIntent.TakeCover] = "LuaMBehaviorRetreatCompound";
        binding.IntentTasks[LuaMBehaviorIntent.EvadeProjectile] = "LuaMBehaviorRetreatCompound";
        binding.IntentTasks[LuaMBehaviorIntent.EvacuateHazard] = "LuaMBehaviorRetreatCompound";
        binding.IntentTasks[LuaMBehaviorIntent.AwaitRescue] = "IdleCompound";
        binding.IntentTasks[LuaMBehaviorIntent.HoldPosition] = "IdleCompound";
        binding.IntentTasks[LuaMBehaviorIntent.RequestAssistance] = "IdleCompound";
        binding.IntentTasks[LuaMBehaviorIntent.FollowOrder] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.ExecuteWorkOrder] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Deliver] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Haul] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.ReturnCargo] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Navigate] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Investigate] = "LuaMBehaviorNavigateDecisionCompound";

        switch (role)
        {
            case LuaMServiceBotRole.Fire:
                binding.IntentTasks[LuaMBehaviorIntent.ExtinguishFire] = "FirebotCompound";
                break;
            case LuaMServiceBotRole.Medical:
                binding.IntentTasks[LuaMBehaviorIntent.Treat] = "MedibotCompound";
                break;
            case LuaMServiceBotRole.Cleaning:
                binding.IntentTasks[LuaMBehaviorIntent.Clean] = "CleanbotCompound";
                break;
        }
    }

    private void RefreshWorkOrderClaim(
        EntityUid uid,
        LuaMServiceBotBehaviorAdapterComponent adapter,
        TimeSpan now)
    {
        if (adapter.WorkOrderBoard is not { Valid: true } board ||
            TerminatingOrDeleted(board) ||
            !HasComp<LuaMWorkOrderBoardComponent>(board))
        {
            ResetWorkOrder(adapter, "no work-order board");
            return;
        }

        var lease = TimeSpan.FromSeconds(Math.Clamp(adapter.WorkOrderLeaseSeconds, 2f, 300f));
        if (adapter.ActiveWorkOrderId != 0)
        {
            if (!_workOrders.TryGetClaimedOrder(board, adapter.ActiveWorkOrderId, uid, out _))
            {
                ResetWorkOrder(adapter, "work order no longer claimed");
                return;
            }

            if (!_workOrders.Renew(board, adapter.ActiveWorkOrderId, uid, lease))
            {
                ResetWorkOrder(adapter, "work-order lease renewal failed");
                return;
            }

            return;
        }

        var allowed = GetAllowedWorkIntents(adapter.Role);
        if (allowed.Count == 0 ||
            !_workOrders.TryClaimBest(board, uid, lease, out var order, allowed) ||
            order == null)
        {
            return;
        }

        adapter.ActiveWorkOrderId = order.Id;
        adapter.LastWorkOrderDistance = float.MaxValue;
        adapter.LastWorkOrderProgressAt = now;
        adapter.WorkOrderStatus = $"claimed order {order.Id} ({order.Intent})";
    }

    private void UpdateWorkOrderExecution(
        EntityUid uid,
        LuaMServiceBotBehaviorAdapterComponent adapter,
        LuaMBehaviorDecision decision,
        TimeSpan now)
    {
        if (adapter.ActiveWorkOrderId == 0 ||
            adapter.WorkOrderBoard is not { Valid: true } board ||
            !_workOrders.TryGetClaimedOrder(board, adapter.ActiveWorkOrderId, uid, out var order) ||
            order == null)
        {
            return;
        }

        if (decision.Tier >= LuaMBehaviorTier.Safety)
        {
            adapter.LastWorkOrderProgressAt = now;
            adapter.WorkOrderStatus = $"order {order.Id} paused by {decision.Intent}";
            return;
        }

        if (IsRoleWorkComplete(uid, adapter.Role, order))
        {
            CompleteWorkOrder(uid, adapter, board, order, "domain task completed");
            return;
        }

        if (!TryGetWorkCoordinates(order, out var destination) ||
            !Transform(uid).Coordinates.TryDistance(
                EntityManager,
                _transform,
                destination,
                out var distance))
        {
            ReleaseWorkOrder(uid, adapter, board, order, "destination unavailable");
            return;
        }

        var arrivalRange = Math.Clamp(adapter.WorkOrderArrivalRange, 0.25f, 5f);
        if (CompletesOnArrival(adapter.Role, order.Intent) && distance <= arrivalRange)
        {
            CompleteWorkOrder(uid, adapter, board, order, "destination reached");
            return;
        }

        if (distance + 0.2f < adapter.LastWorkOrderDistance)
        {
            adapter.LastWorkOrderDistance = distance;
            adapter.LastWorkOrderProgressAt = now;
            adapter.WorkOrderStatus = $"executing order {order.Id}; distance={distance:F1}";
            return;
        }

        var stall = TimeSpan.FromSeconds(Math.Clamp(adapter.WorkOrderStallSeconds, 3f, 120f));
        if (adapter.LastWorkOrderProgressAt == TimeSpan.Zero)
            adapter.LastWorkOrderProgressAt = now;
        if (now - adapter.LastWorkOrderProgressAt < stall)
            return;

        ReleaseWorkOrder(uid, adapter, board, order, "route stalled");
    }

    private bool IsRoleWorkComplete(
        EntityUid uid,
        LuaMServiceBotRole role,
        LuaMWorkOrder order)
    {
        if (order.Target is not { Valid: true } target || TerminatingOrDeleted(target))
            return false;

        switch (role)
        {
            case LuaMServiceBotRole.Fire:
                return !TryComp<FlammableComponent>(target, out var flammable) || !flammable.OnFire;
            case LuaMServiceBotRole.Cleaning:
                return !HasComp<PuddleComponent>(target);
            case LuaMServiceBotRole.Medical:
                if (!TryComp<MedibotComponent>(uid, out var medibot) ||
                    !TryComp<MobStateComponent>(target, out var mobState) ||
                    !TryComp<DamageableComponent>(target, out var damage) ||
                    !_medibot.TryGetTreatment(medibot, mobState.CurrentState, out var treatment))
                {
                    return true;
                }

                return !treatment.IsValid(damage.TotalDamage);
            default:
                return false;
        }
    }

    private bool TryGetWorkCoordinates(LuaMWorkOrder order, out EntityCoordinates destination)
    {
        if (order.Destination is { } explicitDestination)
        {
            destination = explicitDestination;
            return true;
        }

        if (order.Target is { Valid: true } target && !TerminatingOrDeleted(target))
        {
            destination = Transform(target).Coordinates;
            return true;
        }

        destination = EntityCoordinates.Invalid;
        return false;
    }

    private void CompleteWorkOrder(
        EntityUid uid,
        LuaMServiceBotBehaviorAdapterComponent adapter,
        EntityUid board,
        LuaMWorkOrder order,
        string status)
    {
        if (!_workOrders.Complete(board, order.Id, uid))
            return;

        ResetWorkOrder(adapter, $"order {order.Id} completed: {status}");
        _behavior.EvaluateNow(uid, out _);
    }

    private void ReleaseWorkOrder(
        EntityUid uid,
        LuaMServiceBotBehaviorAdapterComponent adapter,
        EntityUid board,
        LuaMWorkOrder order,
        string failure)
    {
        _workOrders.Release(board, order.Id, uid, failure);
        ResetWorkOrder(adapter, $"order {order.Id} released: {failure}");
        _behavior.EvaluateNow(uid, out _);
    }

    private static bool CompletesOnArrival(LuaMServiceBotRole role, LuaMBehaviorIntent intent)
    {
        return role switch
        {
            LuaMServiceBotRole.Taxi => intent is LuaMBehaviorIntent.Navigate or LuaMBehaviorIntent.FollowOrder,
            LuaMServiceBotRole.Supply => intent is LuaMBehaviorIntent.Deliver or
                LuaMBehaviorIntent.Haul or
                LuaMBehaviorIntent.ReturnCargo,
            LuaMServiceBotRole.Entertainment or LuaMServiceBotRole.General =>
                intent is LuaMBehaviorIntent.FollowOrder or
                    LuaMBehaviorIntent.Investigate or
                    LuaMBehaviorIntent.Patrol,
            _ => false,
        };
    }

    private static IReadOnlySet<LuaMBehaviorIntent> GetAllowedWorkIntents(LuaMServiceBotRole role)
    {
        return role switch
        {
            LuaMServiceBotRole.General or LuaMServiceBotRole.Entertainment => GeneralWorkIntents,
            LuaMServiceBotRole.Fire => FireWorkIntents,
            LuaMServiceBotRole.Medical => MedicalWorkIntents,
            LuaMServiceBotRole.Cleaning => CleaningWorkIntents,
            LuaMServiceBotRole.Taxi => TaxiWorkIntents,
            LuaMServiceBotRole.Supply => SupplyWorkIntents,
            _ => NoWorkIntents,
        };
    }

    private static void ResetWorkOrder(
        LuaMServiceBotBehaviorAdapterComponent adapter,
        string status)
    {
        adapter.ActiveWorkOrderId = 0;
        adapter.LastWorkOrderDistance = float.MaxValue;
        adapter.LastWorkOrderProgressAt = TimeSpan.Zero;
        adapter.WorkOrderStatus = status;
    }

    private void ObserveFires(EntityUid uid)
    {
        var count = 0;
        foreach (var target in _lookup.GetEntitiesInRange(uid, PerceptionRange))
        {
            if (target == uid ||
                TerminatingOrDeleted(target) ||
                !TryComp<FlammableComponent>(target, out var flammable) ||
                !flammable.OnFire)
            {
                continue;
            }

            ReportTarget(uid, LuaMBehaviorStimulus.FireNeedsExtinguishing, 0.9f, target);
            if (++count >= MaximumTargets)
                break;
        }
    }

    private void ObservePuddles(EntityUid uid)
    {
        var count = 0;
        foreach (var target in _lookup.GetEntitiesInRange<PuddleComponent>(Transform(uid).Coordinates, PerceptionRange))
        {
            if (TerminatingOrDeleted(target.Owner))
                continue;

            ReportTarget(uid, LuaMBehaviorStimulus.CleaningNeeded, 0.55f, target.Owner);
            if (++count >= MaximumTargets)
                break;
        }
    }

    private void ObservePatients(EntityUid uid)
    {
        if (!TryComp<MedibotComponent>(uid, out var medibot))
            return;

        var count = 0;
        foreach (var target in _lookup.GetEntitiesInRange(uid, PerceptionRange))
        {
            if (target == uid ||
                TerminatingOrDeleted(target) ||
                HasComp<NPCRecentlyInjectedComponent>(target) ||
                !HasComp<InjectableSolutionComponent>(target) ||
                !TryComp<MobStateComponent>(target, out var mobState) ||
                !TryComp<DamageableComponent>(target, out var damage) ||
                !_medibot.TryGetTreatment(medibot, mobState.CurrentState, out var treatment) ||
                !treatment.IsValid(damage.TotalDamage) ||
                _factions.IsEntityHostile(uid, target))
            {
                continue;
            }

            var critical = mobState.CurrentState == MobState.Critical;
            var severity = critical
                ? 1f
                : Math.Clamp((float) damage.TotalDamage / 100f, 0.1f, 0.9f);
            ReportTarget(
                uid,
                critical ? LuaMBehaviorStimulus.AllyCritical : LuaMBehaviorStimulus.AllyInjured,
                severity,
                target);
            if (++count >= MaximumTargets)
                break;
        }
    }

    private void ReportTarget(
        EntityUid uid,
        LuaMBehaviorStimulus stimulus,
        float severity,
        EntityUid target)
    {
        Report(uid, stimulus, severity, target, Transform(target).Coordinates);
    }

    private void Report(
        EntityUid uid,
        LuaMBehaviorStimulus stimulus,
        float severity,
        EntityUid? target = null,
        EntityCoordinates? destination = null)
    {
        _behavior.ReportObservation(
            uid,
            stimulus,
            severity,
            target: target,
            destination: destination,
            ttl: ObservationTtl,
            source: ObservationSource);
    }
}
