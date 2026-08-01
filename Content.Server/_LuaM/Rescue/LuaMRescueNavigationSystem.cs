using System.Numerics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Interaction;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

/// <summary>
/// Performs and caches authoritative path queries for rescue activities.
/// Target selection never treats a missing polygon, a different grid, or NoPath as a
/// merely less attractive route. Moving targets automatically invalidate the probe.
/// </summary>
public sealed class LuaMRescueNavigationSystem : EntitySystem
{
    private static readonly TimeSpan SuccessfulProbeLifetime = TimeSpan.FromSeconds(3);
    private const float ReprobeMovementSquared = 0.25f;

    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly object _routeLock = new();
    private readonly Dictionary<(EntityUid Agent, EntityUid Target, float ActionRange, bool DockedCrossGrid), RouteProbe>
        _routeProbes = new();

    public override void Shutdown()
    {
        lock (_routeLock)
        {
            foreach (var probe in _routeProbes.Values)
            {
                probe.Cancellation.Cancel();
                probe.Cancellation.Dispose();
            }

            _routeProbes.Clear();
        }

        base.Shutdown();
    }

    public LuaMRescuePathProbeSnapshot ProbeRoute(
        EntityUid agent,
        EntityUid target,
        float actionRange,
        bool allowConfirmedDockedCrossGrid = false)
    {
        if (TerminatingOrDeleted(agent) || TerminatingOrDeleted(target))
            return new(LuaMRescuePathProbeState.Invalid, 0f, false);

        var agentXform = Transform(agent);
        var targetXform = Transform(target);
        if (agentXform.MapID != targetXform.MapID)
            return new(LuaMRescuePathProbeState.DifferentGrid, float.PositiveInfinity, false);

        var directDistance = (agentXform.MapPosition.Position - targetXform.MapPosition.Position).Length();

        // A small map-space distance does not make two independent grids traversable.
        // Only callers that have already confirmed a safe dock may bridge grid graphs.
        if (agentXform.GridUid != targetXform.GridUid && !allowConfirmedDockedCrossGrid)
            return new(LuaMRescuePathProbeState.DifferentGrid, directDistance, false);

        actionRange = Math.Max(0.05f, actionRange);
        var closeObstructed = false;
        if (directDistance <= actionRange)
        {
            // Distance alone is not sufficient for an interaction: a wall may separate
            // two entities whose centres are less than the requested action range apart.
            // Return the precise terminal reason instead of reporting a false arrival.
            var unobstructed = _interaction.InRangeUnobstructed(agent, target, actionRange);
            if (unobstructed)
            {
                CancelRoute(agent, target);
                return new(LuaMRescuePathProbeState.Reachable, directDistance, false);
            }

            // Being geometrically close behind a door or thin wall is not a
            // terminal answer: there may be an access-authorized door or a short
            // route around it. Query a closer approach point and only report
            // NoLineOfSight if that authoritative query also fails.
            closeObstructed = true;
        }

        var key = (agent, target, actionRange, allowConfirmedDockedCrossGrid);
        var now = _timing.CurTime;
        // Cache movement in map space, not in the entity's parent/grid-local
        // coordinates. A patient standing still inside a moving shuttle keeps
        // identical local coordinates even though the old path endpoint has
        // moved in the world.
        var agentPosition = agentXform.MapPosition.Position;
        var targetPosition = targetXform.MapPosition.Position;
        var agentGrid = agentXform.GridUid;
        var targetGrid = targetXform.GridUid;

        lock (_routeLock)
        {
            var consecutiveFailures = 0;
            LuaMRescuePathProbeSnapshot? staleReachable = null;
            if (_routeProbes.TryGetValue(key, out var existing))
            {
                var agentMoved = existing.AgentGrid != agentGrid ||
                    Vector2.DistanceSquared(existing.AgentPosition, agentPosition) > ReprobeMovementSquared;
                var targetMoved = existing.TargetGrid != targetGrid ||
                    Vector2.DistanceSquared(existing.TargetPosition, targetPosition) > ReprobeMovementSquared;
                var moved = agentMoved || targetMoved;

                if (!moved && existing.ExpiresAt > now)
                    return VisibleSnapshot(existing, directDistance);

                // Revalidating a previously confirmed route because the rescuer
                // advanced, or because its short cache lifetime elapsed, must not
                // turn ordinary progress into a stop. Keep the last Reachable
                // result visible while the replacement query runs. A moving
                // patient still invalidates the route synchronously.
                if (!targetMoved)
                {
                    if (existing.Snapshot.State == LuaMRescuePathProbeState.Reachable)
                        staleReachable = existing.Snapshot with { Distance = directDistance };
                    else if (existing.Snapshot.State == LuaMRescuePathProbeState.Pending &&
                             existing.ExpiresAt > now)
                        staleReachable = existing.StaleReachable;

                    // Do not starve an in-flight background query by restarting it
                    // for every additional half-metre of rescuer movement.
                    if (existing.Snapshot.State == LuaMRescuePathProbeState.Pending &&
                        staleReachable != null &&
                        existing.ExpiresAt > now)
                    {
                        return staleReachable.Value with
                        {
                            Distance = directDistance,
                            RequiresCloserApproach = existing.CloseObstructed,
                        };
                    }
                }

                if (!moved)
                    consecutiveFailures = existing.ConsecutiveFailures;

                existing.Cancellation.Cancel();
                existing.Cancellation.Dispose();
                _routeProbes.Remove(key);
            }

            var cancellation = new CancellationTokenSource();
            var probe = new RouteProbe(
                agentPosition,
                targetPosition,
                agentGrid,
                targetGrid,
                now + SuccessfulProbeLifetime,
                cancellation,
                consecutiveFailures,
                new LuaMRescuePathProbeSnapshot(
                    LuaMRescuePathProbeState.Pending,
                    directDistance,
                    false,
                    consecutiveFailures + 1,
                    closeObstructed),
                staleReachable);
            _routeProbes[key] = probe;

            _ = CompleteProbeAsync(
                key,
                probe,
                agent,
                agentXform.Coordinates,
                targetXform.Coordinates,
                closeObstructed ? Math.Min(0.25f, actionRange) : actionRange,
                _pathfinding.GetFlags(agent));
            return VisibleSnapshot(probe, directDistance);
        }
    }

