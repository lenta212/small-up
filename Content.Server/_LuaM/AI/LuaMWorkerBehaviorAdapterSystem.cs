using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Medical.Components;
using Content.Server.NPC.HTN;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Medical;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Repairable;
using Content.Shared.Tools.Components;
using Content.Shared.Tools.Systems;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.AI;

public enum LuaMWorkerRole : byte
{
    Engineer,
    Medic,
}

[RegisterComponent]
public sealed partial class LuaMWorkerBehaviorAdapterComponent : Component
{
    [DataField]
    public LuaMWorkerRole Role;

    [DataField]
    public EntityUid? WorkOrderBoard;

    [DataField]
    public float LeaseSeconds = 20f;

    [DataField]
    public float StallSeconds = 15f;

    [DataField]
    public float InteractionRange = 1.25f;

    [DataField]
    public bool BuiltInPerception = true;

    [ViewVariables]
    public uint ActiveWorkOrderId;

    [ViewVariables]
    public TimeSpan NextEvaluation;

    [ViewVariables]
    public TimeSpan NextAction;

    [ViewVariables]
    public TimeSpan LastProgressAt;

    [ViewVariables]
    public float LastDistance = float.MaxValue;

    [ViewVariables]
    public string Status = "unassigned";
}

/// <summary>
/// Executes bounded repair and treatment work orders with tools already held by
/// a humanoid worker. Selection and safety remain in the common arbiter.
/// </summary>
public sealed partial class LuaMWorkerBehaviorAdapterSystem : EntitySystem
{
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(0.75);
    private static readonly IReadOnlySet<LuaMBehaviorIntent> EngineerIntents =
        new HashSet<LuaMBehaviorIntent> { LuaMBehaviorIntent.Repair };
    private static readonly IReadOnlySet<LuaMBehaviorIntent> MedicIntents =
        new HashSet<LuaMBehaviorIntent> { LuaMBehaviorIntent.Treat };

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;
    [Dependency] private LuaMWorkOrderSystem _workOrders = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMWorkerBehaviorAdapterComponent>();
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
        LuaMWorkerBehaviorAdapterComponent adapter,
        bool force = false)
    {
        if (HasComp<ActorComponent>(uid))
            return false;

        var now = _timing.CurTime;
        if (!force && adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
            return true;

        adapter.NextEvaluation = now + EvaluationInterval;
        ConfigureBehavior(uid, adapter);
        RefreshClaim(uid, adapter, now);

        if (!_behavior.EvaluateNow(uid, out var decision))
            return false;

        ExecuteClaim(uid, adapter, decision, now);
        return true;
    }

    private void ConfigureBehavior(EntityUid uid, LuaMWorkerBehaviorAdapterComponent adapter)
    {
        var agent = EnsureComp<LuaMBehaviorAgentComponent>(uid);
        var profile = adapter.Role == LuaMWorkerRole.Engineer
            ? "LuaMEngineerBehavior"
            : "LuaMMedicBehavior";
        if (agent.Profile != profile)
        {
            agent.Profile = profile;
            agent.NextEvaluation = TimeSpan.Zero;
        }

        agent.BuiltInPerception = adapter.BuiltInPerception;
        agent.WriteHtnBlackboard = true;

        var htn = EnsureComp<HTNComponent>(uid);
        htn.RootTask ??= new HTNCompoundTask { Task = "IdleCompound" };

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
        binding.IntentTasks[LuaMBehaviorIntent.SeekSafeAtmosphere] = "LuaMBehaviorRetreatCompound";
        binding.IntentTasks[LuaMBehaviorIntent.AwaitRescue] = "IdleCompound";
        binding.IntentTasks[LuaMBehaviorIntent.HoldPosition] = "IdleCompound";
        binding.IntentTasks[LuaMBehaviorIntent.RequestAssistance] = "IdleCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Repair] = "LuaMBehaviorNavigateDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Treat] = "LuaMBehaviorNavigateDecisionCompound";
    }

    private void RefreshClaim(
        EntityUid uid,
        LuaMWorkerBehaviorAdapterComponent adapter,
        TimeSpan now)
    {
        if (adapter.WorkOrderBoard is not { Valid: true } board ||
            TerminatingOrDeleted(board) ||
            !HasComp<LuaMWorkOrderBoardComponent>(board))
        {
            Reset(adapter, "no work-order board");
            return;
        }

        var lease = TimeSpan.FromSeconds(Math.Clamp(adapter.LeaseSeconds, 2f, 300f));
        if (adapter.ActiveWorkOrderId != 0)
        {
            if (!_workOrders.TryGetClaimedOrder(board, adapter.ActiveWorkOrderId, uid, out _) ||
                !_workOrders.Renew(board, adapter.ActiveWorkOrderId, uid, lease))
            {
                Reset(adapter, "work order no longer claimed");
            }

            return;
        }

        var allowed = adapter.Role == LuaMWorkerRole.Engineer ? EngineerIntents : MedicIntents;
        if (!_workOrders.TryClaimBest(board, uid, lease, out var order, allowed) || order == null)
            return;

        adapter.ActiveWorkOrderId = order.Id;
        adapter.LastProgressAt = now;
        adapter.LastDistance = float.MaxValue;
        adapter.Status = $"claimed order {order.Id} ({order.Intent})";
    }

    private void ExecuteClaim(
        EntityUid uid,
        LuaMWorkerBehaviorAdapterComponent adapter,
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
            CancelCurrentWorkAction(uid, adapter.Role, order.Target);
            adapter.LastProgressAt = now;
            adapter.Status = $"order {order.Id} paused by {decision.Intent}";
            return;
        }

        if (order.Target is not { Valid: true } target || TerminatingOrDeleted(target))
        {
            Release(uid, adapter, board, order, "target unavailable");
            return;
        }

        if (IsComplete(adapter.Role, target))
        {
            Complete(uid, adapter, board, order);
            return;
        }

        var destination = order.Destination ?? Transform(target).Coordinates;
        if (!Transform(uid).Coordinates.TryDistance(
                EntityManager,
                _transform,
                destination,
                out var distance))
        {
            Release(uid, adapter, board, order, "destination unavailable");
            return;
        }

        var interactionRange = Math.Clamp(adapter.InteractionRange, 0.25f, 3f);
        if (distance > interactionRange)
        {
            UpdateRouteProgress(uid, adapter, board, order, distance, now);
            return;
        }

        adapter.LastProgressAt = now;
        adapter.LastDistance = distance;
        if (HasComp<ActiveDoAfterComponent>(uid))
        {
            adapter.Status = $"executing order {order.Id}; interaction in progress";
            return;
        }

        if (now < adapter.NextAction)
            return;

        adapter.NextAction = now + TimeSpan.FromSeconds(0.75);
        bool handled;
        string status;
        switch (adapter.Role)
        {
            case LuaMWorkerRole.Engineer:
                handled = TryRepair(uid, target, out status);
                break;
            case LuaMWorkerRole.Medic:
                handled = TryTreat(uid, target, out status);
                break;
            default:
                handled = false;
                status = "unsupported worker role";
                break;
        }

        if (!handled)
        {
            Release(uid, adapter, board, order, status);
            return;
        }

        adapter.Status = $"executing order {order.Id}: {status}";
    }

    private void CancelCurrentWorkAction(
        EntityUid uid,
        LuaMWorkerRole role,
        EntityUid? target)
    {
        if (!TryComp<DoAfterComponent>(uid, out var doAfters))
            return;

        var ids = new List<DoAfterId>();
        foreach (var doAfter in doAfters.DoAfters.Values)
        {
            if (doAfter.Cancelled ||
                doAfter.Completed ||
                target != null && doAfter.Args.Target != target)
            {
                continue;
            }

            var isCurrentWork = role switch
            {
                LuaMWorkerRole.Engineer => IsRepairDoAfter(doAfter.Args, target),
                LuaMWorkerRole.Medic => doAfter.Args.Event is HealingDoAfterEvent,
                _ => false,
            };
            if (isCurrentWork)
                ids.Add(doAfter.Id);
        }

        foreach (var id in ids)
            _doAfter.Cancel(id);
    }

    private bool IsRepairDoAfter(DoAfterArgs args, EntityUid? target)
    {
        if (target is not { Valid: true } repairTarget ||
            args.Used is not { Valid: true } used ||
            !TryComp<RepairableComponent>(repairTarget, out var repairable) ||
            !TryComp<ToolComponent>(used, out var tool))
        {
            return false;
        }

        return repairable.Qualities.Any(quality => _tools.HasQuality(used, quality, tool));
    }

    private void UpdateRouteProgress(
        EntityUid uid,
        LuaMWorkerBehaviorAdapterComponent adapter,
        EntityUid board,
        LuaMWorkOrder order,
        float distance,
        TimeSpan now)
    {
        if (distance + 0.2f < adapter.LastDistance)
        {
            adapter.LastDistance = distance;
            adapter.LastProgressAt = now;
            adapter.Status = $"approaching order {order.Id}; distance={distance:F1}";
            return;
        }

        if (adapter.LastProgressAt == TimeSpan.Zero)
            adapter.LastProgressAt = now;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(adapter.StallSeconds, 3f, 120f));
        if (now - adapter.LastProgressAt >= timeout)
            Release(uid, adapter, board, order, "route stalled");
    }

    private bool TryRepair(EntityUid uid, EntityUid target, out string status)
    {
        status = "target is not repairable";
        if (!TryComp<RepairableComponent>(target, out var repairable) ||
            !TryComp<HandsComponent>(uid, out var hands))
        {
            return false;
        }

        foreach (var item in _hands.EnumerateHeld(uid, hands))
        {
            if (!TryComp<ToolComponent>(item, out var tool) ||
                !repairable.Qualities.Any(quality => _tools.HasQuality(item, quality, tool)))
            {
                continue;
            }

            SetActiveHand(uid, item, hands);
            if (!_interaction.InteractUsing(uid, item, target, Transform(target).Coordinates))
            {
                status = "repair interaction rejected";
                return false;
            }

            status = $"repair started with {ToPrettyString(item)}";
            return true;
        }

        status = "no compatible repair tool in hands";
        return false;
    }

    private bool TryTreat(EntityUid uid, EntityUid target, out string status)
    {
        status = "target is not medically treatable";
        if (!TryComp<DamageableComponent>(target, out var damage) ||
            !TryComp<HandsComponent>(uid, out var hands))
        {
            return false;
        }

        foreach (var item in _hands.EnumerateHeld(uid, hands))
        {
            if (!TryComp<HealingComponent>(item, out var healing) ||
                !CanHeal(damage, healing))
            {
                continue;
            }

            SetActiveHand(uid, item, hands);
            if (!_interaction.InteractUsing(uid, item, target, Transform(target).Coordinates))
            {
                status = "medical interaction rejected";
                return false;
            }

            status = $"treatment started with {ToPrettyString(item)}";
            return true;
        }

        status = "no effective medical item in hands";
        return false;
    }

    private static bool CanHeal(DamageableComponent damage, HealingComponent healing)
    {
        if (healing.DamageContainers is not null &&
            damage.DamageContainerID is not null &&
            !healing.DamageContainers.Contains(damage.DamageContainerID))
        {
            return false;
        }

        foreach (var (damageType, amount) in healing.Damage.DamageDict)
        {
            if (amount >= 0 ||
                !damage.Damage.DamageDict.TryGetValue(damageType, out var current) ||
                current <= 0)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void SetActiveHand(EntityUid uid, EntityUid item, HandsComponent hands)
    {
        if (_hands.IsHolding(uid, item, out var hand, hands))
            _hands.TrySetActiveHand(uid, hand.Name, hands);
    }

    private bool IsComplete(LuaMWorkerRole role, EntityUid target)
    {
        if (!TryComp<DamageableComponent>(target, out var damage) || damage.TotalDamage > 0)
            return false;

        return role != LuaMWorkerRole.Medic ||
               !TryComp<MobStateComponent>(target, out var mobState) ||
               mobState.CurrentState == MobState.Alive;
    }

    private void Complete(
        EntityUid uid,
        LuaMWorkerBehaviorAdapterComponent adapter,
        EntityUid board,
        LuaMWorkOrder order)
    {
        if (!_workOrders.Complete(board, order.Id, uid))
            return;

        Reset(adapter, $"order {order.Id} completed");
        _behavior.EvaluateNow(uid, out _);
    }

    private void Release(
        EntityUid uid,
        LuaMWorkerBehaviorAdapterComponent adapter,
        EntityUid board,
        LuaMWorkOrder order,
        string failure)
    {
        _workOrders.Release(board, order.Id, uid, failure);
        Reset(adapter, $"order {order.Id} released: {failure}");
        _behavior.EvaluateNow(uid, out _);
    }

    private static void Reset(LuaMWorkerBehaviorAdapterComponent adapter, string status)
    {
        adapter.ActiveWorkOrderId = 0;
        adapter.NextAction = TimeSpan.Zero;
        adapter.LastProgressAt = TimeSpan.Zero;
        adapter.LastDistance = float.MaxValue;
        adapter.Status = status;
    }
}
