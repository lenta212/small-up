using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._LuaM.AI;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.AI;

/// <summary>
/// Server-owned work queue with expiring target leases. It prevents several
/// agents from silently selecting the same single-owner job and recovers work
/// when an assignee disappears or stalls.
/// </summary>
public sealed partial class LuaMWorkOrderSystem : EntitySystem
{
    private static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumFailureBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumFailureBackoff = TimeSpan.FromMinutes(2);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LuaMWorkOrderBoardComponent>();
        while (query.MoveNext(out var boardUid, out var board))
            PruneBoard(boardUid, board);
    }

    public bool Publish(
        EntityUid boardUid,
        LuaMWorkOrderRequest request,
        out uint orderId)
    {
        orderId = 0;
        if (!TryComp<LuaMWorkOrderBoardComponent>(boardUid, out var board) ||
            request.Intent is LuaMBehaviorIntent.None or LuaMBehaviorIntent.Standby ||
            request.Lifetime <= TimeSpan.Zero ||
            request.MaxAssignees <= 0)
        {
            return false;
        }

        PruneBoard(boardUid, board);
        var now = _timing.CurTime;
        var duplicate = board.Orders.FirstOrDefault(order =>
            order.State is (LuaMWorkOrderState.Open or LuaMWorkOrderState.Claimed) &&
            order.Intent == request.Intent &&
            order.Target == request.Target &&
            Nullable.Equals(order.Destination, request.Destination));
        if (duplicate != null)
        {
            duplicate.Priority = Math.Clamp(request.Priority, 0, 1000);
            duplicate.Severity = Math.Clamp(request.Severity, 0f, 1f);
            duplicate.ExpiresAt = AddDuration(now, request.Lifetime);
            duplicate.MaxAssignees = Math.Clamp(request.MaxAssignees, 1, 32);
            duplicate.RequiredCapabilities = request.RequiredCapabilities.Distinct().ToList();
            orderId = duplicate.Id;
            return true;
        }

        EnsureCapacity(board);
        if (board.Orders.Count >= Math.Clamp(board.MaximumOrders, 1, 1024))
            return false;

        orderId = AllocateId(board);
        board.Orders.Add(new LuaMWorkOrder
        {
            Id = orderId,
            Intent = request.Intent,
            Priority = Math.Clamp(request.Priority, 0, 1000),
            Severity = Math.Clamp(request.Severity, 0f, 1f),
            Target = request.Target,
            Destination = request.Destination,
            RequiredCapabilities = request.RequiredCapabilities.Distinct().ToList(),
            CreatedAt = now,
            ExpiresAt = AddDuration(now, request.Lifetime),
            MaxAssignees = Math.Clamp(request.MaxAssignees, 1, 32),
            State = LuaMWorkOrderState.Open,
        });
        return true;
    }

    public bool TryClaimBest(
        EntityUid boardUid,
        EntityUid agent,
        TimeSpan leaseDuration,
        out LuaMWorkOrder? selected,
        IReadOnlySet<LuaMBehaviorIntent>? allowedIntents = null)
    {
        selected = null;
        if (!TryComp<LuaMWorkOrderBoardComponent>(boardUid, out var board) ||
            !TryComp<LuaMBehaviorAgentComponent>(agent, out var behaviorAgent) ||
            !_prototypes.TryIndex(
                behaviorAgent.Profile,
                out LuaMBehaviorProfilePrototype? profile))
        {
            return false;
        }

        PruneBoard(boardUid, board);
        LuaMWorkOrder? best = null;
        long bestScore = long.MinValue;
        foreach (var order in board.Orders)
        {
            if (order.State is not (LuaMWorkOrderState.Open or LuaMWorkOrderState.Claimed) ||
                allowedIntents != null && !allowedIntents.Contains(order.Intent) ||
                order.RequiredCapabilities.Any(capability => !profile.HasCapability(capability)))
            {
                continue;
            }

            var attempt = order.Attempts.FirstOrDefault(candidate => candidate.Agent == agent);
            if (attempt?.RetryAt > _timing.CurTime)
                continue;

            var ownsLease = order.Leases.Any(lease => lease.Agent == agent);
            if (!ownsLease && order.Leases.Count >= order.MaxAssignees)
                continue;

            var ageSeconds = Math.Max(0L, (long) (_timing.CurTime - order.CreatedAt).TotalSeconds);
            var score = (long) order.Priority * 1_000_000L +
                        (long) (order.Severity * 100_000f) +
                        Math.Min(ageSeconds, 99_999L) -
                        (long) (attempt?.Failures ?? 0) * 10_000L;
            if (score < bestScore || score == bestScore && best != null && order.Id >= best.Id)
                continue;

            best = order;
            bestScore = score;
        }

        if (best == null)
            return false;

        ReleaseAgentLeases(boardUid, board, agent, exceptOrderId: best.Id);
        leaseDuration = ClampLease(leaseDuration);
        var lease = best.Leases.FirstOrDefault(candidate => candidate.Agent == agent);
        if (lease == null)
        {
            lease = new LuaMWorkOrderLease { Agent = agent };
            best.Leases.Add(lease);
        }

        lease.ExpiresAt = AddDuration(_timing.CurTime, leaseDuration);
        best.State = LuaMWorkOrderState.Claimed;
        selected = best;
        PublishOrderObservation(boardUid, agent, best, leaseDuration);
        return true;
    }

    public bool TryGetClaimedOrder(
        EntityUid boardUid,
        uint orderId,
        EntityUid agent,
        out LuaMWorkOrder? selected)
    {
        selected = null;
        if (!TryGetOrder(boardUid, orderId, out var board, out var order))
            return false;

        PruneBoard(boardUid, board);
        if (order.State is not (LuaMWorkOrderState.Open or LuaMWorkOrderState.Claimed) ||
            order.Leases.All(lease => lease.Agent != agent))
        {
            return false;
        }

        selected = order;
        return true;
    }

    public bool Renew(
        EntityUid boardUid,
        uint orderId,
        EntityUid agent,
        TimeSpan leaseDuration)
    {
        if (!TryGetOrder(boardUid, orderId, out var board, out var order))
            return false;

        PruneBoard(boardUid, board);
        var lease = order.Leases.FirstOrDefault(candidate => candidate.Agent == agent);
        if (lease == null || order.State is not (LuaMWorkOrderState.Open or LuaMWorkOrderState.Claimed))
            return false;

        leaseDuration = ClampLease(leaseDuration);
        lease.ExpiresAt = AddDuration(_timing.CurTime, leaseDuration);
        PublishOrderObservation(boardUid, agent, order, leaseDuration);
        return true;
    }

    public bool Release(EntityUid boardUid, uint orderId, EntityUid agent, string failure = "")
    {
        if (!TryGetOrder(boardUid, orderId, out _, out var order))
            return false;

        var removed = order.Leases.RemoveAll(lease => lease.Agent == agent) > 0;
        if (!removed)
            return false;

        ClearOrderObservation(boardUid, agent, order);
        if (!string.IsNullOrWhiteSpace(failure))
        {
            order.Failures++;
            order.LastFailure = failure.Trim();
            var attempt = order.Attempts.FirstOrDefault(candidate => candidate.Agent == agent);
            if (attempt == null)
            {
                attempt = new LuaMWorkOrderAttempt { Agent = agent };
                order.Attempts.Add(attempt);
            }

            attempt.Failures = attempt.Failures >= int.MaxValue ? int.MaxValue : attempt.Failures + 1;
            attempt.LastFailure = order.LastFailure;
            attempt.RetryAt = AddDuration(_timing.CurTime, CalculateFailureBackoff(attempt.Failures));
        }

        if (order.Leases.Count == 0)
            order.State = LuaMWorkOrderState.Open;
        return true;
    }

    public bool Complete(EntityUid boardUid, uint orderId, EntityUid agent)
    {
        if (!TryGetOrder(boardUid, orderId, out _, out var order) ||
            order.Leases.All(lease => lease.Agent != agent))
        {
            return false;
        }

        order.State = LuaMWorkOrderState.Completed;
        ClearAllOrderObservations(boardUid, order);
        order.Leases.Clear();
        return true;
    }

    public bool Cancel(EntityUid boardUid, uint orderId)
    {
        if (!TryGetOrder(boardUid, orderId, out _, out var order))
            return false;

        order.State = LuaMWorkOrderState.Cancelled;
        ClearAllOrderObservations(boardUid, order);
        order.Leases.Clear();
        return true;
    }

    private void PruneBoard(EntityUid boardUid, LuaMWorkOrderBoardComponent board)
    {
        var now = _timing.CurTime;
        foreach (var order in board.Orders)
        {
            order.Attempts.RemoveAll(attempt => TerminatingOrDeleted(attempt.Agent));

            for (var i = order.Leases.Count - 1; i >= 0; i--)
            {
                var lease = order.Leases[i];
                if (lease.ExpiresAt > now && !TerminatingOrDeleted(lease.Agent))
                    continue;

                ClearOrderObservation(boardUid, lease.Agent, order);
                order.Leases.RemoveAt(i);
            }

            if (order.State == LuaMWorkOrderState.Claimed && order.Leases.Count == 0)
                order.State = LuaMWorkOrderState.Open;

            if (order.State is LuaMWorkOrderState.Completed or
                LuaMWorkOrderState.Cancelled or
                LuaMWorkOrderState.Failed)
                continue;

            var targetDeleted = order.Target is { } target && TerminatingOrDeleted(target);
            if (!targetDeleted && order.ExpiresAt > now)
                continue;

            order.State = LuaMWorkOrderState.Cancelled;
            ClearAllOrderObservations(boardUid, order);
            order.Leases.Clear();
        }

        EnsureCapacity(board);
    }

    private void EnsureCapacity(LuaMWorkOrderBoardComponent board)
    {
        var maximum = Math.Clamp(board.MaximumOrders, 1, 1024);
        if (board.Orders.Count < maximum)
            return;

        board.Orders.RemoveAll(order =>
            order.State is LuaMWorkOrderState.Completed or
                LuaMWorkOrderState.Cancelled or
                LuaMWorkOrderState.Failed);
    }

    private void ReleaseAgentLeases(
        EntityUid boardUid,
        LuaMWorkOrderBoardComponent board,
        EntityUid agent,
        uint exceptOrderId)
    {
        foreach (var order in board.Orders)
        {
            if (order.Id == exceptOrderId || order.Leases.RemoveAll(lease => lease.Agent == agent) == 0)
                continue;

            ClearOrderObservation(boardUid, agent, order);
            if (order.Leases.Count == 0 && order.State == LuaMWorkOrderState.Claimed)
                order.State = LuaMWorkOrderState.Open;
        }
    }

    private void PublishOrderObservation(
        EntityUid boardUid,
        EntityUid agent,
        LuaMWorkOrder order,
        TimeSpan leaseDuration)
    {
        var stimulus = StimulusFor(order.Intent);
        var severity = Math.Max(order.Severity, order.Priority / 1000f);
        _behavior.ReportObservation(
            agent,
            stimulus,
            severity,
            target: order.Target,
            destination: order.Destination,
            ttl: leaseDuration,
            source: SourceFor(boardUid, order.Id),
            evaluateNow: true);
    }

    private void ClearOrderObservation(EntityUid boardUid, EntityUid agent, LuaMWorkOrder order)
    {
        if (TerminatingOrDeleted(agent))
            return;

        _behavior.ClearObservations(
            agent,
            stimulus: StimulusFor(order.Intent),
            source: SourceFor(boardUid, order.Id));
    }

    private void ClearAllOrderObservations(EntityUid boardUid, LuaMWorkOrder order)
    {
        foreach (var lease in order.Leases)
            ClearOrderObservation(boardUid, lease.Agent, order);
    }

    private bool TryGetOrder(
        EntityUid boardUid,
        uint orderId,
        out LuaMWorkOrderBoardComponent board,
        out LuaMWorkOrder order)
    {
        order = default!;
        if (!TryComp(boardUid, out board!))
            return false;

        var found = board.Orders.FirstOrDefault(candidate => candidate.Id == orderId);
        if (found == null)
            return false;

        order = found;
        return true;
    }

    private static LuaMBehaviorStimulus StimulusFor(LuaMBehaviorIntent intent)
    {
        return intent switch
        {
            LuaMBehaviorIntent.Treat => LuaMBehaviorStimulus.AllyCritical,
            LuaMBehaviorIntent.Revive => LuaMBehaviorStimulus.RecoverableDeadAlly,
            LuaMBehaviorIntent.Rescue or LuaMBehaviorIntent.EvacuateCasualty =>
                LuaMBehaviorStimulus.PatientWaiting,
            LuaMBehaviorIntent.ExtinguishFire => LuaMBehaviorStimulus.FireNeedsExtinguishing,
            LuaMBehaviorIntent.Clean => LuaMBehaviorStimulus.CleaningNeeded,
            LuaMBehaviorIntent.SealBreach => LuaMBehaviorStimulus.BreachNeedsSealing,
            LuaMBehaviorIntent.Repair => LuaMBehaviorStimulus.RepairNeeded,
            LuaMBehaviorIntent.RestorePower => LuaMBehaviorStimulus.DeviceNeedsPower,
            LuaMBehaviorIntent.Deliver or LuaMBehaviorIntent.Haul => LuaMBehaviorStimulus.DeliveryReady,
            LuaMBehaviorIntent.Mine => LuaMBehaviorStimulus.MineableResource,
            LuaMBehaviorIntent.Salvage => LuaMBehaviorStimulus.SalvageTarget,
            LuaMBehaviorIntent.Patrol => LuaMBehaviorStimulus.PatrolDue,
            LuaMBehaviorIntent.Escort => LuaMBehaviorStimulus.EscortRequired,
            LuaMBehaviorIntent.Investigate => LuaMBehaviorStimulus.InvestigationTarget,
            LuaMBehaviorIntent.DefendArea or LuaMBehaviorIntent.ProtectTarget =>
                LuaMBehaviorStimulus.ObjectiveThreatened,
            LuaMBehaviorIntent.FollowOrder => LuaMBehaviorStimulus.DirectOrder,
            _ => LuaMBehaviorStimulus.WorkOrder,
        };
    }

    private static string SourceFor(EntityUid boardUid, uint orderId)
    {
        return $"work-order:{boardUid.Id}:{orderId}";
    }

    private static TimeSpan ClampLease(TimeSpan lease)
    {
        if (lease < MinimumLease)
            return MinimumLease;
        return lease > MaximumLease ? MaximumLease : lease;
    }

    private static TimeSpan CalculateFailureBackoff(int failures)
    {
        var ticks = MinimumFailureBackoff.Ticks;
        for (var i = 1; i < Math.Max(1, failures) && ticks < MaximumFailureBackoff.Ticks; i++)
        {
            ticks = Math.Min(
                MaximumFailureBackoff.Ticks,
                ticks > MaximumFailureBackoff.Ticks / 2 ? MaximumFailureBackoff.Ticks : ticks * 2);
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static uint AllocateId(LuaMWorkOrderBoardComponent board)
    {
        for (var i = 0; i < int.MaxValue; i++)
        {
            var candidate = board.NextOrderId++;
            if (candidate == 0)
                continue;
            if (board.Orders.All(order => order.Id != candidate))
                return candidate;
        }

        throw new InvalidOperationException("LuaM work-order id space exhausted");
    }

    private static TimeSpan AddDuration(TimeSpan now, TimeSpan duration)
    {
        var ticks = Math.Min(
            TimeSpan.MaxValue.Ticks - Math.Max(0L, now.Ticks),
            Math.Max(0L, duration.Ticks));
        return TimeSpan.FromTicks(Math.Max(0L, now.Ticks) + ticks);
    }
}
