using System;
using System.Numerics;
using Content.Server._LuaM.AI;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
public sealed partial class LuaMLogisticsShipBehaviorCoreComponent : Component
{
    [DataField(required: true)]
    public EntityUid Ship;
}

/// <summary>
/// Gives physical logistics grids one Mono ship-AI core and translates their
/// delivery state into the common civilian-ship behavior profile.
/// </summary>
public sealed partial class LuaMAiLogisticsShipBehaviorAdapterSystem : EntitySystem
{
    private const string CivilianCorePrototype = "LuaMAdaptiveCivilianShipAiCore";
    private const string ObservationSource = "logistics-ship:domain";
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ObservationTtl = TimeSpan.FromSeconds(4);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMLogisticsShipBehaviorCoreComponent, ComponentShutdown>(OnCoreShutdown);
        SubscribeLocalEvent<LuaMLogisticsShipBehaviorCoreComponent, LuaMBehaviorDecisionChangedEvent>(OnDecisionChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMAiLogisticsShipComponent>();
        while (query.MoveNext(out var uid, out var ship))
        {
            if (ship.NextBehaviorEvaluation != TimeSpan.Zero && now < ship.NextBehaviorEvaluation)
                continue;

            RefreshNow(uid, ship);
        }
    }

    public bool RefreshNow(EntityUid uid, LuaMAiLogisticsShipComponent ship, bool force = false)
    {
        var now = _timing.CurTime;
        if (!force && ship.NextBehaviorEvaluation != TimeSpan.Zero && now < ship.NextBehaviorEvaluation)
            return true;

        ship.NextBehaviorEvaluation = now + EvaluationInterval;
        if (!TryEnsureBehaviorCore(uid, ship, out var core))
            return false;

        _behavior.ClearObservations(core, source: ObservationSource);
        var destination = ValidTarget(ship.BehaviorDestination) ?? FindBaseAnchor(uid, ship.BaseId);
        if (destination is { } destinationUid)
        {
            ship.BehaviorDestination = destinationUid;
            ship.LastBehaviorDestination = Transform(destinationUid).Coordinates;
        }

        if (ship.RouteBlocked)
        {
            Report(
                core,
                LuaMBehaviorStimulus.NoPath,
                1f,
                destination,
                ship.LastBehaviorDestination);
        }
        else if (destination != null)
        {
            Report(
                core,
                LuaMBehaviorStimulus.DeliveryReady,
                0.8f,
                destination,
                ship.LastBehaviorDestination);
        }
        else
        {
            Report(core, LuaMBehaviorStimulus.PatrolDue, 0.25f);
        }

        if (!_behavior.EvaluateNow(core, out var decision))
        {
            ship.LastBehaviorStatus = "behavior evaluation failed";
            return false;
        }

        ship.LastBehaviorStatus =
            $"{decision.Intent}/{decision.Tier} generation={decision.Generation}; {decision.Reason}";
        return true;
    }

    public bool TryEnsureBehaviorCore(
        EntityUid shipUid,
        LuaMAiLogisticsShipComponent ship,
        out EntityUid core)
    {
        if (ValidTarget(ship.BehaviorCore) is { } current &&
            HasComp<LuaMLogisticsShipBehaviorCoreComponent>(current))
        {
            core = current;
            return true;
        }

        var children = Transform(shipUid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (TryComp<LuaMLogisticsShipBehaviorCoreComponent>(child, out var existing) &&
                existing.Ship == shipUid)
            {
                ship.BehaviorCore = child;
                core = child;
                return true;
            }

            var prototype = MetaData(child).EntityPrototype?.ID;
            if (prototype != null &&
                (prototype.StartsWith("NpcStationAi", StringComparison.Ordinal) ||
                 prototype.StartsWith("LuaMAdaptiveCombatShipAi", StringComparison.Ordinal)))
            {
                ship.BehaviorState = "pilot_conflict";
                ship.LastBehaviorStatus = $"existing ship AI core {prototype} prevents civilian pilot spawn";
                core = EntityUid.Invalid;
                return false;
            }
        }

        core = Spawn(CivilianCorePrototype, new EntityCoordinates(shipUid, Vector2.Zero));
        var marker = EnsureComp<LuaMLogisticsShipBehaviorCoreComponent>(core);
        marker.Ship = shipUid;
        ship.BehaviorCore = core;
        ship.BehaviorState = "standby";
        ship.LastBehaviorStatus = $"civilian behavior core {core} created";
        return true;
    }