    public void CancelRoute(EntityUid agent, EntityUid? target = null)
    {
        lock (_routeLock)
        {
            foreach (var key in _routeProbes.Keys
                         .Where(key => key.Agent == agent && (target == null || key.Target == target))
                         .ToArray())
            {
                var probe = _routeProbes[key];
                probe.Cancellation.Cancel();
                probe.Cancellation.Dispose();
                _routeProbes.Remove(key);
            }
        }
    }

    private async Task CompleteProbeAsync(
        (EntityUid Agent, EntityUid Target, float ActionRange, bool DockedCrossGrid) key,
        RouteProbe expected,
        EntityUid agent,
        EntityCoordinates start,
        EntityCoordinates end,
        float actionRange,
        PathFlags flags)
    {
        PathResultEvent path;
        try
        {
            path = await _pathfinding.GetPath(
                agent,
                start,
                end,
                actionRange,
                expected.Cancellation.Token,
                flags);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var requiresAccess = path.Path.Any(poly =>
            (poly.Data.Flags & PathfindingBreadcrumbFlag.Access) != 0x0);
        var state = path.Result == PathResult.Path
            ? LuaMRescuePathProbeState.Reachable
            : path.AccessDenied
                ? LuaMRescuePathProbeState.AccessDenied
                : expected.CloseObstructed
                    ? LuaMRescuePathProbeState.NoLineOfSight
                    : LuaMRescuePathProbeState.NoPath;
        lock (_routeLock)
        {
            if (!_routeProbes.TryGetValue(key, out var current) || !ReferenceEquals(current, expected))
                return;

            TimeSpan lifetime;
            int attemptCount;
            if (state == LuaMRescuePathProbeState.Reachable)
            {
                current.ConsecutiveFailures = 0;
                lifetime = SuccessfulProbeLifetime;
                attemptCount = 0;
            }
            else
            {
                current.ConsecutiveFailures++;
                lifetime = GetFailedProbeLifetime(current.ConsecutiveFailures);
                attemptCount = current.ConsecutiveFailures;
            }

            current.Snapshot = new LuaMRescuePathProbeSnapshot(
                state,
                current.Snapshot.Distance,
                requiresAccess,
                attemptCount,
                current.CloseObstructed);
            current.StaleReachable = null;
            current.ExpiresAt = _timing.CurTime + lifetime;
        }
    }

    private static LuaMRescuePathProbeSnapshot VisibleSnapshot(RouteProbe probe, float directDistance)
    {
        if (probe.Snapshot.State != LuaMRescuePathProbeState.Pending ||
            probe.StaleReachable is not { } stale)
        {
            return probe.Snapshot;
        }

        return stale with
        {
            Distance = directDistance,
            RequiresCloserApproach = probe.CloseObstructed,
        };
    }

    private static TimeSpan GetFailedProbeLifetime(int consecutiveFailures)
    {
        var seconds = consecutiveFailures switch
        {
            <= 1 => 1,
            2 => 2,
            3 => 4,
            4 => 8,
            5 => 16,
            _ => 30,
        };

        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class RouteProbe(
        Vector2 agentPosition,
        Vector2 targetPosition,
        EntityUid? agentGrid,
        EntityUid? targetGrid,
        TimeSpan expiresAt,
        CancellationTokenSource cancellation,
        int consecutiveFailures,
        LuaMRescuePathProbeSnapshot snapshot,
        LuaMRescuePathProbeSnapshot? staleReachable = null)
    {
        public readonly Vector2 AgentPosition = agentPosition;
        public readonly Vector2 TargetPosition = targetPosition;
        public readonly EntityUid? AgentGrid = agentGrid;
        public readonly EntityUid? TargetGrid = targetGrid;
        public TimeSpan ExpiresAt = expiresAt;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public int ConsecutiveFailures = consecutiveFailures;
        public LuaMRescuePathProbeSnapshot Snapshot = snapshot;
        public LuaMRescuePathProbeSnapshot? StaleReachable = staleReachable;
        public readonly bool CloseObstructed = snapshot.RequiresCloserApproach;
    }
}

public enum LuaMRescuePathProbeState : byte
{
    Pending,
    Reachable,
    NoPath,
    AccessDenied,
    NoLineOfSight,
    DifferentGrid,
    Invalid,
}

public readonly record struct LuaMRescuePathProbeSnapshot(
    LuaMRescuePathProbeState State,
    float Distance,
    bool RequiresAccess,
    int AttemptCount = 0,
    bool RequiresCloserApproach = false);
