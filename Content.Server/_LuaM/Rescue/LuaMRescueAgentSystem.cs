using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server.Bed.Components;
using Content.Server.Buckle.Systems;
using Content.Server.Mind;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Silicons.Bots;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueAgentSystem : EntitySystem
{
    private const string RescueAgentPrototype = "LuaMRescueAgent";
    private static readonly Vector2 SpawnOffset = new(1.25f, 0f);

    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MedibotSystem _medibot = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly BuckleSystem _buckle = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LuaMRescueAgentComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var rescue, out var htn))
        {
            if (!rescue.AutoAcquireTargets ||
                HasComp<ActorComponent>(uid))
                continue;

            rescue.TargetRefreshAccumulator += frameTime;
            if (rescue.TargetRefreshAccumulator < rescue.TargetRefreshInterval)
                continue;

            rescue.TargetRefreshAccumulator = 0f;
            UpdateAssignedTarget(uid, rescue, htn);
        }
    }

    public EntityUid SpawnAgent(EntityUid anchor, EntityUid? followTarget, ICommonSession? controller, bool control)
    {
        var spawnCoordinates = Transform(anchor).Coordinates.Offset(SpawnOffset);
        var agent = Spawn(RescueAgentPrototype, spawnCoordinates);
        var rescue = EnsureComp<LuaMRescueAgentComponent>(agent);

        if (followTarget is { Valid: true } target &&
            TryComp<HTNComponent>(agent, out var htn))
        {
            SetFollowTarget(agent, rescue, htn, target);
        }

        if (control && controller != null)
            _mind.ControlMob(controller.UserId, agent);

        return agent;
    }

    public List<string> BuildRescueStatusLines()
    {
        var lines = new List<string>();
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescue))
        {
            PruneSkippedTargets(rescue);
            lines.Add(BuildRescueStatusLine(uid, rescue));
        }

        return lines;
    }

    public bool TryOrderAgent(EntityUid agent, EntityUid? target, out string status)
    {
        status = string.Empty;

        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue) ||
            !TryComp<HTNComponent>(agent, out var htn))
        {
            status = $"{FormatEntityRef(agent)} is not a LuaM rescue agent.";
            return false;
        }

        if (HasComp<ActorComponent>(agent))
        {
            status = $"{FormatEntityRef(agent)} is under manual player control.";
            return false;
        }

        if (target is not { Valid: true } targetUid)
        {
            if (rescue.EvacuatingTarget is { Valid: true } previousTarget &&
                !Deleted(previousTarget))
            {
                StopPullingTarget(agent, previousTarget);
            }

            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(agent, rescue, htn);
            status = $"{FormatEntityRef(agent)} cleared current rescue order and is returning to standby.";
            return true;
        }

        if (Deleted(targetUid))
        {
            status = $"Target {targetUid} is deleted.";
            return false;
        }

        if (rescue.EvacuatingTarget is { Valid: true } previous &&
            previous != targetUid &&
            !Deleted(previous))
        {
            StopPullingTarget(agent, previous);
        }

        rescue.SkippedTargets.Remove(targetUid);
        ResetTargetProgress(rescue);

        if (TryStartOrContinueEvacuation(agent, rescue, htn, targetUid))
        {
            status = $"{FormatEntityRef(agent)} ordered to rescue {FormatEntityRef(targetUid)}.";
            return true;
        }

        SetFollowTarget(agent, rescue, htn, targetUid);
        status = $"{FormatEntityRef(agent)} ordered to follow {FormatEntityRef(targetUid)}.";
        return true;
    }

    private string BuildRescueStatusLine(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        var target = rescue.EvacuatingTarget ?? rescue.AssignedTarget;
        var route = rescue.ShuttleReturnRouted
            ? "home"
            : rescue.ShuttleRoutedTarget is { Valid: true } routedTarget && !Deleted(routedTarget)
                ? $"target:{FormatEntityRef(routedTarget)}"
                : "none";

        return $"{FormatEntityRef(uid)} phase={GetRescuePhase(uid, rescue)}; " +
               $"target={FormatEntityRef(target)}; shuttle={FormatEntityRef(rescue.AssignedShuttle)}; " +
               $"bed={FormatEntityRef(rescue.AssignedPatientStrap)}; route={route}; " +
               $"skipped={rescue.SkippedTargets.Count}; {FormatProgress(rescue)}";
    }

    private string GetRescuePhase(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (HasComp<ActorComponent>(uid))
            return "manual-control";

        if (!rescue.AutoAcquireTargets)
            return "manual";

        if (rescue.EvacuatingTarget is { Valid: true } target &&
            !Deleted(target))
        {
            if (IsBuckledToAssignedShuttlePatientStrap(target, rescue) ||
                IsOnAssignedShuttle(target, rescue) &&
                rescue.AssignedPatientStrap is not { Valid: true })
            {
                return "delivered";
            }

            if (IsPullingTarget(uid, target))
            {
                return rescue.AssignedPatientStrap is { Valid: true } patientStrap && !Deleted(patientStrap)
                    ? "deliver-to-bed"
                    : "return-to-shuttle";
            }

            return "approach-patient";
        }

        if (rescue.AssignedTarget is { Valid: true } assigned &&
            !Deleted(assigned))
        {
            return "follow-target";
        }

        if (rescue.AssignedShuttle is { Valid: true } shuttle &&
            !Deleted(shuttle))
        {
            return IsOnAssignedShuttle(uid, rescue)
                ? "standby-on-shuttle"
                : "standby-return-to-shuttle";
        }

        if (rescue.ShuttleReturnRouted)
            return "shuttle-return-home";

        if (rescue.ShuttleRoutedTarget is { Valid: true } routedTarget &&
            !Deleted(routedTarget))
        {
            return "shuttle-routing-to-target";
        }

        return "idle";
    }

    private string FormatProgress(LuaMRescueAgentComponent rescue)
    {
        var distance = float.IsPositiveInfinity(rescue.LastProgressDistance)
            ? "n/a"
            : $"{rescue.LastProgressDistance:0.0}";

        return $"progressGoal={FormatEntityRef(rescue.ProgressGoal)}; " +
               $"distance={distance}; stall={rescue.TargetStallAccumulator:0.0}/{rescue.TargetStallSeconds:0.0}s";
    }

    private string FormatEntityRef(EntityUid? uid)
    {
        if (uid is not { Valid: true } entity)
            return "none";

        if (Deleted(entity))
            return $"{entity}:deleted";

        return $"{GetNetEntity(entity)}:{Name(entity)}";
    }

    private void UpdateAssignedTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        PruneSkippedTargets(rescue);

        if (UpdateEvacuation(uid, rescue, htn))
            return;

        if (rescue.AssignedTarget is { Valid: true } assigned &&
            !IsTargetTemporarilySkipped(assigned, rescue) &&
            TryStartOrContinueEvacuation(uid, rescue, htn, assigned))
        {
            return;
        }

        if (!TryComp<MedibotComponent>(uid, out var medibot))
        {
            ClearFollowTarget(uid, rescue, htn);
            return;
        }

        if (rescue.AssignedTarget is { Valid: true } current &&
            !IsTargetTemporarilySkipped(current, rescue) &&
            IsRescueCandidate(uid, current, medibot, requireRange: false, rescue.SearchRange, out _))
        {
            if (TryStartOrContinueEvacuation(uid, rescue, htn, current))
                return;

            SetFollowTarget(uid, rescue, htn, current);
            return;
        }

        if (TryFindRescueTarget(uid, rescue.SearchRange, rescue, medibot, out var target))
        {
            if (TryStartOrContinueEvacuation(uid, rescue, htn, target))
                return;

            SetFollowTarget(uid, rescue, htn, target);
            return;
        }

        if (TryFindEvacuationTarget(uid, rescue.SearchRange, rescue, out var evacuationTarget) &&
            TryStartOrContinueEvacuation(uid, rescue, htn, evacuationTarget))
        {
            return;
        }

        StandbyAtAssignedShuttle(uid, rescue, htn);
    }

    private bool TryFindRescueTarget(
        EntityUid uid,
        float searchRange,
        LuaMRescueAgentComponent rescue,
        MedibotComponent medibot,
        out EntityUid target)
    {
        target = default;
        var bestScore = float.MinValue;

        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (IsTargetTemporarilySkipped(candidate, rescue))
                continue;

            if (!IsRescueCandidate(uid, candidate, medibot, requireRange: true, searchRange, out var score))
                continue;

            if (score <= bestScore)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
    }

    private bool TryFindEvacuationTarget(
        EntityUid uid,
        float searchRange,
        LuaMRescueAgentComponent rescue,
        out EntityUid target,
        EntityUid? excludedTarget = null)
    {
        target = default;
        var bestScore = float.MinValue;

        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (candidate == excludedTarget)
                continue;

            if (IsTargetTemporarilySkipped(candidate, rescue))
                continue;

            if (!IsEvacuationCandidate(uid, candidate, rescue, searchRange, out var score))
                continue;

            if (score <= bestScore)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
    }

    private bool UpdateEvacuation(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!CanUseAssignedShuttle(rescue))
        {
            rescue.EvacuatingTarget = null;
            return false;
        }

        if (rescue.EvacuatingTarget is not { Valid: true } target ||
            Deleted(target))
        {
            ResetTargetProgress(rescue);
            rescue.EvacuatingTarget = null;
            return false;
        }

        if (IsTargetTemporarilySkipped(target, rescue))
        {
            StopPullingTarget(uid, target);
            rescue.EvacuatingTarget = null;
            rescue.AssignedTarget = null;
            rescue.AssignedPatientStrap = null;
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(uid, rescue, htn);
            Dirty(uid, rescue);
            return false;
        }

        if (!TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState == MobState.Dead)
        {
            StopPullingTarget(uid, target);
            rescue.EvacuatingTarget = null;
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(uid, rescue, htn);
            Dirty(uid, rescue);
            return false;
        }

        if (IsEvacuationComplete(target, rescue))
        {
            CompleteEvacuation(uid, rescue, htn, target);
            return false;
        }

        if (UpdateTargetProgress(uid, target, rescue))
        {
            TemporarilySkipTarget(uid, rescue, htn, target);
            return false;
        }

        if (TryFindPatientDeliveryStrap(rescue, out var patientStrap, out _))
        {
            rescue.AssignedPatientStrap = patientStrap;
            if (TryBucklePatientToStrap(uid, target, patientStrap, rescue))
            {
                CompleteEvacuation(uid, rescue, htn, target);
                return false;
            }
        }
        else
        {
            rescue.AssignedPatientStrap = null;
        }

        if (!IsPullingTarget(uid, target))
        {
            if (IsWithinRange(uid, target, rescue.EvacuationStartRange))
                _pulling.TryStartPull(uid, target);

            if (!IsPullingTarget(uid, target))
            {
                SetFollowTarget(uid, rescue, htn, target);
                return true;
            }
        }

        if (rescue.AssignedPatientStrap is { Valid: true } deliveryStrap &&
            !Deleted(deliveryStrap) &&
            TryBucklePatientToStrap(uid, target, deliveryStrap, rescue))
        {
            CompleteEvacuation(uid, rescue, htn, target);
            return false;
        }

        if (rescue.AssignedPatientStrap is { Valid: true } assignedStrap &&
            !Deleted(assignedStrap))
        {
            return SetFollowDeliveryStrap(uid, rescue, htn, assignedStrap);
        }

        return SetFollowShuttle(uid, rescue, htn);
    }

    private bool TryStartOrContinueEvacuation(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (!NeedsEvacuation(target, rescue))
            return false;

        if (rescue.EvacuatingTarget != target)
        {
            rescue.ShuttleReturnRouted = false;
            rescue.ShuttleRoutedTarget = null;
            rescue.AssignedPatientStrap = null;
            ResetTargetProgress(rescue);
        }

        rescue.EvacuatingTarget = target;
        TryRouteShuttleToTarget(uid, rescue, target);

        if (!IsWithinRange(uid, target, rescue.EvacuationStartRange))
        {
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        _pulling.TryStartPull(uid, target);

        if (!IsPullingTarget(uid, target))
        {
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        return SetFollowShuttle(uid, rescue, htn);
    }

    private bool IsRescueCandidate(
        EntityUid rescuer,
        EntityUid candidate,
        MedibotComponent medibot,
        bool requireRange,
        float searchRange,
        out float score)
    {
        score = 0f;

        if (candidate == rescuer ||
            Deleted(candidate) ||
            !TryComp<MobStateComponent>(candidate, out var mobState) ||
            !TryComp<DamageableComponent>(candidate, out var damage) ||
            !HasComp<InjectableSolutionComponent>(candidate))
        {
            return false;
        }

        if (mobState.CurrentState == MobState.Dead ||
            !_medibot.TryGetTreatment(medibot, mobState.CurrentState, out var treatment) ||
            !treatment.IsValid(damage.TotalDamage))
        {
            return false;
        }

        var rescuerCoordinates = Transform(rescuer).Coordinates;
        var candidateCoordinates = Transform(candidate).Coordinates;
        if (!rescuerCoordinates.TryDistance(EntityManager, candidateCoordinates, out var distance))
            return false;

        if (requireRange && distance > searchRange)
            return false;

        score = damage.TotalDamage.Float() - distance;
        if (mobState.CurrentState == MobState.Critical)
            score += 1000f;

        return true;
    }

    private bool NeedsEvacuation(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.EvacuateTargetsToShuttle ||
            !CanUseAssignedShuttle(rescue) ||
            IsTargetTemporarilySkipped(target, rescue) ||
            IsEvacuationComplete(target, rescue) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !TryComp<DamageableComponent>(target, out var damage) ||
            !HasComp<PullableComponent>(target) ||
            mobState.CurrentState == MobState.Dead)
        {
            return false;
        }

        return mobState.CurrentState == MobState.Critical ||
               damage.TotalDamage.Float() >= rescue.EvacuationMinDamage;
    }

    private bool IsEvacuationCandidate(
        EntityUid rescuer,
        EntityUid candidate,
        LuaMRescueAgentComponent rescue,
        float searchRange,
        out float score)
    {
        score = 0f;

        if (candidate == rescuer ||
            Deleted(candidate) ||
            !NeedsEvacuation(candidate, rescue))
        {
            return false;
        }

        var rescuerCoordinates = Transform(rescuer).Coordinates;
        var candidateCoordinates = Transform(candidate).Coordinates;
        if (!rescuerCoordinates.TryDistance(EntityManager, candidateCoordinates, out var distance) ||
            distance > searchRange)
        {
            return false;
        }

        if (!TryComp<MobStateComponent>(candidate, out var mobState) ||
            !TryComp<DamageableComponent>(candidate, out var damage))
        {
            return false;
        }

        score = damage.TotalDamage.Float() - distance;
        if (mobState.CurrentState == MobState.Critical)
            score += 1000f;

        return true;
    }

    private bool CanUseAssignedShuttle(LuaMRescueAgentComponent rescue)
    {
        return rescue.EvacuateTargetsToShuttle &&
               rescue.AssignedShuttle is { Valid: true } shuttle &&
               !Deleted(shuttle);
    }

    private bool IsOnAssignedShuttle(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        return rescue.AssignedShuttle is { Valid: true } shuttle &&
               !Deleted(shuttle) &&
               Transform(target).GridUid == shuttle;
    }

    private bool IsAtAssignedShuttleAnchor(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!TryGetShuttleAnchorCoordinates(rescue, out var coordinates))
            return false;

        var targetCoordinates = Transform(target).Coordinates;
        return targetCoordinates.TryDistance(EntityManager, coordinates, out var distance) &&
               distance <= rescue.EvacuationArrivalRange;
    }

    private bool IsPullingTarget(EntityUid uid, EntityUid target)
    {
        return TryComp<PullerComponent>(uid, out var puller) &&
               puller.Pulling == target;
    }

    private void StopPullingTarget(EntityUid uid, EntityUid target)
    {
        if (!IsPullingTarget(uid, target) ||
            !TryComp<PullableComponent>(target, out var pullable))
        {
            return;
        }

        _pulling.TryStopPull(target, pullable);
    }

    private bool TryRouteShuttleHome(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.AutoReturnShuttle ||
            rescue.ShuttleReturnRouted ||
            rescue.AssignedShuttleConsole is not { Valid: true } console ||
            Deleted(console) ||
            rescue.AssignedReturnTarget is not { Valid: true } returnTarget ||
            Deleted(returnTarget) ||
            !TryComp<ShuttleConsoleComponent>(console, out var shuttleConsole) ||
            !TryComp<HTNComponent>(console, out var htn))
        {
            return false;
        }

        _npc.SetBlackboard(console, shuttleConsole.AutopilotTargetKey, new EntityCoordinates(returnTarget, Vector2.Zero), htn);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);
        _npc.WakeNPC(console, htn);
        rescue.ShuttleReturnRouted = true;
        rescue.ShuttleRoutedTarget = null;
        Dirty(uid, rescue);
        return true;
    }

    private bool TryRouteShuttleToTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (!rescue.AutoRouteShuttleToTargets ||
            IsOnAssignedShuttle(target, rescue) ||
            IsEvacuationComplete(target, rescue) ||
            rescue.ShuttleRoutedTarget == target ||
            rescue.AssignedShuttleConsole is not { Valid: true } console ||
            Deleted(console) ||
            Deleted(target) ||
            !TryComp<ShuttleConsoleComponent>(console, out var shuttleConsole) ||
            !TryComp<HTNComponent>(console, out var htn))
        {
            return false;
        }

        _npc.SetBlackboard(console, shuttleConsole.AutopilotTargetKey, new EntityCoordinates(target, Vector2.Zero), htn);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);
        _npc.WakeNPC(console, htn);
        rescue.ShuttleRoutedTarget = target;
        rescue.ShuttleReturnRouted = false;
        Dirty(uid, rescue);
        return true;
    }

    private bool IsWithinRange(EntityUid uid, EntityUid target, float range)
    {
        var uidCoordinates = Transform(uid).Coordinates;
        var targetCoordinates = Transform(target).Coordinates;
        return uidCoordinates.TryDistance(EntityManager, targetCoordinates, out var distance) &&
               distance <= range;
    }

    private void PruneSkippedTargets(LuaMRescueAgentComponent rescue)
    {
        if (rescue.SkippedTargets.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in rescue.SkippedTargets
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            rescue.SkippedTargets.Remove(target);
        }
    }

    private bool IsTargetTemporarilySkipped(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.SkippedTargets.TryGetValue(target, out var skipUntil))
            return false;

        if (_timing.CurTime < skipUntil)
            return true;

        rescue.SkippedTargets.Remove(target);
        return false;
    }

    private bool UpdateTargetProgress(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.TemporarilySkipStalledTargets ||
            rescue.TargetStallSeconds <= 0f ||
            rescue.TargetSkipSeconds <= 0f ||
            !TryGetEvacuationProgressDistance(uid, target, rescue, out var progressGoal, out var distance))
        {
            return false;
        }

        if (rescue.ProgressTarget != target ||
            rescue.ProgressGoal != progressGoal)
        {
            rescue.ProgressTarget = target;
            rescue.ProgressGoal = progressGoal;
            rescue.LastProgressDistance = distance;
            rescue.TargetStallAccumulator = 0f;
            return false;
        }

        if (distance + rescue.TargetProgressTolerance < rescue.LastProgressDistance)
        {
            rescue.LastProgressDistance = distance;
            rescue.TargetStallAccumulator = 0f;
            return false;
        }

        if (distance > rescue.LastProgressDistance + rescue.TargetProgressTolerance)
            rescue.LastProgressDistance = distance;

        rescue.TargetStallAccumulator += rescue.TargetRefreshInterval;
        return rescue.TargetStallAccumulator >= rescue.TargetStallSeconds;
    }

    private bool TryGetEvacuationProgressDistance(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        out EntityUid? progressGoal,
        out float distance)
    {
        if (IsPullingTarget(uid, target))
        {
            if (rescue.AssignedPatientStrap is { Valid: true } patientStrap &&
                !Deleted(patientStrap))
            {
                progressGoal = patientStrap;
                return TryGetDistance(target, patientStrap, out distance);
            }

            if (rescue.AssignedShuttleAnchor is { Valid: true } anchor &&
                !Deleted(anchor))
            {
                progressGoal = anchor;
                return TryGetDistance(target, anchor, out distance);
            }

            if (rescue.AssignedShuttle is { Valid: true } shuttle &&
                !Deleted(shuttle))
            {
                progressGoal = shuttle;
                return TryGetDistance(target, shuttle, out distance);
            }
        }

        progressGoal = target;
        return TryGetDistance(uid, target, out distance);
    }

    private bool TryGetDistance(EntityUid first, EntityUid second, out float distance)
    {
        var firstCoordinates = Transform(first).Coordinates;
        var secondCoordinates = Transform(second).Coordinates;
        return firstCoordinates.TryDistance(EntityManager, secondCoordinates, out distance);
    }

    private void TemporarilySkipTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        rescue.SkippedTargets[target] = _timing.CurTime + TimeSpan.FromSeconds(rescue.TargetSkipSeconds);

        StopPullingTarget(uid, target);
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.ShuttleRoutedTarget = null;
        ResetTargetProgress(rescue);

        if (!HasPendingEvacuationTarget(uid, rescue, target))
            TryRouteShuttleHome(uid, rescue);
        else
            rescue.ShuttleReturnRouted = false;

        StandbyAtAssignedShuttle(uid, rescue, htn);
        Dirty(uid, rescue);
    }

    private void ResetTargetProgress(LuaMRescueAgentComponent rescue)
    {
        rescue.ProgressTarget = null;
        rescue.ProgressGoal = null;
        rescue.LastProgressDistance = float.PositiveInfinity;
        rescue.TargetStallAccumulator = 0f;
    }

    private void CompleteEvacuation(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        StopPullingTarget(uid, target);
        rescue.ShuttleRoutedTarget = null;
        ResetTargetProgress(rescue);
        if (!HasPendingEvacuationTarget(uid, rescue, target))
            TryRouteShuttleHome(uid, rescue);
        else
            rescue.ShuttleReturnRouted = false;

        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        StandbyAtAssignedShuttle(uid, rescue, htn);
        Dirty(uid, rescue);
    }

    private bool IsEvacuationComplete(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (IsBuckledToAssignedShuttlePatientStrap(target, rescue))
            return true;

        if (TryFindPatientDeliveryStrap(rescue, out _, out _))
            return false;

        return IsOnAssignedShuttle(target, rescue) ||
               IsAtAssignedShuttleAnchor(target, rescue);
    }

    private bool HasPendingEvacuationTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid completedTarget)
    {
        return TryFindEvacuationTarget(uid, rescue.SearchRange, rescue, out _, completedTarget);
    }

    private bool TryFindPatientDeliveryStrap(
        LuaMRescueAgentComponent rescue,
        out EntityUid patientStrap,
        out StrapComponent strap)
    {
        patientStrap = default;
        strap = default!;

        if (!rescue.BucklePatientsOnShuttle ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        if (rescue.AssignedPatientStrap is { Valid: true } assigned &&
            !Deleted(assigned) &&
            TryComp<StrapComponent>(assigned, out var assignedStrap) &&
            IsAvailablePatientDeliveryStrap(assigned, assignedStrap, rescue))
        {
            patientStrap = assigned;
            strap = assignedStrap;
            return true;
        }

        var bestScore = float.MinValue;
        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var strapComp, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAvailablePatientDeliveryStrap(uid, strapComp, rescue))
            {
                continue;
            }

            var score = GetPatientDeliveryStrapScore(uid, rescue);
            if (score <= bestScore)
                continue;

            patientStrap = uid;
            strap = strapComp;
            bestScore = score;
        }

        return patientStrap != default;
    }

    private bool IsAvailablePatientDeliveryStrap(
        EntityUid uid,
        StrapComponent strap,
        LuaMRescueAgentComponent rescue)
    {
        return strap.Enabled &&
               strap.BuckledEntities.Count == 0 &&
               IsAssignedShuttlePatientStrap(uid, rescue);
    }

    private bool IsAssignedShuttlePatientStrap(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle) ||
            !HasComp<StrapComponent>(uid) ||
            Transform(uid).GridUid != shuttle)
        {
            return false;
        }

        if (rescue.PreferStasisBedDelivery && HasComp<StasisBedComponent>(uid))
            return true;

        return HasComp<HealOnBuckleComponent>(uid);
    }

    private float GetPatientDeliveryStrapScore(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        var score = 0f;
        if (HasComp<StasisBedComponent>(uid))
            score += 1000f;
        else if (HasComp<HealOnBuckleComponent>(uid))
            score += 100f;

        if (TryGetShuttleAnchorCoordinates(rescue, out var anchorCoordinates) &&
            Transform(uid).Coordinates.TryDistance(EntityManager, anchorCoordinates, out var distance))
        {
            score -= distance;
        }

        return score;
    }

    private bool TryBucklePatientToStrap(
        EntityUid uid,
        EntityUid target,
        EntityUid patientStrap,
        LuaMRescueAgentComponent rescue)
    {
        if (!rescue.BucklePatientsOnShuttle ||
            Deleted(target) ||
            Deleted(patientStrap) ||
            !IsAssignedShuttlePatientStrap(patientStrap, rescue) ||
            !TryComp<BuckleComponent>(target, out var buckle) ||
            !TryComp<StrapComponent>(patientStrap, out var strap))
        {
            return false;
        }

        if (buckle.BuckledTo == patientStrap)
            return true;

        if (strap.BuckledEntities.Count != 0)
            return false;

        if (!IsWithinRange(target, patientStrap, buckle.Range))
            return false;

        return _buckle.TryBuckle(target, uid, patientStrap, buckle, popup: false);
    }

    private bool IsBuckledToAssignedShuttlePatientStrap(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        return TryComp<BuckleComponent>(target, out var buckle) &&
               buckle.BuckledTo is { Valid: true } strap &&
               !Deleted(strap) &&
               IsAssignedShuttlePatientStrap(strap, rescue);
    }

    private void SetFollowTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        rescue.AssignedTarget = target;
        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, new EntityCoordinates(target, Vector2.Zero), htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", rescue.FollowCloseRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", rescue.FollowRange, htn);
        _npc.WakeNPC(uid, htn);
        Dirty(uid, rescue);
    }

    private bool SetFollowShuttle(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!TryGetShuttleAnchorCoordinates(rescue, out var coordinates))
            return false;

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, coordinates, htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", rescue.FollowCloseRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", rescue.FollowRange, htn);
        _npc.WakeNPC(uid, htn);
        Dirty(uid, rescue);
        return true;
    }

    private bool SetFollowDeliveryStrap(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid patientStrap)
    {
        if (Deleted(patientStrap))
            return SetFollowShuttle(uid, rescue, htn);

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, new EntityCoordinates(patientStrap, Vector2.Zero), htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", rescue.FollowCloseRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", rescue.FollowRange, htn);
        _npc.WakeNPC(uid, htn);
        Dirty(uid, rescue);
        return true;
    }

    private void StandbyAtAssignedShuttle(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;

        if (CanUseAssignedShuttle(rescue))
        {
            TryRouteShuttleHome(uid, rescue);

            if (!IsOnAssignedShuttle(uid, rescue) &&
                SetFollowShuttle(uid, rescue, htn))
            {
                return;
            }
        }

        ClearFollowTarget(uid, rescue, htn);
        Dirty(uid, rescue);
    }

    private bool TryGetShuttleAnchorCoordinates(LuaMRescueAgentComponent rescue, out EntityCoordinates coordinates)
    {
        if (rescue.AssignedShuttleAnchor is { Valid: true } anchor &&
            !Deleted(anchor))
        {
            coordinates = Transform(anchor).Coordinates;
            return true;
        }

        if (rescue.AssignedShuttle is { Valid: true } shuttle &&
            !Deleted(shuttle))
        {
            coordinates = Transform(shuttle).Coordinates;
            return true;
        }

        coordinates = default;
        return false;
    }

    private void ClearFollowTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (rescue.AssignedTarget == null &&
            !htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget))
        {
            return;
        }

        rescue.AssignedTarget = null;
        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        Dirty(uid, rescue);
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueAgentCommand : IConsoleCommand
{
    private const string ControlFlag = "--control";
    private const string NearTargetFlag = "--near-target";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public string Command => "luam_rescue_agent";
    public string Description => "Spawns a player-like LuaM rescue agent humanoid.";
    public string Help => $"Usage: {Command} [targetEntityId|playerName] [{ControlFlag}] [{NearTargetFlag}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var control = args.Any(arg => arg.Equals(ControlFlag, StringComparison.OrdinalIgnoreCase));
        var nearTarget = args.Any(arg => arg.Equals(NearTargetFlag, StringComparison.OrdinalIgnoreCase));
        var targetArg = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));

        if (control && shell.Player == null)
        {
            shell.WriteError($"{ControlFlag} requires a player shell.");
            return;
        }

        EntityUid? target = null;
        if (!string.IsNullOrWhiteSpace(targetArg) &&
            !TryResolveTarget(targetArg, out target, out var error))
        {
            shell.WriteError(error);
            return;
        }

        target ??= shell.Player?.AttachedEntity;
        var anchor = nearTarget ? target : shell.Player?.AttachedEntity ?? target;
        if (anchor is not { Valid: true } anchorUid)
        {
            shell.WriteError("No spawn anchor. Attach to a mob or pass a valid target with --near-target.");
            return;
        }

        var system = _entities.System<LuaMRescueAgentSystem>();
        var agent = system.SpawnAgent(anchorUid, target, shell.Player, control);
        var netAgent = _entities.GetNetEntity(agent);
        var targetText = target is { Valid: true } targetUid
            ? _entities.GetNetEntity(targetUid).ToString()
            : "none";

        shell.WriteLine($"Spawned LuaM rescue agent {netAgent}; followTarget={targetText}; controlled={control}.");
        if (!control)
            shell.WriteLine($"Use `controlmob {netAgent.Id}` if you want to take direct control like a player.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var names = _players.Sessions.Select(session => session.Name);
            return CompletionResult.FromHintOptions(names.Concat([ControlFlag, NearTargetFlag]), "target player/entity or flag");
        }

        return CompletionResult.FromHintOptions([ControlFlag, NearTargetFlag], "optional flag");
    }

    private bool TryResolveTarget(string raw, out EntityUid? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (NetEntity.TryParse(raw, out var netEntity) &&
            _entities.TryGetEntity(netEntity, out var parsedTarget) &&
            parsedTarget is { Valid: true })
        {
            target = parsedTarget.Value;
            return true;
        }

        if (int.TryParse(raw, out var integerId) &&
            _entities.TryGetEntity(new NetEntity(integerId), out parsedTarget) &&
            parsedTarget is { Valid: true })
        {
            target = parsedTarget.Value;
            return true;
        }

        var matches = _players.Sessions
            .Where(session => session.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 1 && matches[0].AttachedEntity is { Valid: true } attached)
        {
            target = attached;
            return true;
        }

        error = matches.Length > 1
            ? $"Target player name is ambiguous: {raw}."
            : $"Target not found or has no attached entity: {raw}.";
        return false;
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueOrderCommand : IConsoleCommand
{
    private const string AgentKey = "agent";
    private const string TargetKey = "target";
    private const string AllAgentsValue = "all";
    private const string NearestAgentValue = "nearest";
    private const string ClearValue = "clear";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public string Command => "luam_rescue_order";
    public string Description => "Orders active LuaM rescue agents to follow or rescue a target.";
    public string Help =>
        $"Usage: {Command} [agent=<entity|{NearestAgentValue}|{AllAgentsValue}>] [target=<entity|player>|{ClearValue}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var clear = args.Any(arg => arg.Equals(ClearValue, StringComparison.OrdinalIgnoreCase) ||
                                    arg.Equals($"{TargetKey}={ClearValue}", StringComparison.OrdinalIgnoreCase));
        var agentArg = GetValue(args, AgentKey);
        var targetArg = GetValue(args, TargetKey) ??
                        args.FirstOrDefault(arg => !arg.Contains('=') &&
                                                   !arg.StartsWith("--", StringComparison.Ordinal) &&
                                                   !arg.Equals(ClearValue, StringComparison.OrdinalIgnoreCase));

        if (!TryResolveAgents(shell, agentArg, out var agents, out var error))
        {
            shell.WriteError(error);
            return;
        }

        EntityUid? target = null;
        if (!clear)
        {
            if (string.IsNullOrWhiteSpace(targetArg))
            {
                shell.WriteError($"Pass {TargetKey}=<entity|player> or {ClearValue}.");
                return;
            }

            if (!TryResolveTarget(targetArg, out target, out error))
            {
                shell.WriteError(error);
                return;
            }
        }

        var system = _entities.System<LuaMRescueAgentSystem>();
        foreach (var agent in agents)
        {
            if (system.TryOrderAgent(agent, target, out var status))
                shell.WriteLine(status);
            else
                shell.WriteError(status);
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length <= 1)
        {
            var names = _players.Sessions.Select(session => $"{TargetKey}={session.Name}");
            return CompletionResult.FromHintOptions(
                names.Concat([
                    $"{AgentKey}={NearestAgentValue}",
                    $"{AgentKey}={AllAgentsValue}",
                    $"{TargetKey}=",
                    ClearValue,
                ]),
                "rescue order option");
        }

        return CompletionResult.FromHintOptions([
            $"{AgentKey}={NearestAgentValue}",
            $"{AgentKey}={AllAgentsValue}",
            $"{TargetKey}=",
            ClearValue,
        ], "rescue order option");
    }

    private bool TryResolveAgents(IConsoleShell shell, string? raw, out List<EntityUid> agents, out string error)
    {
        agents = [];
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (raw.Equals(AllAgentsValue, StringComparison.OrdinalIgnoreCase))
            {
                agents.AddRange(GetActiveAgents());
                if (agents.Count > 0)
                    return true;

                error = "No LuaM rescue agents are active.";
                return false;
            }

            if (raw.Equals(NearestAgentValue, StringComparison.OrdinalIgnoreCase))
                return TryResolveNearestAgent(shell, out agents, out error);

            if (TryResolveEntity(raw, out var agent) &&
                _entities.HasComponent<LuaMRescueAgentComponent>(agent))
            {
                agents.Add(agent);
                return true;
            }

            error = $"LuaM rescue agent not found: {raw}.";
            return false;
        }

        agents.AddRange(GetActiveAgents());
        if (agents.Count == 1)
            return true;

        if (agents.Count > 1)
            return TryResolveNearestAgent(shell, out agents, out error);

        error = "No LuaM rescue agents are active.";
        return false;
    }

    private bool TryResolveNearestAgent(IConsoleShell shell, out List<EntityUid> agents, out string error)
    {
        agents = [];
        error = string.Empty;

        if (shell.Player?.AttachedEntity is not { Valid: true } attached)
        {
            error = $"Could not infer nearest agent. Pass {AgentKey}=<entity|{AllAgentsValue}>.";
            return false;
        }

        var attachedCoordinates = _entities.GetComponent<TransformComponent>(attached).Coordinates;
        var bestDistance = float.PositiveInfinity;
        EntityUid? bestAgent = null;

        foreach (var agent in GetActiveAgents())
        {
            var coordinates = _entities.GetComponent<TransformComponent>(agent).Coordinates;
            if (!attachedCoordinates.TryDistance(_entities, coordinates, out var distance) ||
                distance >= bestDistance)
            {
                continue;
            }

            bestAgent = agent;
            bestDistance = distance;
        }

        if (bestAgent is { Valid: true } nearest)
        {
            agents.Add(nearest);
            return true;
        }

        error = "No reachable LuaM rescue agent found.";
        return false;
    }

    private List<EntityUid> GetActiveAgents()
    {
        var agents = new List<EntityUid>();
        var query = _entities.EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            agents.Add(uid);
        }

        return agents;
    }

    private bool TryResolveTarget(string raw, out EntityUid? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (TryResolveEntity(raw, out var parsedTarget))
        {
            target = parsedTarget;
            return true;
        }

        var matches = _players.Sessions
            .Where(session => session.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 1 && matches[0].AttachedEntity is { Valid: true } attached)
        {
            target = attached;
            return true;
        }

        error = matches.Length > 1
            ? $"Target player name is ambiguous: {raw}."
            : $"Target not found or has no attached entity: {raw}.";
        return false;
    }

    private bool TryResolveEntity(string raw, out EntityUid entity)
    {
        entity = default;

        if (NetEntity.TryParse(raw, out var netEntity) &&
            _entities.TryGetEntity(netEntity, out var parsed) &&
            parsed is { Valid: true })
        {
            entity = parsed.Value;
            return true;
        }

        if (!int.TryParse(raw, out var integerId))
            return false;

        if (_entities.TryGetEntity(new NetEntity(integerId), out parsed) &&
            parsed is { Valid: true })
        {
            entity = parsed.Value;
            return true;
        }

        var uid = new EntityUid(integerId);
        if (!_entities.EntityExists(uid))
            return false;

        entity = uid;
        return true;
    }

    private static string? GetValue(string[] args, string key)
    {
        var prefix = $"{key}=";
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match?.Substring(prefix.Length);
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_rescue_status";
    public string Description => "Prints LuaM rescue agent mission telemetry.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var system = _entities.System<LuaMRescueAgentSystem>();
        var lines = system.BuildRescueStatusLines();
        if (lines.Count == 0)
        {
            shell.WriteLine("No LuaM rescue agents are active.");
            return;
        }

        shell.WriteLine($"LuaM rescue agents: {lines.Count}");
        foreach (var line in lines)
        {
            shell.WriteLine(line);
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.Empty;
    }
}