    private void OnDecisionChanged(
        EntityUid uid,
        LuaMLogisticsShipBehaviorCoreComponent marker,
        ref LuaMBehaviorDecisionChangedEvent args)
    {
        if (!TryComp<LuaMAiLogisticsShipComponent>(marker.Ship, out var ship))
            return;

        ship.BehaviorState = args.Current.Intent switch
        {
            LuaMBehaviorIntent.AwaitRescue or LuaMBehaviorIntent.HoldPosition => "emergency_hold",
            LuaMBehaviorIntent.DisengageShip or
                LuaMBehaviorIntent.Retreat or
                LuaMBehaviorIntent.EvadeProjectile => "disengaging",
            LuaMBehaviorIntent.ReplanRoute or LuaMBehaviorIntent.ClearRoute => "replanning_route",
            LuaMBehaviorIntent.Deliver or
                LuaMBehaviorIntent.Navigate or
                LuaMBehaviorIntent.FollowOrder => "delivering",
            LuaMBehaviorIntent.Dock => "docking",
            LuaMBehaviorIntent.Patrol => "patrol",
            LuaMBehaviorIntent.Standby => "standby",
            _ => ship.BehaviorState,
        };

        if (args.Current.Intent is LuaMBehaviorIntent.Deliver or
            LuaMBehaviorIntent.Navigate or
            LuaMBehaviorIntent.FollowOrder or
            LuaMBehaviorIntent.Dock)
        {
            if (ValidTarget(args.Current.Target) is { } target)
                ship.BehaviorDestination = target;
            ship.LastBehaviorDestination = args.Current.Destination ?? ship.LastBehaviorDestination;
        }

        ship.LastBehaviorStatus =
            $"{args.Current.Intent}/{args.Current.Tier} generation={args.Current.Generation}; executor={ship.BehaviorState}";
    }

    private void OnCoreShutdown(
        EntityUid uid,
        LuaMLogisticsShipBehaviorCoreComponent marker,
        ComponentShutdown args)
    {
        if (!TryComp<LuaMAiLogisticsShipComponent>(marker.Ship, out var ship) ||
            ship.BehaviorCore != uid)
        {
            return;
        }

        ship.BehaviorCore = null;
        ship.BehaviorState = "pilot_lost";
        ship.LastBehaviorStatus = "civilian behavior core was removed";
    }

    private EntityUid? FindBaseAnchor(EntityUid shipUid, string baseId)
    {
        var source = _transform.ToMapCoordinates(Transform(shipUid).Coordinates, logError: false);
        if (source == MapCoordinates.Nullspace)
            return null;

        EntityUid? nearest = null;
        var nearestDistance = float.MaxValue;
        var query = EntityQueryEnumerator<LuaMAiBaseAnchorComponent>();
        while (query.MoveNext(out var uid, out var anchor))
        {
            if (!string.Equals(anchor.BaseId, baseId, StringComparison.OrdinalIgnoreCase))
                continue;

            var coordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
            if (coordinates.MapId != source.MapId)
                continue;

            var distance = Vector2.DistanceSquared(source.Position, coordinates.Position);
            if (distance >= nearestDistance)
                continue;

            nearest = uid;
            nearestDistance = distance;
        }

        return nearest;
    }

    private EntityUid? ValidTarget(EntityUid? target)
    {
        return target is { Valid: true } uid && !TerminatingOrDeleted(uid)
            ? uid
            : null;
    }

    private void Report(
        EntityUid core,
        LuaMBehaviorStimulus stimulus,
        float severity,
        EntityUid? target = null,
        EntityCoordinates? destination = null)
    {
        _behavior.ReportObservation(
            core,
            stimulus,
            severity,
            target: target,
            destination: destination,
            ttl: ObservationTtl,
            source: ObservationSource);
    }
}
